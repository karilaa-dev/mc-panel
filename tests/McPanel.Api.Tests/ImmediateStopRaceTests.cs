using System.Data.Common;
using System.Net.Sockets;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed partial class ApiValidationRegressionTests
{
    [Fact]
    public async Task Immediate_stop_cancels_restart_admitted_during_stop()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pause = new LifecycleSavePause(afterCommit: false,
            db => db.ChangeTracker.Entries<JobEntity>().Any(x => x.State == EntityState.Added && x.Entity.Type == "Restart"));
        var sweep = new CancellationSweepObserver();
        var factory = RaceStateFactory(pause, sweep);
        var services = _factory!.Services;
        using var queue = ActivatorUtilities.CreateInstance<OperationQueue>(services, factory);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, factory, queue);
        await supervisor.StartAsync(_serverId, false, default);
        var restart = supervisor.QueueActionAsync(_serverId, "restart", false, default);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var kill = supervisor.QueueActionAsync(_serverId, "kill", true, default);
            // The broken implementation sweeps before the paused INSERT commits.
            // A shared admission barrier instead holds the kill until that INSERT finishes.
            await Task.WhenAny(sweep.Completed.Task, Task.Delay(300));
            pause.Release.TrySetResult();
            var restartJob = await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Completed, (await kill.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await queue.StartAsync(default);
            for (var attempt = 0; attempt < 100 && !OperationQueue.IsTerminal((await queue.GetAsync(restartJob.Id, default))!.State); attempt++)
                await Task.Delay(20);
            Assert.Equal(JobState.Canceled, (await queue.GetAsync(restartJob.Id, default))!.State);
            Assert.False(supervisor.IsRunning(_serverId));
            Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);
        }
        finally
        {
            pause.Release.TrySetResult();
            await queue.StopAsync(default);
            if (supervisor.IsRunning(_serverId)) await supervisor.KillAsync(_serverId, default);
        }
    }

    [Theory]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    [InlineData(false, "restart")]
    [InlineData(true, "restart")]
    public async Task Immediate_stop_retries_when_start_has_not_reached_runtime(bool freshRuntime, string action)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var runtimeServer = new StartupGapRuntime(_paths!.RuntimeSocket, freshRuntime);
        var pause = new LifecycleSavePause(afterCommit: true,
            db => db.ChangeTracker.Entries<ServerEntity>().Any(x => x.Entity.State == ServerState.Starting && x.Entity.ProcessId is null));
        var factory = RaceStateFactory(pause);
        var services = _factory!.Services;
        var runtime = new PersistentRuntimeClient(_paths, new ProductionRuntimeEnvironment(), NullLogger<PersistentRuntimeClient>.Instance);
        using var queue = ActivatorUtilities.CreateInstance<OperationQueue>(services, factory);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, factory, queue, runtime);
        await queue.StartAsync(default);
        try
        {
            var start = await supervisor.QueueActionAsync(_serverId, action, false, default);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ServerState.Starting, (await ReadServerAsync()).State);
            var kill = supervisor.QueueActionAsync(_serverId, "kill", true, default);
            await runtimeServer.FirstKill.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pause.Release.TrySetResult();
            Assert.Equal(JobState.Completed, (await kill.WaitAsync(TimeSpan.FromSeconds(5))).State);
            for (var attempt = 0; attempt < 100 && !OperationQueue.IsTerminal((await queue.GetAsync(start.Id, default))!.State); attempt++)
                await Task.Delay(20);
            Assert.True(OperationQueue.IsTerminal((await queue.GetAsync(start.Id, default))!.State));
            Assert.False(runtimeServer.Running);
            Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);
            Assert.Null((await ReadServerAsync()).ProcessId);
        }
        finally
        {
            pause.Release.TrySetResult();
            await queue.StopAsync(default);
        }
    }

    [Fact]
    public async Task Immediate_stop_cancels_a_claimed_start_before_it_takes_the_server_lock()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var runtimeServer = new StartupGapRuntime(_paths!.RuntimeSocket, fresh: false);
        var claim = new StartClaimPause();
        var factory = RaceStateFactory(claim);
        var services = _factory!.Services;
        var runtime = new PersistentRuntimeClient(_paths, new ProductionRuntimeEnvironment(), NullLogger<PersistentRuntimeClient>.Instance);
        using var queue = ActivatorUtilities.CreateInstance<OperationQueue>(services, factory);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, factory, queue, runtime);
        await queue.StartAsync(default);
        try
        {
            var start = await supervisor.QueueActionAsync(_serverId, "start", false, default);
            // The worker has marked the job Running but has not reached the server lock.
            await claim.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Running, (await queue.GetAsync(start.Id, default))!.State);
            var kill = await supervisor.QueueActionAsync(_serverId, "kill", true, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Completed, kill.State);
            claim.Release.TrySetResult();
            for (var attempt = 0; attempt < 100 && !OperationQueue.IsTerminal((await queue.GetAsync(start.Id, default))!.State); attempt++)
                await Task.Delay(20);
            Assert.Equal(JobState.Canceled, (await queue.GetAsync(start.Id, default))!.State);
            Assert.False(runtimeServer.LaunchReceived.Task.IsCompleted);
            Assert.False(runtimeServer.Running);
            Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);

            // A deliberate start requested after the immediate stop is admitted normally.
            var restart = await supervisor.QueueActionAsync(_serverId, "start", false, default);
            for (var attempt = 0; attempt < 100 && !OperationQueue.IsTerminal((await queue.GetAsync(restart.Id, default))!.State); attempt++)
                await Task.Delay(20);
            Assert.Equal(JobState.Completed, (await queue.GetAsync(restart.Id, default))!.State);
            Assert.True(runtimeServer.Running);
        }
        finally
        {
            claim.Release.TrySetResult();
            await queue.StopAsync(default);
        }
    }

    [Fact]
    public async Task Immediate_stop_supersedes_panel_crash_recovery_that_already_checked_state()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var runtimeServer = new StartupGapRuntime(_paths!.RuntimeSocket, fresh: false);
        // Hold the kill's Stopped write until the scheduled recovery has read the Crashed state.
        var pause = new LifecycleSavePause(afterCommit: false,
            db => db.ChangeTracker.Entries<ServerEntity>().Any(x => x.State == EntityState.Modified && x.Entity.State == ServerState.Stopped));
        var recoveryCheck = new ServerReadObserver();
        var factory = RaceStateFactory(pause, recoveryCheck);
        var services = _factory!.Services;
        var runtime = new PersistentRuntimeClient(_paths, new ProductionRuntimeEnvironment(), NullLogger<PersistentRuntimeClient>.Instance);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, factory, runtime);
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
                await db.Servers.Where(x => x.Id == _serverId).ExecuteUpdateAsync(x => x.SetProperty(s => s.CrashRecovery, true));
            await supervisor.StartAsync(_serverId, false, default);
            Assert.True(runtimeServer.Running);
            // An older runtime without runtime-recovery leaves crash recovery to the panel.
            runtimeServer.Crash();
            await supervisor.ReconcilePersistentAsync(await runtime.RefreshAsync(default), false, default);
            Assert.Equal(ServerState.Crashed, (await ReadServerAsync()).State);

            var kill = supervisor.QueueActionAsync(_serverId, "kill", true, default);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            recoveryCheck.Arm();
            await recoveryCheck.Observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            pause.Release.TrySetResult();
            Assert.Equal(JobState.Completed, (await kill.WaitAsync(TimeSpan.FromSeconds(5))).State);

            // Give a stale recovery the chance to take the released server lock and launch.
            for (var attempt = 0; attempt < 25 && !runtimeServer.Running; attempt++) await Task.Delay(20);
            Assert.False(runtimeServer.Running);
            Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);

            // A deliberate start after the immediate stop is still admitted.
            await supervisor.StartAsync(_serverId, false, default);
            Assert.True(runtimeServer.Running);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Panel_crash_recovery_restarts_a_crashed_server_without_a_stop()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var runtimeServer = new StartupGapRuntime(_paths!.RuntimeSocket, fresh: false);
        var services = _factory!.Services;
        var runtime = new PersistentRuntimeClient(_paths, new ProductionRuntimeEnvironment(), NullLogger<PersistentRuntimeClient>.Instance);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, runtime);
        await using (var db = await services.GetRequiredService<IDbContextFactory<StateDbContext>>().CreateDbContextAsync())
            await db.Servers.Where(x => x.Id == _serverId).ExecuteUpdateAsync(x => x.SetProperty(s => s.CrashRecovery, true));
        await supervisor.StartAsync(_serverId, false, default);
        runtimeServer.Crash();
        await supervisor.ReconcilePersistentAsync(await runtime.RefreshAsync(default), false, default);
        Assert.Equal(ServerState.Crashed, (await ReadServerAsync()).State);
        for (var attempt = 0; attempt < 500 && (await ReadServerAsync()).State != ServerState.Running; attempt++) await Task.Delay(20);
        var server = await ReadServerAsync();
        Assert.Equal(ServerState.Running, server.State);
        Assert.Equal(1, server.CrashAttempts);
        Assert.True(runtimeServer.Running);
    }

    [Fact]
    public async Task Immediate_stop_waits_for_a_dispatched_restart_after_cancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var runtimeServer = new StartupGapRuntime(_paths!.RuntimeSocket, fresh: false, pauseLaunch: true);
        var services = _factory!.Services;
        var runtime = new PersistentRuntimeClient(_paths, new ProductionRuntimeEnvironment(), NullLogger<PersistentRuntimeClient>.Instance);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, runtime);
        try
        {
            var restart = await supervisor.QueueActionAsync(_serverId, "restart", false, default);
            await runtimeServer.LaunchReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var kill = supervisor.QueueActionAsync(_serverId, "kill", true, default);
            await runtimeServer.FirstKill.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(kill, Task.Delay(300));
            runtimeServer.ReleaseLaunch.TrySetResult();
            Assert.Equal(JobState.Completed, (await kill.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await runtimeServer.LaunchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = services.GetRequiredService<OperationQueue>();
            for (var attempt = 0; attempt < 100 && !OperationQueue.IsTerminal((await queue.GetAsync(restart.Id, default))!.State); attempt++)
                await Task.Delay(20);
            Assert.True(OperationQueue.IsTerminal((await queue.GetAsync(restart.Id, default))!.State));
            Assert.False(runtimeServer.Running);
            Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);
        }
        finally { runtimeServer.ReleaseLaunch.TrySetResult(); }
    }

    [Fact]
    public async Task Immediate_stop_bypasses_lifecycle_queue_backpressure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var services = _factory!.Services;
        using var queue = ActivatorUtilities.CreateInstance<OperationQueue>(services);
        using var supervisor = ActivatorUtilities.CreateInstance<ProcessSupervisor>(services, queue);
        await supervisor.StartAsync(_serverId, false, default);
        for (var index = 0; index < 256; index++)
            await queue.EnqueueAsync("Blocked", null, (_, _, _) => Task.CompletedTask, default);
        var restart = supervisor.QueueActionAsync(_serverId, "restart", false, default);
        try
        {
            for (var attempt = 0; attempt < 100 && (await queue.ListAsync(_serverId, 10, default)).Count == 0; attempt++)
                await Task.Delay(20);
            Assert.Equal(JobState.Queued, Assert.Single(await queue.ListAsync(_serverId, 10, default)).State);
            Assert.False(restart.IsCompleted);
            var kill = await supervisor.QueueActionAsync(_serverId, "kill", true, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Completed, kill.State);
            Assert.False(supervisor.IsRunning(_serverId));
            await queue.StartAsync(default);
            var job = await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Canceled, (await queue.GetAsync(job.Id, default))!.State);
        }
        finally
        {
            await queue.StopAsync(default);
            if (supervisor.IsRunning(_serverId)) await supervisor.KillAsync(_serverId, default);
        }
    }

    private IDbContextFactory<StateDbContext> RaceStateFactory(params IInterceptor[] interceptors) =>
        new RaceDbFactory(new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={_paths!.StateDatabase};Cache=Shared")
            .AddInterceptors(interceptors).Options);

    private sealed class RaceDbFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }

    private sealed class LifecycleSavePause(bool afterCommit, Func<StateDbContext, bool> matches) : SaveChangesInterceptor
    {
        private int _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task PauseAsync(DbContext? context, CancellationToken token)
        {
            if (context is StateDbContext db && matches(db) && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) await PauseAsync(eventData.Context, cancellationToken);
            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (afterCommit) await PauseAsync(eventData.Context, cancellationToken);
            return result;
        }
    }

    // Pauses the queue worker right after it claims a job, before its action runs.
    private sealed class StartClaimPause : DbCommandInterceptor
    {
        private int _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (result > 0 && command.CommandText.StartsWith("UPDATE \"Jobs\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"Progress\" =") && command.CommandText.Contains("\"CancellationRequested\"") &&
                Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    private sealed class ServerReadObserver : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && command.CommandText.Contains("FROM \"Servers\"", StringComparison.Ordinal))
                Observed.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellationSweepObserver : DbCommandInterceptor
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("CancellationRequested"))
                Completed.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    // Emulates both possible replies to a kill before the first start request arrives.
    // The supervisor, operation queue, database and wire client are real.
    private sealed class StartupGapRuntime : IAsyncDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly bool _fresh;
        private readonly bool _pauseLaunch;
        private readonly object _stateGate = new();
        private readonly List<Task> _handlers = [];
        private RuntimeServerSnapshot? _lastSnapshot;
        private bool _started;
        private bool _running;
        public bool Running => Volatile.Read(ref _running);
        public TaskCompletionSource LaunchReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LaunchCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstKill { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StartupGapRuntime(string path, bool fresh, bool pauseLaunch = false)
        {
            _fresh = fresh;
            _pauseLaunch = pauseLaunch;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(8);
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptAsync(_stop.Token);
                    _handlers.Add(HandleAsync(socket));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(Socket socket)
        {
            using (socket)
            await using (var stream = new NetworkStream(socket))
            try
            {
                var request = await RuntimeWire.ReadAsync<RuntimeWireRequest>(stream, _stop.Token);
                if (request.Operation == "snapshot")
                {
                    RuntimeServerSnapshot[] snapshots;
                    lock (_stateGate) snapshots = _lastSnapshot is null ? [] : [_lastSnapshot];
                    await RuntimeWire.WriteAsync(stream, new RuntimeWireResponse(RuntimeWire.Version, request.RequestId,
                        true, null, RuntimeWire.Element(snapshots)), _stop.Token);
                    return;
                }
                var id = request.Operation == "start"
                    ? RuntimeWire.Value<RuntimeLaunchRequest>(request.Payload)!.ServerId
                    : RuntimeWire.Value<Guid>(request.Payload);
                if (request.Operation == "start")
                {
                    LaunchReceived.TrySetResult();
                    if (_pauseLaunch) await ReleaseLaunch.Task.WaitAsync(_stop.Token);
                }
                bool missing;
                RuntimeServerSnapshot snapshot;
                lock (_stateGate)
                {
                    missing = request.Operation == "kill" && _fresh && !_started;
                    if (request.Operation == "start") { _started = true; _running = true; LaunchCompleted.TrySetResult(); }
                    else if (request.Operation == "kill") _running = false;
                    else throw new InvalidOperationException($"Unexpected runtime request: {request.Operation}");
                    snapshot = new RuntimeServerSnapshot(id, Running ? RuntimeProcessState.Running : RuntimeProcessState.Stopped,
                        Running ? 12345 : null, Running ? DateTimeOffset.UtcNow : null, DateTimeOffset.UtcNow,
                        null, false, 0, 0, 0, 0, 0, 0, 0, 0, false, 0);
                    if (!missing) _lastSnapshot = snapshot;
                }
                await RuntimeWire.WriteAsync(stream, new RuntimeWireResponse(RuntimeWire.Version, request.RequestId,
                    !missing, missing ? "The server is not running." : null, RuntimeWire.Element(snapshot)), _stop.Token);
                if (request.Operation == "kill") FirstKill.TrySetResult();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { /* A canceled client cannot retract a runtime launch. */ }
        }

        public void Crash()
        {
            lock (_stateGate)
            {
                _running = false;
                _lastSnapshot = _lastSnapshot! with { State = RuntimeProcessState.Crashed, ProcessId = null };
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _serve;
            await Task.WhenAll(_handlers);
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
