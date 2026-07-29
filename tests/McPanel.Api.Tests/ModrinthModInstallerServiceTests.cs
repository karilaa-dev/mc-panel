using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

public sealed class ModrinthModInstallerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mcpanel-modrinth-installer-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Selected_required_dependencies_are_verified_and_installed_with_the_mod()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var stateOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "state.db")};Cache=Shared").Options;
        var consoleOptions = new DbContextOptionsBuilder<ConsoleDbContext>()
            .UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "console.db")};Cache=Shared").Options;
        var stateFactory = new StateFactory(stateOptions);
        var consoleFactory = new ConsoleFactory(consoleOptions);
        var serverId = Guid.NewGuid();
        await using (var db = await stateFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Dependency install",
                Kind = ServerKind.Fabric,
                Version = "1.21.1",
                JavaRuntimeId = "java",
                EulaAcceptedAt = DateTimeOffset.UtcNow,
                State = ServerState.Stopped
            });
            await db.SaveChangesAsync();
        }
        await using (var db = await consoleFactory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        var mainBytes = Encoding.UTF8.GetBytes("verified main mod");
        var dependencyBytes = Encoding.UTF8.GetBytes("verified dependency");
        var secondMainBytes = Encoding.UTF8.GetBytes("verified second main mod");
        var newerDependencyBytes = Encoding.UTF8.GetBytes("different dependency version");
        var handler = new ModrinthHandler(
            mainBytes, dependencyBytes, secondMainBytes, newerDependencyBytes);
        var downloads = new ValidatedDownloadClient(new StubHttpClientFactory(handler));
        var modrinth = new ModrinthService(downloads, paths, stateFactory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        await using var provider = services.BuildServiceProvider();
        var audience = new SessionAudience();
        var lifetime = new TestApplicationLifetime();
        var queue = new OperationQueue(
            provider, stateFactory, provider.GetRequiredService<IHubContext<PanelHub>>(),
            audience, lifetime, NullLogger<OperationQueue>.Instance);
        var console = new ConsoleService(
            consoleFactory, stateFactory, provider.GetRequiredService<IHubContext<PanelHub>>(),
            audience, Options.Create(options), NullLogger<ConsoleService>.Instance);
        var installer = new ModrinthModInstallerService(
            paths, modrinth, downloads, queue, new AsyncKeyedLock(), stateFactory,
            new StoppedProcessStatus(), console);

        await queue.StartAsync(CancellationToken.None);
        try
        {
            var job = await installer.QueueAsync(
                serverId,
                new InstallModrinthModRequest(
                    "main-project", "main-version", ["dependency-project"]),
                CancellationToken.None);
            var completed = await WaitForJobAsync(queue, job.Id);

            Assert.Equal(JobState.Completed, completed.State);
            Assert.Equal(mainBytes, await File.ReadAllBytesAsync(
                Path.Combine(paths.Instance(serverId), "mods", "main.jar")));
            Assert.Equal(dependencyBytes, await File.ReadAllBytesAsync(
                Path.Combine(paths.Instance(serverId), "mods", "dependency.jar")));

            var secondJob = await installer.QueueAsync(
                serverId,
                new InstallModrinthModRequest(
                    "second-main-project", "second-main-version", []),
                CancellationToken.None);
            var secondCompleted = await WaitForJobAsync(queue, secondJob.Id);

            Assert.Equal(JobState.Completed, secondCompleted.State);
            Assert.Equal(secondMainBytes, await File.ReadAllBytesAsync(
                Path.Combine(paths.Instance(serverId), "mods", "second-main.jar")));
            Assert.Equal(dependencyBytes, await File.ReadAllBytesAsync(
                Path.Combine(paths.Instance(serverId), "mods", "dependency.jar")));
            Assert.False(File.Exists(Path.Combine(
                paths.Instance(serverId), "mods", "newer-dependency.jar")));
            Assert.DoesNotContain(
                "/data/dependency-project/newer-dependency.jar",
                handler.DownloadedPaths);

            var conflictingJob = await installer.QueueAsync(
                serverId,
                new InstallModrinthModRequest(
                    "second-main-project", "second-main-version", ["dependency-project"]),
                CancellationToken.None);
            var conflict = await WaitForJobAsync(queue, conflictingJob.Id);

            Assert.Equal(JobState.Failed, conflict.State);
            Assert.Contains("already installed", conflict.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "/data/dependency-project/newer-dependency.jar",
                handler.DownloadedPaths);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<JobDto> WaitForJobAsync(OperationQueue queue, Guid jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await queue.GetAsync(jobId, CancellationToken.None);
            if (job?.State is JobState.Completed or JobState.Failed) return job;
            await Task.Delay(25);
        }
        throw new TimeoutException("The Modrinth install job did not finish.");
    }

    private static string VersionJson(
        string versionId,
        string projectId,
        string fileName,
        byte[] contents,
        string dependencies = "[]")
    {
        var hash = Convert.ToHexString(SHA512.HashData(contents));
        return $$"""
            {
              "id":"{{versionId}}","project_id":"{{projectId}}","name":"{{projectId}} 1.0",
              "version_number":"1.0.0","version_type":"release","date_published":"2026-07-01T12:00:00Z",
              "game_versions":["1.21.1"],"loaders":["fabric"],
              "files":[{
                "url":"https://cdn.modrinth.com/data/{{projectId}}/{{fileName}}",
                "filename":"{{fileName}}","size":{{contents.Length}},"primary":true,
                "hashes":{
                  "sha1":"0000000000000000000000000000000000000000",
                  "sha512":"{{hash}}"
                }
              }],
              "dependencies":{{dependencies}}
            }
            """;
    }

    private sealed class ModrinthHandler(
        byte[] mainBytes,
        byte[] dependencyBytes,
        byte[] secondMainBytes,
        byte[] newerDependencyBytes)
        : HttpMessageHandler
    {
        public ConcurrentBag<string> DownloadedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".jar", StringComparison.Ordinal))
            {
                DownloadedPaths.Add(path);
                var bytes = path switch
                {
                    "/data/main-project/main.jar" => mainBytes,
                    "/data/dependency-project/dependency.jar" => dependencyBytes,
                    "/data/second-main-project/second-main.jar" => secondMainBytes,
                    "/data/dependency-project/newer-dependency.jar" => newerDependencyBytes,
                    _ => throw new InvalidOperationException(
                        $"Unexpected Modrinth download: {request.RequestUri}")
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }
            var body = path switch
            {
                "/v2/version/main-version" => VersionJson(
                    "main-version", "main-project", "main.jar", mainBytes,
                    """[{"project_id":"dependency-project","version_id":"dependency-version","dependency_type":"required"}]"""),
                "/v2/version/dependency-version" => VersionJson(
                    "dependency-version", "dependency-project", "dependency.jar", dependencyBytes),
                "/v2/version/second-main-version" => VersionJson(
                    "second-main-version", "second-main-project", "second-main.jar",
                    secondMainBytes,
                    """[{"project_id":"dependency-project","version_id":"newer-dependency-version","dependency_type":"required"}]"""),
                "/v2/version/newer-dependency-version" => VersionJson(
                    "newer-dependency-version", "dependency-project",
                    "newer-dependency.jar", newerDependencyBytes),
                "/v2/version_files" => InstalledDependencyResponse(dependencyBytes),
                "/v2/projects" => """[{"id":"dependency-project","title":"Dependency"}]""",
                _ => throw new InvalidOperationException($"Unexpected Modrinth request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }

        private static string InstalledDependencyResponse(byte[] contents)
        {
            var hash = Convert.ToHexString(
                SHA512.HashData(contents)).ToLowerInvariant();
            return $"{{\"{hash}\":" +
                   VersionJson(
                       "dependency-version", "dependency-project",
                       "dependency.jar", contents) +
                   "}";
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StateFactory(DbContextOptions<StateDbContext> options)
        : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ConsoleFactory(DbContextOptions<ConsoleDbContext> options)
        : IDbContextFactory<ConsoleDbContext>
    {
        public ConsoleDbContext CreateDbContext() => new(options);
        public Task<ConsoleDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StoppedProcessStatus : IServerProcessStatus
    {
        public bool IsRunning(Guid id) => false;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
