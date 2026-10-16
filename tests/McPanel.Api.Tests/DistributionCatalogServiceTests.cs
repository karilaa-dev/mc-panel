using System.Net;
using System.Text.Json;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class DistributionCatalogServiceTests
{
    [Theory]
    [InlineData("26.3", null, 25)]
    [InlineData("26.3", 25, 25)]
    [InlineData("1.20.4", null, 17)]
    [InlineData("1.20.4", 21, 21)]
    [InlineData("1.21.11", null, 21)]
    public async Task Vanilla_install_plan_uses_metadata_or_the_version_java_requirement(string version, int? javaMajor, int expected)
    {
        using var handler = new MojangHandler(version, javaMajor);
        var catalog = new DistributionCatalogService(new ValidatedDownloadClient(new ClientFactory(handler)),
            NullLogger<DistributionCatalogService>.Instance);

        var plan = await catalog.ResolveAsync(ServerKind.Vanilla, version, null, null, null, false, default);

        Assert.Equal(expected, plan.RequiredJavaMajor);
        Assert.False(plan.Experimental);
        Assert.Equal(version, plan.Version);
        Assert.Equal("https://piston-data.mojang.com/server.jar", plan.Artifact.Url.ToString());
        Assert.Equal("sha1", plan.Artifact.HashAlgorithm);
        Assert.Equal(new string('a', 40), plan.Artifact.Hash);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MojangHandler(string version, int? javaMajor) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var metadata = new Dictionary<string, object>
            {
                ["downloads"] = new { server = new { url = "https://piston-data.mojang.com/server.jar", sha1 = new string('a', 40), size = 1234 } }
            };
            if (javaMajor.HasValue) metadata["javaVersion"] = new { majorVersion = javaMajor.Value };
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/mc/game/version_manifest_v2.json" => JsonSerializer.Serialize(new
                {
                    versions = new[] { new { id = version, type = "release", url = "https://piston-meta.mojang.com/version.json", releaseTime = "2026-09-15T00:00:00Z" } }
                }),
                "/version.json" => JsonSerializer.Serialize(metadata),
                _ => throw new InvalidOperationException($"Unexpected metadata request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
