using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Infrastructure;
using Microsoft.Data.Sqlite;

namespace McPanel.Api.Services;

public enum RuntimeProcessState { Starting, Running, Stopping, Stopped, Crashed }

public sealed record RuntimeLaunchRequest(
    Guid ServerId, string JavaExecutable, string WorkingDirectory, IReadOnlyList<string> Arguments,
    int MemoryLimitMb, int GracefulStopSeconds);

public sealed record RuntimeServerSnapshot(
    Guid ServerId, RuntimeProcessState State, int? ProcessId, DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt, int? ExitCode, bool MemoryLimitExceeded,
    double CpuPercent, double MemoryUsedMb, double MemoryPeakMb, double SwapUsedMb,
    double AnonymousMemoryMb, double FileMemoryMb, double KernelMemoryMb, double SocketMemoryMb,
    bool MemoryEnforced, long UptimeSeconds);
public sealed record RuntimeSubscription(long Revision, IReadOnlyList<RuntimeServerSnapshot> Servers);

internal sealed record RuntimeWireRequest(int Version, Guid RequestId, string Operation, JsonElement Payload);
internal sealed record RuntimeWireResponse(int Version, Guid RequestId, bool Success, string? Error, JsonElement Payload);

internal static class RuntimeWire
{
    public const int Version = 1;
    private const int MaximumFrame = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static T? Value<T>(JsonElement value) => value.Deserialize<T>(Json);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("Runtime protocol frame is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrame) throw new InvalidDataException("Invalid runtime protocol frame length.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Invalid runtime protocol payload.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("Runtime protocol connection closed.");
            read += count;
        }
    }
}

public sealed class PersistentRuntimeClient(PanelPaths paths, IHostEnvironment environment, ILogger<PersistentRuntimeClient> logger)
{
    private readonly ConcurrentDictionary<Guid, RuntimeServerSnapshot> _snapshots = new();
    private long _revision;
    public bool Enabled { get; } = environment.IsProduction() && OperatingSystem.IsLinux();

    public bool IsRunning(Guid id) => Enabled && _snapshots.TryGetValue(id, out var snapshot) && IsActive(snapshot.State);
    public RuntimeServerSnapshot? Get(Guid id) => _snapshots.GetValueOrDefault(id);

    public async Task<IReadOnlyList<RuntimeServerSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return [];
        var snapshots = await SendAsync<IReadOnlyList<RuntimeServerSnapshot>>("snapshot", null, cancellationToken) ?? [];
        _snapshots.Clear();
        foreach (var snapshot in snapshots) _snapshots[snapshot.ServerId] = snapshot;
        return snapshots;
    }

    public async Task<IReadOnlyList<RuntimeServerSnapshot>> SubscribeAsync(CancellationToken cancellationToken)
    {
        var subscription = await SendAsync<RuntimeSubscription>("subscribe", _revision, cancellationToken)
            ?? throw RuntimeUnavailable("The runtime returned no subscription result.");
        _revision = subscription.Revision;
        _snapshots.Clear();
        foreach (var snapshot in subscription.Servers) _snapshots[snapshot.ServerId] = snapshot;
        return subscription.Servers;
    }

    public async Task<RuntimeServerSnapshot> StartAsync(RuntimeLaunchRequest launch, CancellationToken cancellationToken)
    {
        var snapshot = await SendAsync<RuntimeServerSnapshot>("start", launch, cancellationToken)
            ?? throw RuntimeUnavailable("The runtime returned no start result.");
        _snapshots[launch.ServerId] = snapshot;
        return snapshot;
    }

    public async Task<RuntimeServerSnapshot> StopAsync(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await SendAsync<RuntimeServerSnapshot>("stop", id, cancellationToken)
            ?? throw RuntimeUnavailable("The runtime returned no stop result.");
        _snapshots[id] = snapshot;
        return snapshot;
    }

    public async Task<RuntimeServerSnapshot> RestartAsync(RuntimeLaunchRequest launch, CancellationToken cancellationToken)
    {
        var snapshot = await SendAsync<RuntimeServerSnapshot>("restart", launch, cancellationToken)
            ?? throw RuntimeUnavailable("The runtime returned no restart result.");
        _snapshots[launch.ServerId] = snapshot;
        return snapshot;
    }

    public async Task<RuntimeServerSnapshot> KillAsync(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await SendAsync<RuntimeServerSnapshot>("kill", id, cancellationToken)
            ?? throw RuntimeUnavailable("The runtime returned no kill result.");
        _snapshots[id] = snapshot;
        return snapshot;
    }

    public Task CommandAsync(Guid id, string command, CancellationToken cancellationToken) =>
        SendAsync<object>("command", new RuntimeCommand(id, command), cancellationToken);

    private async Task<T?> SendAsync<T>(string operation, object? payload, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("The persistent runtime is disabled in this environment.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.RuntimeSocket), cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var requestId = Guid.NewGuid();
            await RuntimeWire.WriteAsync(stream, new RuntimeWireRequest(RuntimeWire.Version, requestId, operation, RuntimeWire.Element(payload)), cancellationToken);
            var response = await RuntimeWire.ReadAsync<RuntimeWireResponse>(stream, cancellationToken);
            if (response.Version != RuntimeWire.Version || response.RequestId != requestId)
                throw new InvalidDataException("The runtime returned a mismatched protocol response.");
            if (!response.Success) throw new PanelException(409, "RUNTIME_OPERATION_FAILED", response.Error ?? "The runtime operation failed.");
            return response.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? default : RuntimeWire.Value<T>(response.Payload);
        }
        catch (PanelException) { throw; }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidDataException)
        {
            logger.LogWarning(exception, "Persistent runtime request {Operation} failed", operation);
            throw RuntimeUnavailable(exception.Message);
        }
    }

    private static PanelException RuntimeUnavailable(string detail) =>
        new(503, "RUNTIME_UNAVAILABLE", "The Minecraft runtime service is unavailable.", detail);
    public static bool IsActive(RuntimeProcessState state) => state is RuntimeProcessState.Starting or RuntimeProcessState.Running or RuntimeProcessState.Stopping;
    private sealed record RuntimeCommand(Guid ServerId, string Command);
}

