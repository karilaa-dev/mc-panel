using System.Net.Sockets;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

[Collection(ApiIntegrationCollection.Name)]
public sealed class RuntimeUpgradeTests
{
    [Theory]
    [InlineData(false, RuntimeProcessState.Starting, 1)]
    [InlineData(false, RuntimeProcessState.Running, 1)]
    [InlineData(false, RuntimeProcessState.Stopping, 1)]
    [InlineData(false, RuntimeProcessState.Stopped, 0)]
    [InlineData(true, RuntimeProcessState.Running, 0)]
    public async Task Upgrade_requires_heap_only_support_or_an_idle_runtime(bool heapOnly, RuntimeProcessState state, int expectedExit)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "mc-upgrade-" + Guid.NewGuid().ToString("N"));
        var oldData = Environment.GetEnvironmentVariable("MCPANEL_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("MCPANEL_CONFIG_DIR");
        var oldError = Console.Error;
        var errors = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", root);
            Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", Path.Combine(root, "config"));
            var paths = new PanelPaths(new PanelOptions());
            paths.EnsureCreated();
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(paths.RuntimeSocket));
            listener.Listen();
            var runtime = ReplyAsync();
            Console.SetError(errors);

            Assert.Equal(expectedExit, await ProductionCommand.RunAsync(["--mcpanel-check-upgrade"]));
            await runtime;
            if (expectedExit != 0)
                Assert.Contains("Stop workloads explicitly, then retry the update; the working installation was preserved", errors.ToString());

            async Task ReplyAsync()
            {
                // An upgrade probe must only inspect the runtime, never stop a workload.
                foreach (var operation in heapOnly ? new[] { "capabilities" } : new[] { "capabilities", "snapshot" })
                {
                    using var socket = await listener.AcceptAsync(timeout.Token);
                    await using var stream = new NetworkStream(socket);
                    var request = await RuntimeWire.ReadAsync<RuntimeWireRequest>(stream, timeout.Token);
                    Assert.Equal(operation, request.Operation);
                    string[] features = heapOnly
                        ? ["save-leases", "gate-feature-memory", "minecraft-heap-only"]
                        : ["save-leases", "gate-feature-memory"];
                    var payload = operation == "capabilities"
                        ? RuntimeWire.Element(new RuntimeCapabilities(RuntimeWire.Version, SchemaMigration.ConsoleVersion, "fixture", features))
                        : RuntimeWire.Element(new[] { new { serverId = Guid.NewGuid(), state } });
                    await RuntimeWire.WriteAsync(stream,
                        new RuntimeWireResponse(RuntimeWire.Version, request.RequestId, true, null, payload), timeout.Token);
                }
            }
        }
        finally
        {
            Console.SetError(oldError);
            Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", oldConfig);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
