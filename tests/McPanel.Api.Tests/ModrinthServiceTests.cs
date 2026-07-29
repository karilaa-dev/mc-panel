using System.Net;
using System.Security.Cryptography;
using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class ModrinthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-modrinth-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mod_search_uses_selected_filters_and_page_size()
    {
        var (service, serverId, handler) = CreateService(ServerKind.Fabric, """
            {"hits":[],"offset":0,"limit":5,"total_hits":0}
            """);

        var result = await service.SearchAsync(
            "mod", "storage", 0, 5, serverId, "1.20.4", "forge", CancellationToken.None);

        Assert.Equal(5, result.Limit);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("project_type:mod", query);
        Assert.Contains("versions:1.20.4", query);
        Assert.Contains("categories:forge", query);
        Assert.Contains("limit=5", query);
    }

    [Fact]
    public async Task Plugin_search_uses_plugin_facet_and_returns_catalog_artwork()
    {
        var response = """
            {
              "hits":[{
                "project_id":"plugin-id","slug":"example","title":"Example Plugin",
                "description":"Paper utilities","project_type":"mod","author":"Ada",
                "icon_url":"https://cdn.modrinth.com/data/plugin-id/icon.png",
                "downloads":1200,"versions":["1.21.1"],"categories":["paper","utility"],
                "featured_gallery":"https://cdn.modrinth.com/data/plugin-id/images/hero.png",
                "follows":42,"date_modified":"2026-07-01T12:00:00Z"
              }],
              "offset":0,"limit":20,"total_hits":1
            }
            """;
        var (service, serverId, handler) = CreateService(ServerKind.Paper, response);

        var result = await service.SearchAsync(
            "plugin", "", 0, null, serverId, null, null, CancellationToken.None);

        var plugin = Assert.Single(result.Projects);
        Assert.Equal("plugin", plugin.ProjectType);
        Assert.Equal(42, plugin.Followers);
        Assert.EndsWith("hero.png", plugin.FeaturedGalleryUrl);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Requests).Query);
        Assert.Contains("all_project_types:plugin", query);
        Assert.Contains("versions:1.21.1", query);
        Assert.Contains("categories:paper", query);
    }

    [Fact]
    public async Task Plugin_search_rejects_non_paper_servers()
    {
        var (service, serverId, _) = CreateService(ServerKind.Forge, """
            {"hits":[],"offset":0,"limit":20,"total_hits":0}
            """);

        var exception = await Assert.ThrowsAsync<PanelException>(() => service.SearchAsync(
            "plugin", "", 0, null, serverId, null, null, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    [Fact]
    public async Task Paper_accepts_bukkit_compatible_plugin_versions()
    {
        var response = """
            {
              "id":"version-id","project_id":"plugin-id","name":"Example Plugin 1.0",
              "version_number":"1.0.0","version_type":"release","date_published":"2026-07-01T12:00:00Z",
              "game_versions":["1.21.1"],"loaders":["bukkit"],
              "files":[{
                "url":"https://cdn.modrinth.com/data/plugin-id/example.jar",
                "filename":"example.jar","size":100,"primary":true,
                "hashes":{
                  "sha1":"0000000000000000000000000000000000000000",
                  "sha512":"00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
                }
              }],
              "dependencies":[]
            }
            """;
        var (service, serverId, _) = CreateService(ServerKind.Paper, response);

        var resolved = await service.ResolvePluginAsync(
            serverId, "plugin-id", "version-id", CancellationToken.None);

        Assert.Equal("example.jar", resolved.File.FileName);
        Assert.Equal("1.0.0", resolved.Version.Number);
    }

    [Fact]
    public async Task Versions_resolve_required_dependency_names_and_links()
    {
        var versions = """
            [{
              "id":"version-id","project_id":"project-id","name":"Example 1.0",
              "version_number":"1.0.0","version_type":"release","date_published":"2026-07-01T12:00:00Z",
              "game_versions":["1.21.1"],"loaders":["fabric"],"files":[],
              "dependencies":[
                {"project_id":"dependency-project","dependency_type":"required"},
                {"version_id":"dependency-version","dependency_type":"required"},
                {"file_name":"external-library.jar","dependency_type":"required"}
              ]
            }]
            """;
        var dependencyVersions = """
            [{"id":"dependency-version","project_id":"version-project"}]
            """;
        var projects = """
            [
              {"id":"dependency-project","title":"Dependency Project"},
              {"id":"version-project","title":"Version Dependency"}
            ]
            """;
        var (service, serverId, handler) = CreateService(ServerKind.Fabric, request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/v2/versions" => dependencyVersions,
                "/v2/projects" => projects,
                _ => versions
            });

        var result = await service.VersionsAsync(
            "project-id", serverId, "mod", null, null, CancellationToken.None);

        var dependencies = Assert.Single(result).Dependencies;
        Assert.Collection(dependencies,
            dependency =>
            {
                Assert.Equal("Dependency Project", dependency.ProjectTitle);
                Assert.Equal(
                    "https://modrinth.com/project/dependency-project",
                    dependency.ProjectUrl);
            },
            dependency =>
            {
                Assert.Equal("version-project", dependency.ProjectId);
                Assert.Equal("Version Dependency", dependency.ProjectTitle);
                Assert.Equal(
                    "https://modrinth.com/project/version-project",
                    dependency.ProjectUrl);
            },
            dependency =>
            {
                Assert.Equal("external-library.jar", dependency.FileName);
                Assert.Null(dependency.ProjectTitle);
                Assert.Null(dependency.ProjectUrl);
            });
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Versions_report_installed_dependency_projects_by_jar_hash()
    {
        var installedBytes = Encoding.UTF8.GetBytes("installed dependency artifact");
        var hash = Convert.ToHexString(SHA512.HashData(installedBytes)).ToLowerInvariant();
        var parentVersion = VersionJson(
            "parent-version", "parent-project", "parent.jar",
            """[{"project_id":"dependency-project","version_id":"required-version","dependency_type":"required"}]""");
        var installedVersion = VersionJson(
            "installed-version", "dependency-project", "dependency-old.jar");
        var (service, serverId, handler) = CreateService(
            ServerKind.Fabric,
            request => request.RequestUri!.AbsolutePath switch
            {
                "/v2/project/parent-project/version" => $"[{parentVersion}]",
                "/v2/projects" => """[{"id":"dependency-project","title":"Dependency"}]""",
                "/v2/version_files" => $"{{\"{hash}\":{installedVersion}}}",
                _ => throw new InvalidOperationException(
                    $"Unexpected Modrinth request: {request.RequestUri}")
            },
            (paths, id) =>
            {
                var directory = Path.Combine(paths.Instance(id), "mods");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(
                    Path.Combine(directory, "renamed-installed-dependency.jar"),
                    installedBytes);
            });

        var versions = await service.VersionsAsync(
            "parent-project", serverId, "mod", null, null, CancellationToken.None);

        var installed = Assert.Single(Assert.Single(versions).Dependencies).InstalledVersions;
        var version = Assert.Single(installed);
        Assert.Equal("installed-version", version.VersionId);
        Assert.Equal("1.0.0", version.VersionNumber);
        Assert.Equal("renamed-installed-dependency.jar", version.FileName);
        Assert.Contains(handler.Requests, request =>
            request.AbsolutePath == "/v2/version_files");
        Assert.Contains(handler.Methods, method => method == HttpMethod.Post);
    }

    [Fact]
    public async Task Selected_dependencies_resolve_fixed_and_latest_compatible_versions()
    {
        var fixedVersion = VersionJson("fixed-version", "fixed-project", "fixed.jar");
        var latestVersion = VersionJson("latest-version", "latest-project", "latest.jar");
        var (service, serverId, handler) = CreateService(ServerKind.Fabric, request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/v2/projects" => """
                    [
                      {"id":"fixed-project","title":"Fixed Dependency"},
                      {"id":"latest-project","title":"Latest Dependency"}
                    ]
                    """,
                "/v2/project/latest-project/version" => $"[{latestVersion}]",
                "/v2/version/fixed-version" => fixedVersion,
                "/v2/version/latest-version" => latestVersion,
                _ => throw new InvalidOperationException($"Unexpected Modrinth request: {request.RequestUri}")
            });
        var parent = new ModrinthVersion(
            "parent-version", "parent-project", "Parent", "1.0.0", "release",
            DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            ["1.21.1"], ["fabric"], [],
            [
                new("required", "fixed-project", "fixed-version", null, null, null, []),
                new("required", "latest-project", null, null, null, null, [])
            ]);

        var dependencies = await service.ResolveDependenciesAsync(
            serverId, parent, ["fixed-project", "latest-project"], false, CancellationToken.None);

        Assert.Collection(dependencies,
            dependency =>
            {
                Assert.Equal("fixed-version", dependency.Version.Id);
                Assert.Equal("fixed.jar", dependency.File.FileName);
            },
            dependency =>
            {
                Assert.Equal("latest-version", dependency.Version.Id);
                Assert.Equal("latest.jar", dependency.File.FileName);
            });
        Assert.Contains(handler.Requests, request =>
            request.AbsolutePath == "/v2/project/latest-project/version" &&
            Uri.UnescapeDataString(request.Query).Contains("game_versions=[\"1.21.1\"]"));
    }

    [Fact]
    public async Task Selected_dependencies_reject_projects_not_declared_by_parent()
    {
        var (service, serverId, _) = CreateService(ServerKind.Fabric, """
            [{"id":"required-project","title":"Required Dependency"}]
            """);
        var parent = new ModrinthVersion(
            "parent-version", "parent-project", "Parent", "1.0.0", "release",
            DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            ["1.21.1"], ["fabric"], [],
            [new("required", "required-project", null, null, null, null, [])]);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.ResolveDependenciesAsync(
                serverId, parent, ["unrelated-project"], false, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    private static string VersionJson(
        string versionId,
        string projectId,
        string fileName,
        string dependencies = "[]") => $$"""
        {
          "id":"{{versionId}}","project_id":"{{projectId}}","name":"{{projectId}} 1.0",
          "version_number":"1.0.0","version_type":"release","date_published":"2026-07-01T12:00:00Z",
          "game_versions":["1.21.1"],"loaders":["fabric"],
          "files":[{
            "url":"https://cdn.modrinth.com/data/{{projectId}}/{{fileName}}",
            "filename":"{{fileName}}","size":100,"primary":true,
            "hashes":{
              "sha1":"0000000000000000000000000000000000000000",
              "sha512":"00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            }
          }],
          "dependencies":{{dependencies}}
        }
        """;

    private (ModrinthService Service, Guid ServerId, RecordingHandler Handler) CreateService(
        ServerKind kind, string response) => CreateService(kind, _ => response);

    private (ModrinthService Service, Guid ServerId, RecordingHandler Handler) CreateService(
        ServerKind kind, Func<HttpRequestMessage, string> response)
        => CreateService(kind, response, null);

    private (ModrinthService Service, Guid ServerId, RecordingHandler Handler) CreateService(
        ServerKind kind,
        Func<HttpRequestMessage, string> response,
        Action<PanelPaths, Guid>? arrange)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, suffix, "data"),
            ConfigDirectory = Path.Combine(_root, suffix, "config")
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "state.db")}").Options;
        var factory = new TestStateDbContextFactory(dbOptions);
        var serverId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Catalog",
                Kind = kind,
                Version = "1.21.1",
                JavaRuntimeId = "java",
                EulaAcceptedAt = DateTimeOffset.UtcNow,
                State = ServerState.Stopped
            });
            db.SaveChanges();
        }
        arrange?.Invoke(paths, serverId);
        var handler = new RecordingHandler(response);
        var client = new ValidatedDownloadClient(new StubHttpClientFactory(handler));
        return (new ModrinthService(client, paths, factory), serverId, handler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request))
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options)
        : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