public static class PersistentRuntimeHost
{
    public const string Argument = "--mcpanel-runtime-host";
    public static bool IsInvocation(string[] arguments) => arguments.Length == 1 && arguments[0] == Argument;

    public static async Task<int> RunAsync(string[] arguments)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        var panelOptions = new PanelOptions();
        builder.Configuration.GetSection("Panel").Bind(panelOptions);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(panelOptions));
        var paths = new PanelPaths(panelOptions); paths.EnsureCreated();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<CgroupMemoryService>();
        builder.Services.AddSingleton<RuntimeEngine>();
        builder.Services.AddHostedService<RuntimeSocketService>();
        await builder.Build().RunAsync();
        return 0;
    }
}

internal sealed class RuntimeSocketService(
    PanelPaths paths, RuntimeEngine engine, IHostApplicationLifetime lifetime,
    ILogger<RuntimeSocketService> logger) : BackgroundService
{
    private Socket? _listener;
    private byte[]? _startupExecutableHash;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await engine.InitializeAsync(stoppingToken);
        _startupExecutableHash = ExecutableHash();
        _ = WatchForIdleUpgradeAsync(stoppingToken);
        if (File.Exists(paths.RuntimeSocket)) File.Delete(paths.RuntimeSocket);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(paths.RuntimeSocket));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(paths.RuntimeSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(32);
        logger.LogInformation("Persistent runtime listening on {Socket}", paths.RuntimeSocket);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(stoppingToken);
                _ = HandleAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _listener.Dispose();
            try { File.Delete(paths.RuntimeSocket); } catch { }
            await engine.StopAllAsync(CancellationToken.None);
        }
    }

    private async Task WatchForIdleUpgradeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                if (engine.Snapshot().Any(x => PersistentRuntimeClient.IsActive(x.State))) continue;
                var current = ExecutableHash();
                if (_startupExecutableHash is not null && current is not null && !CryptographicOperations.FixedTimeEquals(_startupExecutableHash, current))
                {
                    logger.LogInformation("Runtime binary changed and no servers are active; restarting onto the updated binary");
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static byte[]? ExecutableHash()
    {
        try { return Environment.ProcessPath is { } path && File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null; }
        catch { return null; }
    }

    private async Task HandleAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            RuntimeWireRequest? request = null;
            try
            {
                request = await RuntimeWire.ReadAsync<RuntimeWireRequest>(stream, cancellationToken);
                if (request.Version != RuntimeWire.Version) throw new InvalidDataException($"Unsupported runtime protocol version {request.Version}.");
                var result = await DispatchAsync(request, cancellationToken);
                await RuntimeWire.WriteAsync(stream, new RuntimeWireResponse(RuntimeWire.Version, request.RequestId, true, null, result), cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Runtime protocol request failed");
                try
                {
                    await RuntimeWire.WriteAsync(stream, new RuntimeWireResponse(RuntimeWire.Version, request?.RequestId ?? Guid.Empty, false, exception.Message, RuntimeWire.Element<object?>(null)), CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private async Task<JsonElement> DispatchAsync(RuntimeWireRequest request, CancellationToken cancellationToken) => request.Operation switch
    {
        "snapshot" => RuntimeWire.Element(engine.Snapshot()),
        "subscribe" => RuntimeWire.Element(await engine.SubscribeAsync(RuntimeWire.Value<long>(request.Payload), cancellationToken)),
        "start" => RuntimeWire.Element(await engine.StartAsync(RuntimeWire.Value<RuntimeLaunchRequest>(request.Payload) ?? throw new InvalidDataException("Missing launch request."), cancellationToken)),
        "restart" => RuntimeWire.Element(await engine.RestartAsync(RuntimeWire.Value<RuntimeLaunchRequest>(request.Payload) ?? throw new InvalidDataException("Missing launch request."), cancellationToken)),
        "stop" => RuntimeWire.Element(await engine.StopAsync(RuntimeWire.Value<Guid>(request.Payload), false, cancellationToken)),
        "kill" => RuntimeWire.Element(await engine.StopAsync(RuntimeWire.Value<Guid>(request.Payload), true, cancellationToken)),
        "command" => await CommandAsync(request.Payload, cancellationToken),
        "upgradeWhenIdle" => UpgradeWhenIdle(),
        _ => throw new InvalidDataException("Unknown runtime operation.")
    };

    private async Task<JsonElement> CommandAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = RuntimeWire.Value<RuntimeCommand>(payload) ?? throw new InvalidDataException("Missing command request.");
        await engine.CommandAsync(command.ServerId, command.Command, cancellationToken);
        return RuntimeWire.Element<object?>(null);
    }

    private JsonElement UpgradeWhenIdle()
    {
        var idle = engine.Snapshot().All(x => !PersistentRuntimeClient.IsActive(x.State));
        if (idle) lifetime.StopApplication();
        return RuntimeWire.Element(idle);
    }

    private sealed record RuntimeCommand(Guid ServerId, string Command);
}

internal sealed class RuntimeEngine(
    PanelPaths paths, CgroupMemoryService cgroups, ILogger<RuntimeEngine> logger)
{
    private readonly ConcurrentDictionary<Guid, RuntimeProcess> _active = new();
    private readonly ConcurrentDictionary<Guid, RuntimeServerSnapshot> _status = new();
    private long _revision;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RuntimeState);
        await RuntimeConsoleWriter.EnsureCreatedAsync(paths.ConsoleDatabase, cancellationToken);
        foreach (var file in Directory.EnumerateFiles(paths.RuntimeState, "*.json"))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RuntimeServerSnapshot>(await File.ReadAllTextAsync(file, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (snapshot is null) continue;
                if (PersistentRuntimeClient.IsActive(snapshot.State))
                    snapshot = snapshot with { State = RuntimeProcessState.Crashed, ProcessId = null, UpdatedAt = DateTimeOffset.UtcNow, ExitCode = -1 };
                _status[snapshot.ServerId] = snapshot;
                await PersistAsync(snapshot, cancellationToken);
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not load runtime state {File}", file); }
        }
        Changed();
    }

    public IReadOnlyList<RuntimeServerSnapshot> Snapshot()
    {
        foreach (var (id, process) in _active)
        {
            var measured = Measure(process) with { ServerId = id };
            _status.AddOrUpdate(
                id,
                measured with { State = RuntimeProcessState.Running },
                (_, current) => PersistentRuntimeClient.IsActive(current.State)
                    ? measured with { State = current.State }
                    : current);
        }
        return _status.Values.OrderBy(x => x.ServerId).ToList();
    }

    public async Task<RuntimeSubscription> SubscribeAsync(long afterRevision, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
        while (Volatile.Read(ref _revision) <= afterRevision && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, cancellationToken);
        return new(Volatile.Read(ref _revision), Snapshot());
    }

    public async Task<RuntimeServerSnapshot> RestartAsync(RuntimeLaunchRequest launch, CancellationToken cancellationToken)
    {
        if (_active.ContainsKey(launch.ServerId)) await StopAsync(launch.ServerId, false, cancellationToken);
        return await StartAsync(launch, cancellationToken);
    }

    public async Task<RuntimeServerSnapshot> StartAsync(RuntimeLaunchRequest launch, CancellationToken cancellationToken)
    {
        ValidateLaunch(launch);
        if (_active.ContainsKey(launch.ServerId)) throw new InvalidOperationException("The server already has an active process.");
        var workload = cgroups.Create(launch.ServerId, launch.MemoryLimitMb);
        var start = new ProcessStartInfo
        {
            FileName = launch.JavaExecutable,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("JAVA_TOOL_OPTIONS"); start.Environment.Remove("_JAVA_OPTIONS"); start.Environment.Remove("JDK_JAVA_OPTIONS");
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MCPANEL_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        Process process;
        try { process = Process.Start(cgroups.Wrap(start, workload)) ?? throw new InvalidOperationException("Java could not start."); }
        catch { cgroups.Remove(workload); throw; }
        var managed = new RuntimeProcess(process, DateTimeOffset.UtcNow, launch.GracefulStopSeconds, workload, cgroups.Read(workload)?.OomKillCount ?? 0);
        if (!_active.TryAdd(launch.ServerId, managed))
        {
            process.Kill(true); process.Dispose(); cgroups.Remove(workload);
            throw new InvalidOperationException("The server already has an active process.");
        }
        var snapshot = Measure(managed) with { ServerId = launch.ServerId, State = RuntimeProcessState.Starting };
        _status[launch.ServerId] = snapshot; await PersistAsync(snapshot, cancellationToken);
        Changed();
        await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, launch.ServerId, "system", $"Started Java process {process.Id}.", cancellationToken);
        _ = PumpAsync(launch.ServerId, managed, process.StandardOutput, "stdout");
        _ = PumpAsync(launch.ServerId, managed, process.StandardError, "stderr");
        _ = MonitorAsync(launch.ServerId, managed);
        var first = await Task.WhenAny(managed.Ready.Task, managed.Exit.Task, Task.Delay(TimeSpan.FromSeconds(90), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (first == managed.Exit.Task) throw new InvalidOperationException("Java exited before Minecraft became ready.");
        if (first != managed.Ready.Task)
            await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, launch.ServerId, "system", "Minecraft readiness text was not detected after 90 seconds; treating the running process as ready.", cancellationToken);
        snapshot = Measure(managed) with { ServerId = launch.ServerId, State = RuntimeProcessState.Running };
        _status[launch.ServerId] = snapshot; await PersistAsync(snapshot, cancellationToken);
        Changed();
        return snapshot;
    }

    public async Task<RuntimeServerSnapshot> StopAsync(Guid id, bool kill, CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(id, out var managed))
            return _status.GetValueOrDefault(id) ?? throw new InvalidOperationException("The server is not running.");
        managed.RequestStop();
        var stopping = Measure(managed) with { ServerId = id, State = RuntimeProcessState.Stopping };
        _status[id] = stopping; await PersistAsync(stopping, cancellationToken);
        Changed();
        if (kill)
        {
            try { managed.Process.Kill(true); } catch { }
            await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, id, "system", "Emergency process-tree kill requested.", cancellationToken);
        }
        else
        {
            await WriteAsync(managed, "stop", cancellationToken);
            await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, id, "system", "Graceful stop requested.", cancellationToken);
            var completed = await Task.WhenAny(managed.Exit.Task, Task.Delay(TimeSpan.FromSeconds(managed.GracefulStopSeconds), cancellationToken));
            if (completed != managed.Exit.Task)
            {
                await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, id, "system", "Graceful stop timed out; killing the process tree.", CancellationToken.None);
                try { managed.Process.Kill(true); } catch { }
            }
        }
        await managed.Exit.Task.WaitAsync(cancellationToken);
        return _status[id];
    }

    public async Task CommandAsync(Guid id, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(x => x is '\r' or '\n' or '\0'))
            throw new InvalidDataException("Commands must be one non-empty line of at most 4096 characters.");
        if (!_active.TryGetValue(id, out var managed)) throw new InvalidOperationException("The server is not running.");
        await WriteAsync(managed, command, cancellationToken);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_active.Keys.Select(async id => { try { await StopAsync(id, false, cancellationToken); } catch (Exception exception) { logger.LogWarning(exception, "Could not stop server {ServerId}", id); } }));

    private async Task PumpAsync(Guid id, RuntimeProcess managed, StreamReader reader, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase) || line.Contains("For help, type", StringComparison.OrdinalIgnoreCase)) managed.Ready.TrySetResult();
                await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, id, stream, line, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { logger.LogDebug(exception, "Runtime console stream ended for {ServerId}", id); }
    }

    private async Task MonitorAsync(Guid id, RuntimeProcess managed)
    {
        try { await managed.Process.WaitForExitAsync(); } catch { }
        var exitCode = managed.Process.HasExited ? managed.Process.ExitCode : -1;
        var finalMemory = cgroups.Read(managed.Cgroup);
        var exceeded = finalMemory is not null && finalMemory.OomKillCount > managed.InitialOomKillCount;
        _active.TryRemove(new KeyValuePair<Guid, RuntimeProcess>(id, managed));
        var requested = managed.StopRequested;
        var clean = requested || exitCode == 0;
        managed.Process.Dispose(); cgroups.Remove(managed.Cgroup);
        var snapshot = new RuntimeServerSnapshot(id, clean ? RuntimeProcessState.Stopped : RuntimeProcessState.Crashed, null,
            clean ? null : managed.StartedAt, DateTimeOffset.UtcNow, exitCode, exceeded, 0,
            (finalMemory?.CurrentBytes ?? 0) / 1024d / 1024d, (finalMemory?.PeakBytes ?? 0) / 1024d / 1024d,
            (finalMemory?.SwapBytes ?? 0) / 1024d / 1024d,
            (finalMemory?.AnonymousBytes ?? 0) / 1024d / 1024d, (finalMemory?.FileBytes ?? 0) / 1024d / 1024d,
            (finalMemory?.KernelBytes ?? 0) / 1024d / 1024d, (finalMemory?.SocketBytes ?? 0) / 1024d / 1024d,
            managed.Cgroup is not null,
            Math.Max(0, (long)(DateTimeOffset.UtcNow - managed.StartedAt).TotalSeconds));
        _status[id] = snapshot; await PersistAsync(snapshot, CancellationToken.None);
        Changed();
        var message = clean ? $"Server stopped (exit {exitCode})." : exceeded ? $"Server exceeded its total RAM limit and stopped (exit {exitCode})." : $"Server exited unexpectedly with code {exitCode}.";
        await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, id, "system", message, CancellationToken.None);
        managed.Exit.TrySetResult(exitCode);
    }

    private RuntimeServerSnapshot Measure(RuntimeProcess managed)
    {
        try
        {
            lock (managed.MetricsLock)
            {
                managed.Process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpu = managed.Process.TotalProcessorTime;
                var elapsed = (now - managed.LastMetricAt).TotalMilliseconds;
                var percent = elapsed <= 0 ? managed.LastCpuPercent : Math.Clamp((cpu - managed.LastCpuTime).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
                managed.LastMetricAt = now; managed.LastCpuTime = cpu; managed.LastCpuPercent = percent;
                var memory = cgroups.Read(managed.Cgroup);
                return new(Guid.Empty, RuntimeProcessState.Running, managed.Process.Id, managed.StartedAt, now, null, false, percent,
                    (memory?.CurrentBytes ?? managed.Process.WorkingSet64) / 1024d / 1024d,
                    (memory?.PeakBytes ?? managed.Process.PeakWorkingSet64) / 1024d / 1024d,
                    (memory?.SwapBytes ?? 0) / 1024d / 1024d,
                    (memory?.AnonymousBytes ?? 0) / 1024d / 1024d, (memory?.FileBytes ?? 0) / 1024d / 1024d,
                    (memory?.KernelBytes ?? 0) / 1024d / 1024d, (memory?.SocketBytes ?? 0) / 1024d / 1024d,
                    managed.Cgroup is not null,
                    Math.Max(0, (long)(now - managed.StartedAt).TotalSeconds));
            }
        }
        catch { return new(Guid.Empty, RuntimeProcessState.Running, managed.Process.Id, managed.StartedAt, DateTimeOffset.UtcNow, null, false, 0, 0, 0, 0, 0, 0, 0, 0, managed.Cgroup is not null, Math.Max(0, (long)(DateTimeOffset.UtcNow - managed.StartedAt).TotalSeconds)); }
    }

    private async Task PersistAsync(RuntimeServerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(paths.RuntimeState, $"{snapshot.ServerId:N}.json");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
        File.Move(temporary, destination, true);
    }

    private void Changed() => Interlocked.Increment(ref _revision);

    private void ValidateLaunch(RuntimeLaunchRequest launch)
    {
        var work = Path.GetFullPath(launch.WorkingDirectory);
        var instances = Path.GetFullPath(paths.Instances) + Path.DirectorySeparatorChar;
        if (!work.StartsWith(instances, StringComparison.Ordinal) || !Directory.Exists(work)) throw new InvalidDataException("The runtime working directory is outside the instances root.");
        if (!Path.IsPathFullyQualified(launch.JavaExecutable) || !File.Exists(launch.JavaExecutable)) throw new InvalidDataException("The Java executable is invalid.");
        if (launch.Arguments.Count is < 3 or > 256 || launch.Arguments.Any(x => x.Length > 4096 || x.Contains('\0'))) throw new InvalidDataException("The Java argument list is invalid.");
        if (launch.MemoryLimitMb < PanelOptions.MinimumServerTotalMemoryMb || launch.GracefulStopSeconds is < 1 or > 600) throw new InvalidDataException("The runtime resource limits are invalid.");
    }

    private static async Task WriteAsync(RuntimeProcess managed, string command, CancellationToken cancellationToken)
    {
        await managed.InputLock.WaitAsync(cancellationToken);
        try { await managed.Process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken); await managed.Process.StandardInput.FlushAsync(cancellationToken); }
        finally { managed.InputLock.Release(); }
    }

    private sealed class RuntimeProcess(Process process, DateTimeOffset startedAt, int gracefulStopSeconds, CgroupWorkload? cgroup, long initialOomKillCount)
    {
        private int _stopRequested;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int GracefulStopSeconds { get; } = gracefulStopSeconds;
        public CgroupWorkload? Cgroup { get; } = cgroup;
        public long InitialOomKillCount { get; } = initialOomKillCount;
        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim InputLock { get; } = new(1, 1);
        public object MetricsLock { get; } = new();
        public DateTimeOffset LastMetricAt { get; set; } = startedAt;
        public TimeSpan LastCpuTime { get; set; } = process.TotalProcessorTime;
        public double LastCpuPercent { get; set; }
    }
}

