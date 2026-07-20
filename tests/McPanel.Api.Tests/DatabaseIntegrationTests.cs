using McPanel.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class DatabaseIntegrationTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "mcpanel-state-" + Guid.NewGuid().ToString("N") + ".db");

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
            stamp = admin.SessionStamp;
        }
        await using (var db = new StateDbContext(options))
        {
            await db.EnsureCompatibleSchemaAsync();
            Assert.Equal(stamp, (await db.Admins.AsNoTracking().SingleAsync()).SessionStamp);
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (File.Exists(_file)) File.Delete(_file); }
}
