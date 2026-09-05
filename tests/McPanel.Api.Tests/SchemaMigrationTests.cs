using McPanel.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-migration-" + Guid.NewGuid().ToString("N"));
    private string FileName => Path.Combine(_root, "state.db");
    public SchemaMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Console_schema_compatibility_is_checked_without_touching_existing_logs()
    {
        var file = Path.Combine(_root, "console.db");
        await using (var db = new ConsoleDbContext(new DbContextOptionsBuilder<ConsoleDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options))
        { await db.Database.EnsureCreatedAsync(); }
        await SchemaMigration.CheckConsoleAsync(file);
        await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        { await connection.OpenAsync(); await ExecuteAsync(connection, "ALTER TABLE Lines ADD COLUMN Unexpected TEXT NOT NULL DEFAULT 'future';"); }
        var before = await File.ReadAllBytesAsync(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaMigration.CheckConsoleAsync(file));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Installed_legacy_retired_fields_are_adopted_and_preserved()
    {
        await using var connection = new SqliteConnection($"Data Source={FileName};Pooling=False");
        await connection.OpenAsync(); await ExecuteAsync(connection, SchemaMigration.Script(1));
        await ExecuteAsync(connection, """
            ALTER TABLE Admins ADD COLUMN KeepServersRunningOnPanelStop INTEGER NOT NULL DEFAULT 1;
            CREATE TABLE ProxySettings (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Mode TEXT NOT NULL,
              GlobalPublicHost TEXT NULL, PublicPort INTEGER NOT NULL, DefaultServerId TEXT NULL,
              ClassicForwardingMode TEXT NOT NULL, BackendSetupAcknowledgementHash TEXT NULL,
              ApiPort INTEGER NOT NULL, Revision TEXT NOT NULL, UpdatedAt INTEGER NOT NULL);
            INSERT INTO ProxySettings VALUES (1,'Lite',NULL,25565,NULL,'Velocity',NULL,25566,'preserved',1234);
            """);
        await SchemaMigration.MigrateAsync(FileName);
        Assert.Equal(SchemaMigration.CurrentVersion, await SchemaMigration.CheckAsync(FileName));
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT Revision FROM ProxySettings;";
        Assert.Equal("preserved", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Populated_legacy_schema_is_adopted_without_losing_data_and_has_verified_backup()
    {
        await using (var connection = new SqliteConnection($"Data Source={FileName}"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, SchemaMigration.Script(1));
            await ExecuteAsync(connection, "INSERT INTO PanelSettings VALUES (1,1,NULL,'original',1234);");
        }
        Assert.Equal(1, await SchemaMigration.CheckAsync(FileName));
        await SchemaMigration.MigrateAsync(FileName);
        Assert.Equal(SchemaMigration.CurrentVersion, await SchemaMigration.CheckAsync(FileName));
        await using var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={FileName}").Options);
        Assert.Equal("original", (await db.PanelSettings.SingleAsync()).Revision);
        Assert.Empty(await db.Incidents.ToListAsync());
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "schema-backups"), "*.db"));
        Assert.Equal(1, await SchemaMigration.CheckAsync(backup));
        await SchemaMigration.MigrateAsync(FileName);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "schema-backups"), "*.db"));
    }

    [Fact]
    public async Task Unknown_or_newer_schema_is_rejected_without_changing_it()
    {
        await using (var connection = new SqliteConnection($"Data Source={FileName}"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE Precious (Value TEXT); INSERT INTO Precious VALUES ('preserve');");
        }
        var before = await File.ReadAllBytesAsync(FileName);
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaMigration.MigrateAsync(FileName));
        Assert.Equal(before, await File.ReadAllBytesAsync(FileName));
    }

    [Fact]
    public async Task Fresh_schema_and_ef_created_current_schema_are_both_supported()
    {
        await SchemaMigration.MigrateAsync(FileName);
        Assert.Equal(SchemaMigration.CurrentVersion, await SchemaMigration.CheckAsync(FileName));
        var efFile = Path.Combine(_root, "ef.db");
        await using (var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={efFile}").Options))
            await db.Database.EnsureCreatedAsync();
        await SchemaMigration.MigrateAsync(efFile);
        Assert.Equal(SchemaMigration.CurrentVersion, await SchemaMigration.CheckAsync(efFile));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