internal static class RuntimeConsoleWriter
{
    public static async Task EnsureCreatedAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Cache=Shared;Pooling=True");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS "Lines" (
                "Sequence" INTEGER NOT NULL CONSTRAINT "PK_Lines" PRIMARY KEY AUTOINCREMENT,
                "ServerId" TEXT NOT NULL, "Timestamp" INTEGER NOT NULL,
                "Stream" TEXT NOT NULL, "Level" TEXT NOT NULL, "Text" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_Lines_ServerId_Sequence" ON "Lines" ("ServerId", "Sequence");
            CREATE INDEX IF NOT EXISTS "IX_Lines_Timestamp" ON "Lines" ("Timestamp");
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task AppendAsync(string database, Guid serverId, string stream, string text, CancellationToken cancellationToken)
    {
        text = text.Length > 16_384 ? text[..16_384] : text;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={database};Cache=Shared;Pooling=True;Default Timeout=5");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO \"Lines\" (\"ServerId\", \"Timestamp\", \"Stream\", \"Level\", \"Text\") VALUES ($server, $time, $stream, $level, $text);";
                // EF Core's SQLite Guid converter uses uppercase text. Keep the raw runtime writer
                // byte-for-byte compatible because SQLite's default TEXT comparison is case-sensitive.
                command.Parameters.AddWithValue("$server", serverId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$stream", stream);
                command.Parameters.AddWithValue("$level", DetectLevel(text));
                command.Parameters.AddWithValue("$text", text);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 && attempt < 4)
            { await Task.Delay(50 * (attempt + 1), cancellationToken); }
        }
    }

    private static string DetectLevel(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "error" :
        line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "warn" : line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ? "debug" : "info";
}
