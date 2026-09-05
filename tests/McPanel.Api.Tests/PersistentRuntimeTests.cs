using McPanel.Api.Configuration;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using McPanel.Api.Data;

namespace McPanel.Api.Tests;

public sealed class PersistentRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-runtime-" + Guid.NewGuid().ToString("N"));
    private PanelPaths _paths = null!;
    private RuntimeEngine _engine = null!;
    private string _fakeJava = null!;

    public async Task InitializeAsync()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = Path.Combine(_root, "data"), ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
        _fakeJava = Path.Combine(_root, "fake-java");
        await File.WriteAllTextAsync(_fakeJava, """
            #!/bin/sh
            printf '%s\n' 'Done (0.01s)! For help, type "help"'
            while IFS= read -r line; do
                case "$line" in
                    spam) i=0; while [ "$i" -lt 20000 ]; do printf '%s\n' 'sustained stdout'; printf '%s\n' 'sustained stderr' >&2; i=$((i+1)); done ;;
                    ping) printf '%s\n' 'pong while panel is absent' ;;
                    "save-all flush") printf '%s\n' 'Saved the game' ;;
                    "save-on") printf '%s\n' 'Automatic saving is now enabled' ;;
                    crash) exit 7 ;;
                    stop) exit 0 ;;
                esac
            done
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_fakeJava, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cgroups = new CgroupMemoryService(new TestEnvironment(), NullLogger<CgroupMemoryService>.Instance);
        _engine = new RuntimeEngine(_paths, cgroups, NullLogger<RuntimeEngine>.Instance,
            Microsoft.Extensions.Options.Options.Create(new PanelOptions { BackupLeaseSeconds = 1, StartupTimeoutSeconds = 1 }))
            { MaintenanceInterval = TimeSpan.FromMilliseconds(20), RecoveryDelay = TimeSpan.FromMilliseconds(100) };
        await _engine.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Recovery_rechecks_committed_policy_without_waiting_for_panel_notification()
    {
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await using (var db = new McPanel.Api.Data.StateDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<McPanel.Api.Data.StateDbContext>()
            .UseSqlite($"Data Source={_paths.StateDatabase};Pooling=False").Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new() { Id = id, Name = "Do not restart", Version = "1.21", JavaRuntimeId = "java", State = McPanel.Api.Data.ServerState.Running, CrashRecovery = false });
            await db.SaveChangesAsync();
        }
        await _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1, CrashRecovery: true), default);
        await _engine.CommandAsync(id, "crash", default);
        await Task.Delay(500);
        Assert.Equal(RuntimeProcessState.Crashed, Assert.Single(_engine.Snapshot()).State);
        Assert.Null(Assert.Single(_engine.Snapshot()).ProcessId);
    }

    [Fact]
    public async Task Expired_save_lease_resumes_saving_without_a_panel_and_cannot_be_reused()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), CancellationToken.None);
        var lease = await _engine.AcquireSaveLeaseAsync(id, CancellationToken.None);
        await WaitForConsoleAsync(id, "Automatic saving is now enabled");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.RenewSaveLeaseAsync(lease, CancellationToken.None));
        Assert.Equal(RuntimeProcessState.Running, Assert.Single(_engine.Snapshot()).State);
    }

    [Fact]
    public async Task Crash_recovery_runs_without_panel_and_explicit_stop_cancels_backoff()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        var started = await _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1, CrashRecovery: true), CancellationToken.None);
        await _engine.CommandAsync(id, "crash", CancellationToken.None);
        RuntimeServerSnapshot snapshot = started;
        for (var i = 0; i < 200; i++)
        {
            snapshot = Assert.Single(_engine.Snapshot());
            if (snapshot.State == RuntimeProcessState.Running && snapshot.ProcessId != started.ProcessId) break;
            await Task.Delay(10);
        }
        Assert.Equal(RuntimeProcessState.Running, snapshot.State);
        Assert.NotEqual(started.ProcessId, snapshot.ProcessId);
        _engine.RecoveryDelay = TimeSpan.FromSeconds(1);
        await _engine.CommandAsync(id, "crash", CancellationToken.None);
        for (var i = 0; i < 100 && Assert.Single(_engine.Snapshot()).State != RuntimeProcessState.Crashed; i++) await Task.Delay(10);
        await _engine.StopAsync(id, false, CancellationToken.None);
        await Task.Delay(1200);
        Assert.Null(Assert.Single(_engine.Snapshot()).ProcessId);
    }

    [Fact]
    public async Task Process_without_startup_confirmation_does_not_become_ready()
    {
        if (OperatingSystem.IsWindows()) return;
        await File.WriteAllTextAsync(_fakeJava, "#!/bin/sh\nwhile IFS= read -r line; do [ \"$line\" = stop ] && exit 0; done\n");
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), CancellationToken.None));
        Assert.NotEqual(RuntimeProcessState.Running, Assert.Single(_engine.Snapshot()).State);
    }

    [Fact]
    public async Task Process_can_stop_after_console_storage_fails()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await _engine.StartAsync(new RuntimeLaunchRequest(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), CancellationToken.None);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.ConsoleDatabase}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE Lines;";
            await command.ExecuteNonQueryAsync();
        }
        await _engine.CommandAsync(id, "spam", CancellationToken.None);
        await Task.Delay(500);
        Assert.True(Assert.Single(_engine.Snapshot()).DroppedLogLines > 0);
        var stopped = await _engine.StopAsync(id, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeProcessState.Stopped, stopped.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_console_database_does_not_block_stop_or_kill(bool force)
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), default);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.ConsoleDatabase}");
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await _engine.CommandAsync(id, "spam", default);
        await Task.Delay(500);
        var stopped = await _engine.StopAsync(id, force, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeProcessState.Stopped, stopped.State);
        Assert.Null(stopped.ProcessId);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Save_on_failure_is_persisted_as_an_incident_and_retried()
    {
        var script = await File.ReadAllTextAsync(_fakeJava);
        await File.WriteAllTextAsync(_fakeJava, script.Replace("Automatic saving is now enabled", "save-on rejected"));
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await _engine.StartAsync(new(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), default);
        var lease = await _engine.AcquireSaveLeaseAsync(id, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ReleaseSaveLeaseAsync(lease, default));
        Assert.True(File.Exists(Path.Combine(_paths.Runtime, "incidents", $"{id:N}-SAVE_RESUME_FAILED.json")));
        Assert.True(File.Exists(Path.Combine(_paths.Runtime, "leases", $"{id:N}.json")));
        await _engine.StopAsync(id, true, default);
    }

    [Fact]
    public async Task Process_can_stop_when_runtime_state_directory_is_unwritable()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); Directory.CreateDirectory(_paths.Instance(id));
        await _engine.StartAsync(new RuntimeLaunchRequest(id, _fakeJava, _paths.Instance(id), ["-jar", "server.jar"], 1024, 1), CancellationToken.None);
        Directory.Delete(_paths.RuntimeState, true);
        await File.WriteAllTextAsync(_paths.RuntimeState, "simulate unavailable filesystem");
        var stopped = await _engine.StopAsync(id, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeProcessState.Stopped, stopped.State);
    }

    [Fact]
    public async Task Runtime_keeps_process_console_and_commands_independent_of_panel_connection()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid();
        var instance = _paths.Instance(id); Directory.CreateDirectory(instance);
        var started = await _engine.StartAsync(new RuntimeLaunchRequest(id, _fakeJava, instance, ["-jar", "server.jar", "nogui"], 1024, 5), CancellationToken.None);

        Assert.Equal(RuntimeProcessState.Running, started.State);
        Assert.NotNull(started.ProcessId);
        await _engine.CommandAsync(id, "ping", CancellationToken.None);
        await WaitForConsoleAsync(id, "pong while panel is absent");
        var consoleOptions = new DbContextOptionsBuilder<ConsoleDbContext>().UseSqlite($"Data Source={_paths.ConsoleDatabase}").Options;
        await using (var consoleDb = new ConsoleDbContext(consoleOptions))
            Assert.Contains(await consoleDb.Lines.Where(x => x.ServerId == id).ToListAsync(), x => x.Text.Contains("pong while panel is absent", StringComparison.Ordinal));

        var rediscovered = Assert.Single(_engine.Snapshot(), x => x.ServerId == id);
        Assert.Equal(started.ProcessId, rediscovered.ProcessId);
        Assert.Equal(RuntimeProcessState.Running, rediscovered.State);

        var stopped = await _engine.StopAsync(id, false, CancellationToken.None);
        Assert.Equal(RuntimeProcessState.Stopped, stopped.State);
        Assert.Null(stopped.ProcessId);
    }

    [Fact]
    public async Task Unexpected_exit_is_persisted_as_crashed_without_runtime_recovery()
    {
        if (OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid();
        var instance = _paths.Instance(id); Directory.CreateDirectory(instance);
        await _engine.StartAsync(new RuntimeLaunchRequest(id, _fakeJava, instance, ["-jar", "server.jar", "nogui"], 1024, 5), CancellationToken.None);
        await _engine.CommandAsync(id, "crash", CancellationToken.None);

        RuntimeServerSnapshot snapshot = null!;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            snapshot = Assert.Single(_engine.Snapshot(), x => x.ServerId == id);
            if (snapshot.State == RuntimeProcessState.Crashed) break;
            await Task.Delay(25);
        }

        Assert.Equal(RuntimeProcessState.Crashed, snapshot.State);
        Assert.Equal(7, snapshot.ExitCode);
        Assert.Null(snapshot.ProcessId);
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeState, $"{id:N}.json")));
    }

    [Fact]
    public async Task Idle_upgrade_is_acknowledged_before_runtime_shutdown_is_requested()
    {
        if (OperatingSystem.IsWindows()) return;
        var lifetime = new TestApplicationLifetime();
        var service = new RuntimeSocketService(_paths, _engine, lifetime, NullLogger<RuntimeSocketService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 100 && !File.Exists(_paths.RuntimeSocket); attempt++)
                await Task.Delay(10);

            var restarting = await PersistentRuntimeProtocol.SendAsync<bool>(
                _paths.RuntimeSocket, "upgradeWhenIdle", null, CancellationToken.None);

            Assert.True(restarting);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Idle_upgrade_rejects_new_starts_after_the_restart_is_acknowledged()
    {
        if (OperatingSystem.IsWindows()) return;
        var lifetime = new TestApplicationLifetime();
        var service = new RuntimeSocketService(_paths, _engine, lifetime, NullLogger<RuntimeSocketService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 100 && !File.Exists(_paths.RuntimeSocket); attempt++)
                await Task.Delay(10);
            var restarting = await PersistentRuntimeProtocol.SendAsync<bool>(
                _paths.RuntimeSocket, "upgradeWhenIdle", null, CancellationToken.None);
            var id = Guid.NewGuid();
            var instance = _paths.Instance(id);
            Directory.CreateDirectory(instance);

            var exception = await Assert.ThrowsAsync<PanelException>(() =>
                PersistentRuntimeProtocol.SendAsync<RuntimeServerSnapshot>(
                    _paths.RuntimeSocket, "start",
                    new RuntimeLaunchRequest(id, _fakeJava, instance, ["-jar", "server.jar", "nogui"], 1024, 5),
                    CancellationToken.None));

            Assert.True(restarting);
            Assert.Equal("RUNTIME_OPERATION_FAILED", exception.Code);
            Assert.DoesNotContain(_engine.Snapshot(), snapshot => PersistentRuntimeClient.IsActive(snapshot.State));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Busy_upgrade_request_restarts_runtime_after_the_last_server_stops()
    {
        if (OperatingSystem.IsWindows()) return;
        var lifetime = new TestApplicationLifetime();
        var service = new RuntimeSocketService(_paths, _engine, lifetime, NullLogger<RuntimeSocketService>.Instance)
        {
            UpgradePollInterval = TimeSpan.FromMilliseconds(10)
        };
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 100 && !File.Exists(_paths.RuntimeSocket); attempt++)
                await Task.Delay(10);
            var id = Guid.NewGuid();
            var instance = _paths.Instance(id); Directory.CreateDirectory(instance);
            await _engine.StartAsync(new RuntimeLaunchRequest(id, _fakeJava, instance,
                ["-jar", "server.jar", "nogui"], 1024, 5), CancellationToken.None);

            var restarting = await PersistentRuntimeProtocol.SendAsync<bool>(
                _paths.RuntimeSocket, "upgradeWhenIdle", null, CancellationToken.None);

            Assert.False(restarting);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            await _engine.StopAsync(id, false, CancellationToken.None);
            for (var attempt = 0; attempt < 100 && !lifetime.ApplicationStopping.IsCancellationRequested; attempt++)
                await Task.Delay(10);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task WaitForConsoleAsync(Guid id, string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.ConsoleDatabase}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"Lines\" WHERE \"ServerId\" = $id AND \"Text\" LIKE $text;";
            command.Parameters.AddWithValue("$id", id.ToString().ToUpperInvariant()); command.Parameters.AddWithValue("$text", $"%{expected}%");
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Runtime console output was not persisted.");
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "McPanel.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }
}
