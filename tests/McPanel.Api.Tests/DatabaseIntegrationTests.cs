using McPanel.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class DatabaseIntegrationTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "mcpanel-state-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _consoleFile = Path.Combine(Path.GetTempPath(), "mcpanel-console-" + Guid.NewGuid().ToString("N") + ".db");

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
        select.CommandText = "SELECT \"InitialMemoryMb\", \"UseAikarFlags\", \"MemoryLimitMb\", \"LoaderVersion\", \"InstallerVersion\", \"LaunchMode\", \"LaunchTarget\" FROM \"Servers\" LIMIT 1;";
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6144, reader.GetInt32(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal(7680, reader.GetInt32(2));
        Assert.Equal("0.16.14", reader.GetString(3));
        Assert.Equal("1.0.3", reader.GetString(4));
        Assert.Equal("Jar", reader.GetString(5));
        Assert.Equal("fabric-server-launch.jar", reader.GetString(6));
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
        if (File.Exists(_consoleFile)) File.Delete(_consoleFile);
    }
}
