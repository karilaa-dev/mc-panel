using System.Net;
using System.Net.Sockets;
using McPanel.Api.Configuration;
using McPanel.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class GatePersistentRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-gate-runtime-" + Guid.NewGuid().ToString("N"));
    private PanelPaths _paths = null!;
    private RuntimeEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = Path.Combine(_root, "data"), ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
        _engine = new RuntimeEngine(_paths,
            new CgroupMemoryService(new TestEnvironment(), NullLogger<CgroupMemoryService>.Instance),
            NullLogger<RuntimeEngine>.Instance);
        await _engine.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Multiple_typed_Gate_workloads_start_and_stop_independently()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = await LaunchAsync(Guid.NewGuid(), FreePort());
        var second = (await LaunchAsync(Guid.NewGuid(), FreePort())) with { MemoryLimitMb = 1536 };

        var firstStarted = await _engine.StartAsync(first, CancellationToken.None);
        var secondStarted = await _engine.StartAsync(second, CancellationToken.None);
        Assert.Equal(RuntimeProcessState.Running, firstStarted.State);
        Assert.Equal(RuntimeProcessState.Running, secondStarted.State);

        var firstStopped = await _engine.StopAsync(first.ServerId, false, CancellationToken.None);
        Assert.Equal(RuntimeProcessState.Stopped, firstStopped.State);
        Assert.Equal(RuntimeProcessState.Running, _engine.Snapshot().Single(x => x.ServerId == second.ServerId).State);

        var secondStopped = await _engine.StopAsync(second.ServerId, false, CancellationToken.None);
        Assert.Equal(RuntimeProcessState.Stopped, secondStopped.State);
    }

    private async Task<RuntimeLaunchRequest> LaunchAsync(Guid id, int port)
    {
        var version = Path.Combine(_paths.GateVersions(id), "test");
        Directory.CreateDirectory(version);
        var executable = Path.Combine(version, "gate");
        await File.WriteAllTextAsync(executable, """
            #!/usr/bin/python3
            import http.server, sys
            port = int(sys.argv[2])
            class Handler(http.server.BaseHTTPRequestHandler):
                def log_message(self, format, *args): pass
                def do_POST(self):
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", "2")
                    self.end_headers()
                    self.wfile.write(b"{}")
            http.server.HTTPServer(("127.0.0.1", port), Handler).serve_forever()
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeLaunchRequest(id, executable, _paths.Instance(id), ["--api-port", port.ToString()],
            256, 15, RuntimeWorkloadKind.Gate, port);
    }

    public async Task DisposeAsync()
    {
        try { await _engine.DisposeAsync(); } catch { }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "McPanel.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
