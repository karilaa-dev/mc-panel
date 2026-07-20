using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed record ServerRuntimeMetrics(double CpuPercent, double MemoryUsedMb, long UptimeSeconds);

public interface IServerProcessStatus
{
    bool IsRunning(Guid id);
}

public sealed class ProcessSupervisor(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    ConsoleService console,
    AsyncKeyedLock keyedLock,
    JavaDiscoveryService javaDiscovery,
    OperationQueue operations,
    IOptions<PanelOptions> options,
    IHubContext<PanelHub> hub,
    SessionAudience audience,
    IHostApplicationLifetime lifetime,
    ILogger<ProcessSupervisor> logger) : BackgroundService, IServerProcessStatus
{
    public static IReadOnlyList<string> AikarFlags { get; } = new[]
    {
        "-XX:+UseG1GC",
        "-XX:+ParallelRefProcEnabled",
        "-XX:MaxGCPauseMillis=200",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+DisableExplicitGC",
        "-XX:+AlwaysPreTouch",
        "-XX:G1NewSizePercent=30",
        "-XX:G1MaxNewSizePercent=40",
        "-XX:G1HeapRegionSize=8M",
        "-XX:G1ReservePercent=20",
        "-XX:G1HeapWastePercent=5",
        "-XX:G1MixedGCCountTarget=4",
        "-XX:InitiatingHeapOccupancyPercent=15",
        "-XX:G1MixedGCLiveThresholdPercent=90",
        "-XX:G1RSetUpdatingPauseTimePercent=5",
        "-XX:SurvivorRatio=32",
        "-XX:+PerfDisableSharedMem",
        "-XX:MaxTenuringThreshold=1",
        "-Dusing.aikars.flags=https://mcflags.emc.gs",
        "-Daikars.new.flags=true"
    };
    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly SemaphoreSlim _memoryAdmission = new(1, 1);

    public Task<JobDto> QueueActionAsync(Guid id, string action, bool confirmKill, CancellationToken cancellationToken)
    {
        var normalized = action.ToLowerInvariant();
        if (normalized == "kill" && !confirmKill) throw PanelProblems.Validation("Emergency kill requires confirm=true.");
        return operations.EnqueueAsync(char.ToUpperInvariant(normalized[0]) + normalized[1..], id, async (_, _, token) =>
        {
            switch (normalized)
            {
                case "start": await StartAsync(id, false, token); break;
                case "stop": await StopAsync(id, token); break;
                case "restart": await RestartAsync(id, token); break;
                case "kill": await KillAsync(id, token); break;
                default: throw PanelProblems.Validation("Unknown server action.");
            }
        }, cancellationToken);
    }

    public async Task StartAsync(Guid id, bool recovery, CancellationToken cancellationToken)
    {
        StartAttempt attempt;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
            attempt = await BeginStartLockedAsync(id, recovery, cancellationToken);
        await CompleteStartAsync(id, attempt, cancellationToken);
    }

    private async Task<StartAttempt> BeginStartLockedAsync(Guid id, bool recovery, CancellationToken cancellationToken)
    {
        var readinessCursor = await console.LatestSequenceAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (_processes.ContainsKey(id) || server.State is ServerState.Running or ServerState.Starting or ServerState.Stopping)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server already has a managed process.");
        if (server.State is ServerState.Installing or ServerState.Updating or ServerState.BackingUp or ServerState.Error)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server cannot be started in its current state.");
        if (server.MemoryMb < PanelOptions.MinimumServerMemoryMb)
            throw new PanelException(409, "MEMORY_LIMIT_TOO_LOW", "The configured server memory is below the supported minimum.",
                $"Allocate at least {PanelOptions.MinimumServerMemoryMb} MiB before starting the server.");
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == server.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        var probed = await javaDiscovery.ProbeAsync(runtime.Path, runtime.IsCustom, cancellationToken);
        var required = server.RequiredJavaMajor > 0 ? server.RequiredJavaMajor : RequiredJava(server.Version);
        if (probed.Major < required)
            throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {required} or newer.");
        var jar = Path.Combine(paths.Instance(id), server.ExecutableJar);
        if (!File.Exists(jar)) throw new PanelException(409, "OPERATION_FAILED", "The server executable JAR is missing.");
        await _memoryAdmission.WaitAsync(cancellationToken);
        Process process;
        ManagedProcess managedProcess;
        try
        {
            var allocationMb = await db.Servers.Where(x => x.Id != id && (x.State == ServerState.Running || x.State == ServerState.Starting)).SumAsync(x => (long)x.MemoryMb, cancellationToken);
            var total = HostMetricsService.ReadMemory().Total;
            if ((allocationMb + server.MemoryMb) * 1024 * 1024 > total * options.Value.MemoryAllocationFraction)
                throw new PanelException(409, "MEMORY_LIMIT_EXCEEDED", "Starting this server would exceed the host memory allocation limit.");
            EnsurePortAvailable(server.Port);
            var start = BuildStartInfo(server, runtime.Path, jar);
            server.State = ServerState.Starting; server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
            if (!recovery) server.CrashAttempts = 0;
            await db.SaveChangesAsync(cancellationToken);
            await PublishStateAsync(server, cancellationToken);
            try { process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null."); }
            catch (Exception exception)
            {
                server.State = ServerState.Crashed; server.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken);
                throw new PanelException(500, "OPERATION_FAILED", "Java could not start.", exception.Message);
            }
            managedProcess = new ManagedProcess(process, DateTimeOffset.UtcNow);
            if (!_processes.TryAdd(id, managedProcess))
            {
                process.Kill(true); process.Dispose(); throw PanelProblems.Conflict("SERVER_BUSY", "A managed process already exists.");
            }
            _ = PumpAsync(id, process.StandardOutput, "stdout", CancellationToken.None);
            _ = PumpAsync(id, process.StandardError, "stderr", CancellationToken.None);
            _ = MonitorAsync(id, managedProcess);
            server.State = ServerState.Starting; server.ProcessId = process.Id; server.StartedAt = managedProcess.StartedAt;
            server.RestartRequired = false; server.UpdatedAt = DateTimeOffset.UtcNow;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch
            {
                managedProcess.RequestStop();
                try { process.Kill(true); } catch { }
                throw;
            }
        }
        finally { _memoryAdmission.Release(); }
        try { await console.AppendAsync(id, "system", $"Started Java process {process.Id} using Java {probed.Major}.", cancellationToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not persist start message for {ServerId}", id); }
        await PublishStateAsync(server, cancellationToken);
        return new StartAttempt(readinessCursor, managedProcess);
    }

    private async Task CompleteStartAsync(Guid id, StartAttempt attempt, CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(id, out var managed) || !ReferenceEquals(managed, attempt.Managed))
            throw new PanelException(500, "OPERATION_FAILED", "The Java process exited during startup.");
        var readyTask = console.WaitForAsync(id, attempt.ReadinessCursor,
            line => line.Text.Contains("Done (", StringComparison.OrdinalIgnoreCase) || line.Text.Contains("For help, type", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(90), cancellationToken);
        var first = await Task.WhenAny(readyTask, managed.Exit.Task);
        if (first == managed.Exit.Task) throw new PanelException(500, "OPERATION_FAILED", "The Java process exited before Minecraft became ready.");
        var ready = await readyTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (!ready)
        {
            try { await console.AppendAsync(id, "system", "Minecraft readiness text was not detected after 90 seconds; treating the running process as ready.", cancellationToken); }
            catch { }
        }
        using var readyLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var readyDb = await stateFactory.CreateDbContextAsync(cancellationToken);
        var current = await readyDb.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (!_processes.TryGetValue(id, out var currentManaged) || !ReferenceEquals(currentManaged, attempt.Managed) || current.State != ServerState.Starting)
            throw new PanelException(409, "OPERATION_FAILED", "The server stopped while Minecraft was starting.");
        current.State = ServerState.Running; current.UpdatedAt = DateTimeOffset.UtcNow;
        await readyDb.SaveChangesAsync(cancellationToken);
        await PublishStateAsync(current, cancellationToken);
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        ManagedProcess managed;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            if (!_processes.TryGetValue(id, out managed!))
            {
                if (server.State == ServerState.Stopped) return;
                if (server.State == ServerState.Crashed)
                {
                    server.State = ServerState.Stopped; server.CrashAttempts = 0; server.StartedAt = null;
                    await db.SaveChangesAsync(cancellationToken);
                    await console.AppendAsync(id, "system", "Pending crash recovery was cancelled.", cancellationToken);
                    return;
                }
                throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
            }
            managed.RequestStop(); server.State = ServerState.Stopping; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await WriteCommandInternalAsync(managed, "stop", cancellationToken);
            await console.AppendAsync(id, "system", "Graceful stop requested.", cancellationToken);
            await PublishStateAsync(server, cancellationToken);
        }
        var completed = await Task.WhenAny(managed.Exit.Task, Task.Delay(TimeSpan.FromSeconds(options.Value.GracefulStopSeconds), cancellationToken));
        if (completed != managed.Exit.Task)
        {
            await console.AppendAsync(id, "system", "Graceful stop timed out; killing the process tree.", CancellationToken.None);
            try { managed.Process.Kill(true); } catch (Exception exception) { logger.LogWarning(exception, "Could not kill server {ServerId}", id); }
        }
        await managed.Exit.Task.WaitAsync(cancellationToken);
    }

    public async Task RestartAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_processes.ContainsKey(id)) await StopAsync(id, cancellationToken);
        await StartAsync(id, false, cancellationToken);
    }

    public async Task KillAsync(Guid id, CancellationToken cancellationToken)
    {
        ManagedProcess managed;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            _ = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            if (!_processes.TryGetValue(id, out managed!)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
            managed.RequestStop();
            try { managed.Process.Kill(true); }
            catch (Exception exception) { throw new PanelException(500, "OPERATION_FAILED", "The process tree could not be killed.", exception.Message); }
        }
        await console.AppendAsync(id, "system", "Emergency process-tree kill requested.", cancellationToken);
        await managed.Exit.Task.WaitAsync(cancellationToken);
    }

    public async Task CommandAsync(Guid id, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(x => x is '\r' or '\n' or '\0'))
            throw PanelProblems.Validation("Commands must be one non-empty line of at most 4096 characters.");
        if (command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { await StopAsync(id, cancellationToken); return; }
        if (!_processes.TryGetValue(id, out var managed)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
        await WriteCommandInternalAsync(managed, command, cancellationToken);
    }

    public ServerRuntimeMetrics GetMetrics(Guid id)
    {
        if (!_processes.TryGetValue(id, out var managed)) return new(0, 0, 0);
        try
        {
            if (managed.Process.HasExited) return new(0, 0, 0);
            lock (managed.MetricsLock)
            {
                managed.Process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpu = managed.Process.TotalProcessorTime;
                var elapsedMs = (now - managed.LastMetricAt).TotalMilliseconds;
                var cpuPercent = elapsedMs <= 0 ? managed.LastCpuPercent : Math.Clamp((cpu - managed.LastCpuTime).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100, 0, 100);
                managed.LastMetricAt = now; managed.LastCpuTime = cpu; managed.LastCpuPercent = cpuPercent;
                return new(cpuPercent, managed.Process.WorkingSet64 / 1024d / 1024d, Math.Max(0, (long)(now - managed.StartedAt).TotalSeconds));
            }
        }
        catch { return new(0, 0, Math.Max(0, (long)(DateTimeOffset.UtcNow - managed.StartedAt).TotalSeconds)); }
    }

    public bool IsRunning(Guid id) => _processes.ContainsKey(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            await using var db = await stateFactory.CreateDbContextAsync(stoppingToken);
            var ids = await db.Servers.Where(x => x.StartOnBoot && x.State == ServerState.Stopped).Select(x => x.Id).ToListAsync(stoppingToken);
            foreach (var id in ids)
            {
                try { await StartAsync(id, false, stoppingToken); }
                catch (Exception exception) { logger.LogError(exception, "Start-on-boot failed for {ServerId}", id); }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        var stops = _processes.Keys.Select(id => StopForShutdownAsync(id)).ToArray();
        await Task.WhenAll(stops);
    }

    private async Task StopForShutdownAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.GracefulStopSeconds + 5));
        try { await StopAsync(id, timeout.Token); }
        catch (Exception exception) { logger.LogWarning(exception, "Shutdown stop failed for {ServerId}", id); }
    }

    private async Task MonitorAsync(Guid id, ManagedProcess managed)
    {
        try { await managed.Process.WaitForExitAsync(); }
        catch (Exception exception) { logger.LogDebug(exception, "WaitForExit failed for {ServerId}", id); }
        var exitCode = managed.Process.HasExited ? managed.Process.ExitCode : -1;
        _processes.TryRemove(new KeyValuePair<Guid, ManagedProcess>(id, managed));
        var requested = managed.StopRequested;
        managed.Process.Dispose();
        int attempt = 0;
        bool recover = false;
        try
        {
            using var serverLock = await keyedLock.AcquireAsync(id);
            await using var db = await stateFactory.CreateDbContextAsync();
            var server = await db.Servers.FindAsync(id);
            if (server is null) return;
            server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
            if (requested)
            {
                server.State = ServerState.Stopped; server.CrashAttempts = 0; server.StartedAt = null;
            }
            else
            {
                if (DateTimeOffset.UtcNow - managed.StartedAt >= TimeSpan.FromMinutes(5)) server.CrashAttempts = 0;
                server.State = ServerState.Crashed; server.CrashAttempts++; attempt = server.CrashAttempts;
                recover = server.CrashRecovery && attempt <= 3;
            }
            await db.SaveChangesAsync();
            await db.Players.Where(x => x.ServerId == id && x.Online).ExecuteUpdateAsync(x => x.SetProperty(p => p.Online, false));
            try { await console.AppendAsync(id, "system", requested ? $"Server stopped (exit {exitCode})." : $"Server exited unexpectedly with code {exitCode}."); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not persist exit message for {ServerId}", id); }
            await PublishStateAsync(server, CancellationToken.None);
        }
        catch (Exception exception) { logger.LogError(exception, "Could not persist exit state for {ServerId}", id); }
        finally { managed.Exit.TrySetResult(exitCode); }
        if (recover)
        {
            var delay = new[] { 5, 15, 60 }[attempt - 1];
            try { await console.AppendAsync(id, "system", $"Crash recovery attempt {attempt}/3 in {delay} seconds."); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not persist recovery message for {ServerId}", id); }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.ApplicationStopping);
                await TryStartRecoveryAsync(id, attempt, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Crash recovery failed for {ServerId}", id); }
        }
    }

    private async Task TryStartRecoveryAsync(Guid id, int expectedAttempt, CancellationToken cancellationToken)
    {
        StartAttempt attempt;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
            {
                var current = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (current is null || current.State != ServerState.Crashed || !current.CrashRecovery || current.CrashAttempts != expectedAttempt)
                    return;
            }
            attempt = await BeginStartLockedAsync(id, true, cancellationToken);
        }
        await CompleteStartAsync(id, attempt, cancellationToken);
    }

    private async Task PumpAsync(Guid id, StreamReader reader, string stream, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                try { await console.AppendAsync(id, stream, line, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { logger.LogError(exception, "Console persistence failed for {ServerId}; continuing to drain {Stream}", id, stream); }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        { logger.LogDebug(exception, "Console stream ended for {ServerId}", id); }
    }

    private static ProcessStartInfo BuildStartInfo(ServerEntity server, string javaPath, string jar)
    {
        var start = new ProcessStartInfo
        {
            FileName = javaPath, WorkingDirectory = Path.GetDirectoryName(jar)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add($"-Xms{server.InitialMemoryMb}M");
        start.ArgumentList.Add($"-Xmx{server.MemoryMb}M");
        if (server.UseAikarFlags)
            foreach (var argument in AikarFlags) start.ArgumentList.Add(argument);
        foreach (var argument in JvmArgumentParser.Parse(server.JvmArguments)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("-jar"); start.ArgumentList.Add(server.ExecutableJar); start.ArgumentList.Add("nogui");
        start.Environment.Remove("JAVA_TOOL_OPTIONS"); start.Environment.Remove("_JAVA_OPTIONS"); start.Environment.Remove("JDK_JAVA_OPTIONS");
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MCPANEL_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        return start;
    }

    private static async Task WriteCommandInternalAsync(ManagedProcess managed, string command, CancellationToken cancellationToken)
    {
        await managed.InputLock.WaitAsync(cancellationToken);
        try { await managed.Process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken); await managed.Process.StandardInput.FlushAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server console is no longer available.", exception.Message); }
        finally { managed.InputLock.Release(); }
    }

    private static void EnsurePortAvailable(int port)
    {
        try { var listener = new TcpListener(IPAddress.Any, port); listener.Start(); listener.Stop(); }
        catch (SocketException) { throw PanelProblems.Conflict("PORT_IN_USE", "The selected game port is already in use on the host."); }
    }

    private async Task PublishStateAsync(ServerEntity server, CancellationToken cancellationToken)
    {
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("ServerStateChanged", new { serverId = server.Id, state = server.State.ToString() }, cancellationToken), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogDebug(exception, "Could not broadcast state for {ServerId}", server.Id); }
    }

    private static int RequiredJava(string version)
    {
        var pieces = version.Split('-', '+')[0].Split('.');
        if (pieces.Length > 0 && int.TryParse(pieces[0], out var calendar) && calendar >= 26) return 25;
        if (pieces.Length < 2 || !int.TryParse(pieces[1], out var minor)) return 21;
        var patch = pieces.Length > 2 && int.TryParse(pieces[2], out var p) ? p : 0;
        return minor > 20 || minor == 20 && patch >= 5 ? 21 : minor >= 18 ? 17 : minor == 17 ? 16 : 8;
    }

    private sealed record StartAttempt(long ReadinessCursor, ManagedProcess Managed);

    private sealed class ManagedProcess(Process process, DateTimeOffset startedAt)
    {
        private int _stopRequested;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim InputLock { get; } = new(1, 1);
        public object MetricsLock { get; } = new();
        public DateTimeOffset LastMetricAt { get; set; } = startedAt;
        public TimeSpan LastCpuTime { get; set; } = process.TotalProcessorTime;
        public double LastCpuPercent { get; set; }
        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);
    }
}
