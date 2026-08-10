using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class DatabaseIntegrationTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "mcpanel-state-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _consoleFile = Path.Combine(Path.GetTempPath(), "mcpanel-console-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "mcpanel-data-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sqlite_persists_enum_state_and_single_player_identity()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        var serverId = Guid.NewGuid();
        await using (var db = new StateDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new ServerEntity { Id = serverId, Name = "Test", Kind = ServerKind.Paper, Version = "1.21.11", JavaRuntimeId = "java", EulaAcceptedAt = DateTimeOffset.UtcNow, State = ServerState.Stopped });
            db.Players.Add(new PlayerEntity { ServerId = serverId, Name = "Steve", Online = true });
            await db.SaveChangesAsync();
        }
        await using (var db = new StateDbContext(options))
        {
            Assert.Equal(ServerState.Stopped, (await db.Servers.SingleAsync()).State);
            Assert.True((await db.Players.SingleAsync()).Online);
        }
    }

    [Fact]
    public async Task Existing_database_gains_a_persistent_session_stamp_without_losing_the_admin()
    {
        await using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Admins" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Admins" PRIMARY KEY,
                    "Username" TEXT NOT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL
                );
                INSERT INTO "Admins" ("Id", "Username", "PasswordHash", "CreatedAt")
                VALUES (1, 'legacy-admin', 'legacy-password-hash', 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        string stamp;
        await using (var db = new StateDbContext(options))
        {
            await db.EnsureCompatibleSchemaAsync();
            var admin = await db.Admins.AsNoTracking().SingleAsync();
            Assert.Equal("legacy-admin", admin.Username);
            Assert.Equal("legacy-password-hash", admin.PasswordHash);
            Assert.Matches("^[0-9a-f]{32}$", admin.SessionStamp);
            Assert.True(admin.KeepServersRunningOnPanelStop);
            Assert.Equal(0, admin.LastConsoleSequence);
            stamp = admin.SessionStamp;
        }
        await using (var db = new StateDbContext(options))
        {
            await db.EnsureCompatibleSchemaAsync();
            Assert.Equal(stamp, (await db.Admins.AsNoTracking().SingleAsync()).SessionStamp);
        }
    }

    [Fact]
    public async Task Existing_servers_gain_runtime_columns_idempotently_and_xms_matches_xmx()
    {
        await using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Admins" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Admins" PRIMARY KEY,
                    "Username" TEXT NOT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "SessionStamp" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL
                );
                CREATE TABLE "Servers" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Servers" PRIMARY KEY,
                    "MemoryMb" INTEGER NOT NULL,
                    "FabricLoaderVersion" TEXT NULL,
                    "FabricInstallerVersion" TEXT NULL,
                    "ExecutableJar" TEXT NOT NULL
                );
                INSERT INTO "Servers" ("Id", "MemoryMb", "FabricLoaderVersion", "FabricInstallerVersion", "ExecutableJar")
                VALUES ('00000000-0000-0000-0000-000000000001', 6144, '0.16.14', '1.0.3', 'fabric-server-launch.jar');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        await using (var db = new StateDbContext(options))
        {
            await db.EnsureCompatibleSchemaAsync();
            await db.EnsureCompatibleSchemaAsync();
        }

        await using var verify = new SqliteConnection($"Data Source={_file}");
        await verify.OpenAsync();
        await using var select = verify.CreateCommand();
        select.CommandText = "SELECT \"InitialMemoryMb\", \"UseAikarFlags\", \"MemoryLimitMb\", \"LoaderVersion\", \"InstallerVersion\", \"LaunchMode\", \"LaunchTarget\", \"PublicHost\" FROM \"Servers\" LIMIT 1;";
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6144, reader.GetInt32(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal(7680, reader.GetInt32(2));
        Assert.Equal("0.16.14", reader.GetString(3));
        Assert.Equal("1.0.3", reader.GetString(4));
        Assert.Equal("Jar", reader.GetString(5));
        Assert.Equal("fabric-server-launch.jar", reader.GetString(6));
        Assert.True(reader.IsDBNull(7));
        await reader.DisposeAsync();
        select.CommandText = "SELECT \"Mode\", \"PublicPort\", \"ClassicForwardingMode\", length(\"Revision\") FROM \"ProxySettings\" WHERE \"Id\" = 1;";
        await using var proxy = await select.ExecuteReaderAsync();
        Assert.True(await proxy.ReadAsync());
        Assert.Equal("Lite", proxy.GetString(0));
        Assert.Equal(25565, proxy.GetInt32(1));
        Assert.Equal("Velocity", proxy.GetString(2));
        Assert.Equal(32, proxy.GetInt32(3));
        await proxy.DisposeAsync();
        select.CommandText = "SELECT COUNT(*) FROM pragma_table_info('GateSettings') WHERE name = 'DefaultExternalBackendId';";
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync())!);
        select.CommandText = "SELECT COUNT(*) FROM pragma_table_info('GateSettings') WHERE name = 'ClassicConfigJson';";
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync())!);
        select.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'GateExternalBackends';";
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Legacy_required_executable_jar_column_does_not_block_new_server_inserts()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        await using (var db = new StateDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Servers\" ADD COLUMN \"ExecutableJar\" TEXT NOT NULL;");
            await db.EnsureCompatibleSchemaAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = Guid.NewGuid(),
                Name = "New server",
                Kind = ServerKind.Paper,
                Version = "1.21.8",
                JavaRuntimeId = "java-21",
                EulaAcceptedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            Assert.Equal("New server", (await db.Servers.SingleAsync()).Name);
        }

        await using var verify = new SqliteConnection($"Data Source={_file}");
        await verify.OpenAsync();
        await using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name = 'ExecutableJar';";
        Assert.Equal(0L, (long)(await columns.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Existing_lowercase_runtime_console_ids_are_made_queryable_by_ef()
    {
        var id = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConsoleDbContext>().UseSqlite($"Data Source={_consoleFile}").Options;
        await using (var db = new ConsoleDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Lines" ("ServerId", "Timestamp", "Stream", "Level", "Text")
                VALUES ({id.ToString()}, 0, 'stdout', 'info', 'persisted while panel was down');
                """);
            Assert.Empty(await db.Lines.Where(x => x.ServerId == id).ToListAsync());
            await db.EnsureCompatibleSchemaAsync();
        }
        await using (var db = new ConsoleDbContext(options))
            Assert.Single(await db.Lines.Where(x => x.ServerId == id).ToListAsync());
    }

    [Fact]
    public async Task Legacy_advertised_hosts_preserve_the_singleton_effective_port()
    {
        await using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Admins" ("Id" INTEGER NOT NULL PRIMARY KEY, "Username" TEXT NOT NULL, "PasswordHash" TEXT NOT NULL, "CreatedAt" INTEGER NOT NULL);
                CREATE TABLE "Servers" ("Id" TEXT NOT NULL PRIMARY KEY, "MemoryMb" INTEGER NOT NULL, "PublicHost" TEXT NULL);
                CREATE TABLE "ProxySettings" (
                    "Id" INTEGER NOT NULL PRIMARY KEY, "Mode" TEXT NOT NULL, "GlobalPublicHost" TEXT NULL,
                    "PublicPort" INTEGER NOT NULL, "DefaultServerId" TEXT NULL, "ClassicForwardingMode" TEXT NOT NULL,
                    "BackendSetupAcknowledgementHash" TEXT NULL, "ApiPort" INTEGER NOT NULL, "Revision" TEXT NOT NULL, "UpdatedAt" INTEGER NOT NULL);
                INSERT INTO "Servers" ("Id", "MemoryMb", "PublicHost") VALUES ('00000000-0000-0000-0000-000000000001', 2048, 'PLAY.EXAMPLE.COM');
                INSERT INTO "ProxySettings" VALUES (1, 'Lite', 'network.example.com', 25570, NULL, 'Velocity', NULL, 18080, 'revision', 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        await using var db = new StateDbContext(options);
        await db.EnsureCompatibleSchemaAsync();
        await using var verify = new SqliteConnection($"Data Source={_file}");
        await verify.OpenAsync();
        await using var select = verify.CreateCommand();
        select.CommandText = "SELECT \"PublicHost\", \"PublicPort\" FROM \"Servers\" LIMIT 1;";
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("PLAY.EXAMPLE.COM", reader.GetString(0));
        Assert.Equal(25570, reader.GetInt32(1));
        Assert.Equal("network.example.com", (await db.PanelSettings.AsNoTracking().SingleAsync()).GlobalServerHost);
    }

    [Fact]
    public async Task Complete_legacy_Gate_installation_becomes_one_real_server_and_default_state_does_not()
    {
        var paths = new PanelPaths(new PanelOptions { DataDirectory = _dataRoot, ConfigDirectory = Path.Combine(_dataRoot, "config") });
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_file}").Options;
        await using var db = new StateDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.EnsureCompatibleSchemaAsync();
        var backendId = Guid.NewGuid();
        db.Servers.Add(new ServerEntity { Id = backendId, Name = "Lobby", Kind = ServerKind.Paper, Version = "1.21.8", JavaRuntimeId = "java", State = ServerState.Stopped });
        var proxy = await db.ProxySettings.SingleAsync();
        proxy.GlobalPublicHost = "play.example.com";
        proxy.PublicPort = 25570;
        proxy.DefaultServerId = backendId;
        proxy.ApiPort = 18080;
        await db.SaveChangesAsync();

        var migration = new LegacyGateMigrationService(paths, NullLogger<LegacyGateMigrationService>.Instance);
        await migration.MigrateAsync(db, CancellationToken.None);
        Assert.Empty(await db.Servers.Where(x => x.Kind == ServerKind.Gate).ToListAsync());

        var versionDirectory = Path.Combine(paths.LegacyGateVersions, "0.65.0");
        Directory.CreateDirectory(versionDirectory);
        var executable = Path.Combine(versionDirectory, "gate");
        await File.WriteAllTextAsync(executable, "verified legacy binary");
        await File.WriteAllTextAsync(paths.LegacyGateConfig, "{}");
        await File.WriteAllTextAsync(paths.LegacyGateInstallManifest, JsonSerializer.Serialize(
            new GateInstallManifest("0.65.0", executable, new string('a', 64), null, DateTimeOffset.UtcNow), GateReleaseService.JsonOptions));
        await File.WriteAllTextAsync(paths.LegacyGateVelocitySecret, "secret");
        await File.WriteAllTextAsync(paths.GateDesiredState, "{\"desiredRunning\":true}");

        await migration.MigrateAsync(db, CancellationToken.None);
        var gate = await db.Servers.SingleAsync(x => x.Kind == ServerKind.Gate);
        Assert.Equal(25570, gate.Port);
        Assert.Equal(256, gate.MemoryLimitMb);
        Assert.True(gate.StartOnBoot);
        var settings = await db.GateSettings.SingleAsync(x => x.ServerId == gate.Id);
        Assert.Equal(backendId, settings.DefaultBackendServerId);
        Assert.Equal(18080, settings.ApiPort);
        Assert.Contains(await db.GateBackends.ToListAsync(), x => x.GateServerId == gate.Id && x.BackendServerId == backendId);
        Assert.True(File.Exists(paths.GateInstallManifest(gate.Id)));
        Assert.True(File.Exists(paths.GateVelocitySecret(gate.Id)));
        Assert.False(File.Exists(paths.LegacyGateInstallManifest));
        Assert.False(File.Exists(paths.LegacyGateVelocitySecret));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
        if (File.Exists(_consoleFile)) File.Delete(_consoleFile);
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, true);
    }
}
