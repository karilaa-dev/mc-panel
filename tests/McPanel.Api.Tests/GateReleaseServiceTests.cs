using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class GateReleaseServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-gate-release-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PanelPaths _paths;
    private readonly Guid _serverId = Guid.NewGuid();

    public GateReleaseServiceTests()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = Path.Combine(_root, "data"), ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
    }

    [Fact]
    public async Task Selects_complete_stable_release_for_the_exact_host_architecture()
    {
        var payload = Script("1.2.3");
        var service = Service("1.2.3", payload, includeIncompleteStableFirst: true);

        var release = await service.LatestAsync(CancellationToken.None);

        Assert.Equal("1.2.3", release.Version);
        Assert.Equal($"gate_1.2.3_linux_{ArchitectureName()}", release.AssetName);
    }

    [Fact]
    public async Task Checksum_mismatch_uses_the_stable_Gate_error_and_does_not_activate()
    {
        var service = Service("1.2.3", Script("1.2.3"), checksumOverride: new string('0', 64));

        var exception = await Assert.ThrowsAsync<PanelException>(() => service.InstallLatestAsync(_serverId, CancellationToken.None));

        Assert.Equal("GATE_CHECKSUM_MISMATCH", exception.Code);
        Assert.False(File.Exists(_paths.GateInstallManifest(_serverId)));
    }

    [Fact]
    public async Task Verified_versions_activate_atomically_and_previous_version_can_be_restored()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = await Service("1.2.3", Script("1.2.3")).InstallLatestAsync(_serverId, CancellationToken.None);
        var secondService = Service("1.3.0", Script("1.3.0"));
        var second = await secondService.InstallLatestAsync(_serverId, CancellationToken.None);

        Assert.Equal("1.2.3", second.PreviousVersion);
        Assert.True(File.Exists(first.Executable));
        Assert.True(File.Exists(second.Executable));

        await secondService.RestorePreviousAsync(_serverId, CancellationToken.None);
        Assert.Equal("1.2.3", secondService.Installed(_serverId)!.Version);
        Assert.Equal(first.Executable, secondService.Installed(_serverId)!.Executable);
    }

    private GateReleaseService Service(
        string version, byte[] binary, string? checksumOverride = null,
        bool includeIncompleteStableFirst = false)
    {
        var asset = $"gate_{version}_linux_{ArchitectureName()}";
        var checksum = checksumOverride ?? Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
        var releases = new List<string>
        {
            "{\"draft\":false,\"prerelease\":true,\"tag_name\":\"v9.9.9\",\"assets\":[]}" 
        };
        if (includeIncompleteStableFirst)
            releases.Add("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v2.0.0\",\"assets\":[{\"name\":\"checksums.txt\",\"browser_download_url\":\"https://github.com/minekube/gate/releases/download/v2.0.0/checksums.txt\"}]}");
        releases.Add($$"""
            {"draft":false,"prerelease":false,"tag_name":"v{{version}}","assets":[
              {"name":"{{asset}}","browser_download_url":"https://github.com/minekube/gate/releases/download/v{{version}}/{{asset}}","size":{{binary.Length}}},
              {"name":"checksums.txt","browser_download_url":"https://github.com/minekube/gate/releases/download/v{{version}}/checksums.txt"}
            ]}
            """);
        var releasesJson = "[" + string.Join(',', releases) + "]";
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "api.github.com") return Text(releasesJson, "application/json");
            if (path.EndsWith("checksums.txt", StringComparison.Ordinal)) return Text($"{checksum}  {asset}\n", "text/plain");
            if (path.EndsWith('/' + asset, StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        return new GateReleaseService(new ValidatedDownloadClient(new StubFactory(handler)), _paths);
    }

    private static byte[] Script(string version) => Encoding.UTF8.GetBytes($"#!/bin/sh\nprintf 'gate version {version}\\n'\n");

    private static string ArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => throw new InvalidOperationException("Gate supports Linux x64 and arm64 hosts.")
    };

    private static HttpResponseMessage Text(string value, string contentType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, contentType) };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
