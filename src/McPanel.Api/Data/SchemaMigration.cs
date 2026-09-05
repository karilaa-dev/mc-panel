using Microsoft.Data.Sqlite;

namespace McPanel.Api.Data;

/// <summary>Explicit, additive SQLite migrations. The console schema remains compatible with runtime protocol 1.</summary>
public static class SchemaMigration
{
    public const int CurrentVersion = 2;
    public const int ConsoleVersion = 1;

    public static string Script(int version)
    {
        var assembly = typeof(SchemaMigration).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.Contains($".Migrations.{version:000}-", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    public static async Task<int> CheckAsync(string file, CancellationToken token = default)
    {
        if (!File.Exists(file)) return 0;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        return await InspectAsync(connection, token);
    }

    public static async Task CheckConsoleAsync(string file, CancellationToken token = default)
    {
        if (!File.Exists(file)) return;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        var actual = await SignatureAsync(connection, token);
        var expected = new[] { "Lines|Level|TEXT|1|0", "Lines|Sequence|INTEGER|1|1", "Lines|ServerId|TEXT|1|0", "Lines|Stream|TEXT|1|0", "Lines|Text|TEXT|1|0", "Lines|Timestamp|INTEGER|1|0" };
        if (actual.Count != 0 && !actual.SequenceEqual(expected))
            throw new InvalidDataException("The console database schema is incompatible with runtime protocol 1. Preserve it and use a compatible runtime; no changes were made.");
    }

    public static async Task MigrateAsync(string file, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        // Serialize panel startup and privileged maintenance. Never replace the lock inode.
        await using var migrationLock = new FileStream(file + ".migration-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        var version = await InspectAsync(connection, token);
        var recorded = await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__McPanelSchema';", token) != 0;
        if (version == CurrentVersion && recorded) return;
        if (version > 0)
        {
            var directory = Path.Combine(Path.GetDirectoryName(file)!, "schema-backups");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var snapshot = Path.Combine(directory, $"state-v{version}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.db");
            await SnapshotAsync(connection, snapshot, token);
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        for (var next = version + 1; next <= CurrentVersion; next++)
            await ExecuteAsync(connection, Script(next), token, transaction);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS __McPanelSchema (Version INTEGER NOT NULL PRIMARY KEY, AppliedAt TEXT NOT NULL);", token, transaction);
        for (var next = 1; next <= CurrentVersion; next++)
            await ExecuteAsync(connection, $"INSERT OR IGNORE INTO __McPanelSchema VALUES ({next}, strftime('%Y-%m-%dT%H:%M:%fZ','now'));", token, transaction);
        transaction.Commit();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", token);
    }

    public static async Task SnapshotAsync(SqliteConnection source, string destination, CancellationToken token)
    {
        if (File.Exists(destination)) throw new IOException("The database snapshot destination already exists.");
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        await target.OpenAsync(token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        token.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
        if (await TextAsync(target, "PRAGMA integrity_check;", token) != "ok") throw new InvalidDataException("The database snapshot failed its integrity check.");
    }

    private static async Task<int> InspectAsync(SqliteConnection connection, CancellationToken token)
    {
        var recorded = await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__McPanelSchema';", token) != 0;
        var version = recorded ? (int)await ScalarAsync(connection, "SELECT coalesce(max(Version),0) FROM __McPanelSchema;", token) : 0;
        if (version > CurrentVersion) throw new InvalidDataException($"Database schema {version} requires a newer MC Panel. This build supports up to {CurrentVersion}; preserve the database and use compatible binaries.");
        var actual = await SignatureAsync(connection, token);
        // Earlier installed releases retained these retired fields after their replacements
        // were introduced. Recognize their exact shape and preserve the dormant data.
        actual.Remove("Admins|KeepServersRunningOnPanelStop|INTEGER|1|0");
        var legacyProxy = new[]
        {
            "ProxySettings|ApiPort|INTEGER|1|0", "ProxySettings|BackendSetupAcknowledgementHash|TEXT|0|0",
            "ProxySettings|ClassicForwardingMode|TEXT|1|0", "ProxySettings|DefaultServerId|TEXT|0|0",
            "ProxySettings|GlobalPublicHost|TEXT|0|0", "ProxySettings|Id|INTEGER|1|1",
            "ProxySettings|Mode|TEXT|1|0", "ProxySettings|PublicPort|INTEGER|1|0",
            "ProxySettings|Revision|TEXT|1|0", "ProxySettings|UpdatedAt|INTEGER|1|0"
        };
        if (actual.Where(x => x.StartsWith("ProxySettings|", StringComparison.Ordinal)).SequenceEqual(legacyProxy))
            actual.RemoveAll(x => x.StartsWith("ProxySettings|", StringComparison.Ordinal));
        if (actual.Count == 0) return 0;
        await using var reference = new SqliteConnection("Data Source=:memory:");
        await reference.OpenAsync(token);
        for (var candidate = 1; candidate <= CurrentVersion; candidate++)
        {
            await ExecuteAsync(reference, Script(candidate), token);
            if ((version == 0 || version == candidate) && actual.SequenceEqual(await SignatureAsync(reference, token))) return candidate;
        }
        throw new InvalidDataException("Unrecognized panel database schema. No changes were made. Upgrade using a supported release migration; never delete the database to continue.");
    }

    private static async Task<List<string>> SignatureAsync(SqliteConnection connection, CancellationToken token)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        // Compare table columns rather than SQL formatting or generated default expressions.
        command.CommandText = "SELECT m.name,p.name,p.type,p.\"notnull\",p.pk FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type='table' AND m.name NOT LIKE 'sqlite_%' AND m.name <> '__McPanelSchema' ORDER BY m.name,p.name;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(string.Join('|', Enumerable.Range(0, 5).Select(reader.GetValue)));
        return result;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token) => long.Parse(await TextAsync(connection, sql, token));
    private static async Task<string> TextAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token);
    }
}
