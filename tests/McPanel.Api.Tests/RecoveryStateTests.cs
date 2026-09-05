using System.Reflection;
using McPanel.Api.Data;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class RecoveryStateTests
{
    [Fact]
    public void Stale_stopped_runtime_snapshot_cannot_clear_failed_recovery()
    {
        var server = new ServerEntity { Id = Guid.NewGuid(), Name = "Preserve world", Version = "1.21", JavaRuntimeId = "java", State = ServerState.Error, RecoveryRequired = true, StartOnBoot = true };
        var snapshot = new RuntimeServerSnapshot(server.Id, RuntimeProcessState.Stopped, null, null, DateTimeOffset.UtcNow, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, false, 0);
        typeof(ProcessSupervisor).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [server, snapshot, false]);
        Assert.Equal(ServerState.Error, server.State);
        Assert.True(server.RecoveryRequired);
    }
}
