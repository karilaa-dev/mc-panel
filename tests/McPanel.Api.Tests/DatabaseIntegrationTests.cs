using McPanel.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class DatabaseIntegrationTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"mcpanel-state-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Sqlite_persists_enum_state_and_single_player_identity()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={_file}").Options;
        var serverId = Guid.NewGuid();
        await using (var db = new StateDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Test",
                Kind = ServerKind.Paper,
                Version = "1.21.11",
                JavaRuntimeId = "java",
                EulaAcceptedAt = DateTimeOffset.UtcNow,
                State = ServerState.Stopped
            });
            db.Players.Add(new PlayerEntity { ServerId = serverId, Name = "Steve", Online = true });
            await db.SaveChangesAsync();
        }

        await using var reopened = new StateDbContext(options);
        Assert.Equal(ServerState.Stopped, (await reopened.Servers.SingleAsync()).State);
        Assert.True((await reopened.Players.SingleAsync()).Online);
    }

    [Fact]
    public async Task Fresh_database_contains_only_the_current_panel_schema()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={_file}").Options;
        await using (var db = new StateDbContext(options))
            await db.Database.EnsureCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={_file}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProxySettings';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('GateSettings') WHERE name IN ('DefaultExternalBackendId', 'ClassicConfigJson');";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Backups') WHERE name = 'SoftwareMetadataJson';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
    }
}
