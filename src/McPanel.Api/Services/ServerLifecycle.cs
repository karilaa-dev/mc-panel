using McPanel.Api.Data;
using McPanel.Api.Infrastructure;

namespace McPanel.Api.Services;

internal static class ServerLifecycle
{
    public static bool IsStable(ServerState state, bool processRunning) => state switch
    {
        ServerState.Running => processRunning,
        ServerState.Stopped or ServerState.Crashed => !processRunning,
        _ => false
    };

    public static void RequireStartable(ServerEntity server, bool processRunning)
    {
        if (processRunning || server.State is ServerState.Running or ServerState.Starting or ServerState.Stopping)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server already has a managed process.");
        if (server.RecoveryRequired || server.State is not (ServerState.Stopped or ServerState.Crashed))
            throw PanelProblems.Conflict("SERVER_BUSY", "The server cannot be started in its current state.");
    }

    public static void BeginStart(ServerEntity server, bool recovery)
    {
        server.State = ServerState.Starting;
        server.ProcessId = null;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        if (!recovery) server.CrashAttempts = 0;
    }

    public static void StartFailed(ServerEntity server)
    {
        server.State = ServerState.Crashed;
        server.ProcessId = null;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void AcceptRuntimeStart(ServerEntity server, RuntimeServerSnapshot snapshot)
    {
        server.State = snapshot.State == RuntimeProcessState.Running ? ServerState.Running : ServerState.Starting;
        server.ProcessId = snapshot.ProcessId;
        server.StartedAt = snapshot.StartedAt;
        server.RestartRequired = false;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static ServerState Observe(ServerEntity server, RuntimeProcessState observed, bool preserveOperation) =>
        server.RecoveryRequired ? ServerState.Error : observed switch
        {
            RuntimeProcessState.Starting => ServerState.Starting,
            RuntimeProcessState.Running or RuntimeProcessState.Stopped when preserveOperation && server.State is ServerState.BackingUp or ServerState.Updating => server.State,
            RuntimeProcessState.Running => ServerState.Running,
            RuntimeProcessState.Stopping => ServerState.Stopping,
            RuntimeProcessState.Crashed => ServerState.Crashed,
            _ => ServerState.Stopped
        };
}
