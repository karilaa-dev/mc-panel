using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiIntegrationCollection
{
    public const string Name = "API integration";
}

[Collection(ApiIntegrationCollection.Name)]
public sealed class ApiValidationRegressionTests : IAsyncLifetime
{
    private const string SetupToken = "validation-regression-setup-token";
    private const string JavaId = "validation-test-java";
    private const int MaxUploadBytes = 1_024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-validation-api-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _serverId = Guid.NewGuid();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private PanelPaths? _paths;
    private string _fakeJavaPath = null!;

    [Fact]
    public async Task Panel_shutdown_policy_defaults_to_preserve_and_is_persisted()
    {
        string revision;
        using (var initial = await _client!.GetAsync("/api/v1/system/settings"))
        {
            initial.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
            Assert.True(document.RootElement.GetProperty("keepServersRunningOnPanelStop").GetBoolean());
            revision = document.RootElement.GetProperty("revision").GetString()!;
        }
        using (var changed = await SendJsonAsync(HttpMethod.Put, "/api/v1/system/settings", $$"""{"keepServersRunningOnPanelStop":false,"revision":"{{revision}}"}"""))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using (var saved = await _client.GetAsync("/api/v1/system/settings"))
        {
            using var document = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
            Assert.False(document.RootElement.GetProperty("keepServersRunningOnPanelStop").GetBoolean());
            revision = document.RootElement.GetProperty("revision").GetString()!;
        }
        using var restored = await SendJsonAsync(HttpMethod.Put, "/api/v1/system/settings", $$"""{"keepServersRunningOnPanelStop":true,"revision":"{{revision}}"}""");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task Repeated_create_key_returns_the_committed_server_and_job()
    {
        var requestId = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(new
        {
            name = "Idempotent server",
            kind = "Vanilla",
            version = "1.20.4",
            javaRuntimeId = JavaId,
            memoryMb = PanelOptions.MinimumServerMemoryMb,
            port = 32124,
            eulaAccepted = true,
            clientRequestId = requestId
        });
        using var first = await SendJsonAsync(HttpMethod.Post, "/api/v1/servers", json);
        using var second = await SendJsonAsync(HttpMethod.Post, "/api/v1/servers",
            json.Replace(requestId, requestId.ToUpperInvariant(), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(firstDocument.RootElement.GetProperty("id").GetGuid(), secondDocument.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(firstDocument.RootElement.GetProperty("serverId").GetGuid(), secondDocument.RootElement.GetProperty("serverId").GetGuid());
    }

    [Fact]
    public async Task Committed_delete_returns_success_and_removes_staged_server_files()
    {
        var backup = _paths!.ServerBackups(_serverId);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "keep-until-commit.txt"), "backup");

        using var deleted = await SendJsonAsync(HttpMethod.Delete, $"/api/v1/servers/{_serverId}", "{}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await _client!.GetAsync($"/api/v1/servers/{_serverId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(Directory.Exists(_paths.Instance(_serverId)));
        Assert.False(Directory.Exists(backup));
    }

    public async Task InitializeAsync()
    {
        var data = Path.Combine(_root, "data");
        var config = Path.Combine(_root, "config");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Panel:DataDirectory", data);
            builder.UseSetting("Panel:ConfigDirectory", config);
            builder.UseSetting("Panel:SetupToken", SetupToken);
            builder.UseSetting("Panel:MaxUploadBytes", MaxUploadBytes.ToString());
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = _factory.Services.CreateScope())
        {
            _paths = scope.ServiceProvider.GetRequiredService<PanelPaths>();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<PanelOptions>>().Value;
            Assert.Equal(Path.GetFullPath(data), _paths.Data);
            Assert.Equal(MaxUploadBytes, options.MaxUploadBytes);
        }

        _fakeJavaPath = Path.Combine(_root, "fake-java");
        if (!OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(_fakeJavaPath, """
#!/bin/sh
if [ "${1:-}" = "-XshowSettings:properties" ]; then
    printf '%s\n' 'java.version = 21.0.1' 'java.vendor = Test' 'os.arch = amd64' >&2
    exit 0
fi
printf '%s\n' 'Done (0.01s)! For help, type "help"'
while IFS= read -r line; do
    case "$line" in
        "save-all flush") printf '%s\n' 'Saved the game' ;;
        crash) printf '%s\n' 'Simulated crash'; exit 1 ;;
        stop) printf '%s\n' 'Stopping server'; exit 0 ;;
    esac
done
""");
            File.SetUnixFileMode(_fakeJavaPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (var setup = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/setup", JsonSerializer.Serialize(new
               {
                   token = SetupToken,
                   username = "validation_admin",
                   password = "validation-test-password"
               })))
        {
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        }

        await using var seedScope = _factory.Services.CreateAsyncScope();
        var stateFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        db.JavaRuntimes.Add(new JavaRuntimeEntity
        {
            Id = JavaId,
            Path = _fakeJavaPath,
            Version = "21.0.0",
            Major = 21,
            Vendor = "Test",
            Architecture = "x64",
            IsCustom = true
        });
        db.Servers.Add(new ServerEntity
        {
            Id = _serverId,
            Name = "Validation Server",
            Kind = ServerKind.Paper,
            Version = "1.20.4",
            State = ServerState.Stopped,
            Port = 32_123,
            MemoryMb = PanelOptions.MinimumServerMemoryMb,
            InitialMemoryMb = PanelOptions.MinimumServerMemoryMb,
            MemoryLimitMb = PanelOptions.MinimumServerTotalMemoryMb,
            JavaRuntimeId = JavaId,
            EulaAcceptedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        Directory.CreateDirectory(_paths.Instance(_serverId));
        await File.WriteAllBytesAsync(Path.Combine(_paths.Instance(_serverId), "server.jar"), []);
        await File.WriteAllTextAsync(Path.Combine(_paths.Instance(_serverId), "server.properties"), "server-port=32123\n");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public async Task Malformed_or_null_json_returns_structured_validation_problem(string body)
    {
        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/files", body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Upload_accepts_exact_file_limit_and_rejects_one_byte_over()
    {
        using (var exact = await UploadAsync("exact.bin", new byte[MaxUploadBytes]))
            Assert.Equal(HttpStatusCode.NoContent, exact.StatusCode);
        Assert.Equal(MaxUploadBytes, new FileInfo(Path.Combine(_paths!.Instance(_serverId), "exact.bin")).Length);

        using var over = await UploadAsync("over.bin", new byte[MaxUploadBytes + 1]);
        await AssertProblemAsync(over, HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE");
        Assert.False(File.Exists(Path.Combine(_paths.Instance(_serverId), "over.bin")));
    }

    [Fact]
    public async Task Multipart_parser_size_failure_returns_file_too_large_problem()
    {
        using var response = await UploadAsync("form-limit.bin", new byte[MaxUploadBytes + 1024 * 1024 + 1]);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE");
        Assert.False(File.Exists(Path.Combine(_paths!.Instance(_serverId), "form-limit.bin")));
    }

    [Fact]
    public async Task Unknown_lifecycle_action_is_rejected_without_enqueuing_a_job()
    {
        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/actions/dance", "{}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Mods_endpoint_rejects_unknown_and_non_modded_servers_and_returns_mixed_inventory()
    {
        using (var unknown = await _client!.GetAsync($"/api/v1/servers/{Guid.NewGuid()}/mods"))
            await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");
        using (var paper = await _client!.GetAsync($"/api/v1/servers/{_serverId}/mods"))
            await AssertProblemAsync(paper, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var fabricId = Guid.NewGuid();
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = fabricId, Name = "Fabric inventory", Kind = ServerKind.Fabric, Version = "1.20.4",
                State = ServerState.Stopped, Port = 32_124, MemoryMb = 512, InitialMemoryMb = 512,
                JavaRuntimeId = JavaId, EulaAcceptedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var mods = Path.Combine(_paths!.Instance(fabricId), "mods");
        Directory.CreateDirectory(mods);
        using (var archive = ZipFile.Open(Path.Combine(mods, "valid.jar"), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{\"id\":\"valid\",\"name\":\"Valid Mod\",\"version\":\"1.0\"}");
        }
        await File.WriteAllTextAsync(Path.Combine(mods, "broken.jar"), "not a jar");

        using var response = await _client.GetAsync($"/api/v1/servers/{fabricId}/mods");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Contains(json.RootElement.EnumerateArray(), x => x.GetProperty("status").GetString() == "Parsed");
        Assert.Contains(json.RootElement.EnumerateArray(), x => x.GetProperty("status").GetString() == "Invalid");
    }

    [Fact]
    public async Task Server_creation_rejects_less_than_heap_memory_minimum_without_enqueuing_a_job()
    {
        var request = JsonSerializer.Serialize(new
        {
            name = "Too Small",
            kind = "Paper",
            version = "1.20.4",
            javaRuntimeId = JavaId,
            memoryMb = 256,
            port = 32_124,
            eulaAccepted = true,
            build = "499",
            includeExperimental = false
        });

        using var response = await SendJsonAsync(HttpMethod.Post, "/api/v1/servers", request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        Assert.Single(await db.Servers.ToListAsync());
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Server_with_minimum_heap_and_total_limit_starts_and_stops()
    {
        Assert.Equal(512, PanelOptions.MinimumServerMemoryMb);
        try
        {
            var job = await QueueActionAndWaitAsync("start");
            Assert.Equal("Completed", job.GetProperty("state").GetString());
            await WaitForServerAsync(server => server.State == ServerState.Running);
        }
        finally { await StopManagedServerAsync(); }
        Assert.Equal(ServerState.Stopped, (await ReadServerAsync()).State);
    }

    [Fact]
    public async Task Schedule_name_accepts_limit_and_rejects_limit_plus_one_and_controls()
    {
        var validName = new string('s', 96);
        using (var accepted = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules", Schedule(validName)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            Assert.Equal(validName, document.RootElement.GetProperty("name").GetString());
        }

        using (var tooLong = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules", Schedule(new string('s', 97))))
            await AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        using (var control = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules", Schedule("nightly\nrestart")))
            await AssertProblemAsync(control, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        Assert.Equal(validName, Assert.Single(await db.Schedules.ToListAsync()).Name);
    }

    [Theory]
    [InlineData("/api/v1")]
    [InlineData("/api")]
    public async Task Schedule_rejects_null_action_elements_with_a_structured_problem(string prefix)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = "Null action",
            frequency = "Interval",
            timeZone = "UTC",
            enabled = false,
            intervalMinutes = 5,
            actions = new object?[] { null }
        });

        using var response = await SendJsonAsync(HttpMethod.Post, $"{prefix}/servers/{_serverId}/schedules", body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Schedule_rejects_unsupported_frequency_and_bounded_text_fields()
    {
        using (var frequency = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Bad frequency", frequency: "unsupported")))
            await AssertProblemAsync(frequency, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using (var timeZone = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Long time zone", timeZone: new string('z', 129))))
            await AssertProblemAsync(timeZone, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using (var timeZoneControl = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Control time zone", timeZone: "UTC\n")))
            await AssertProblemAsync(timeZoneControl, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using (var cron = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Long cron", frequency: "Cron", cron: new string('*', 257))))
            await AssertProblemAsync(cron, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using var cronControl = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
            Schedule("Control cron", frequency: "Cron", cron: "0 0 * * *\n"));
        await AssertProblemAsync(cronControl, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Schedule_rejects_duplicate_invalid_and_irrelevant_oversized_day_lists()
    {
        using (var duplicate = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Duplicate days", frequency: "Weekly", timeOfDay: "04:30", daysOfWeek: [1, 1])))
            await AssertProblemAsync(duplicate, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using (var invalid = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
                   Schedule("Invalid day", frequency: "Weekly", timeOfDay: "04:30", daysOfWeek: [7])))
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using var oversized = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules",
            Schedule("Irrelevant oversized days", daysOfWeek: [0, 1, 2, 3, 4, 5, 6, 0]));
        await AssertProblemAsync(oversized, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Inventory_backup_schedule_targets_all_saved_players_without_a_uuid()
    {
        var valid = JsonSerializer.Serialize(new
        {
            name = "Player inventory",
            frequency = "Interval",
            timeZone = "UTC",
            enabled = false,
            intervalMinutes = 30,
            actions = new[] { new { action = "InventoryBackup" } }
        });
        using (var accepted = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/schedules", valid))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData("motd", 513)]
    [InlineData("worldName", 129)]
    [InlineData("jvmArguments", 2049)]
    public async Task Configuration_rejects_values_one_character_over_limit(string property, int length)
    {
        var configuration = ValidConfiguration();
        configuration[property] = new string('x', length);

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Configuration_accepts_values_exactly_at_limits()
    {
        var configuration = ValidConfiguration();
        configuration["motd"] = new string('m', 512);
        configuration["worldName"] = new string('w', 128);
        configuration["jvmArguments"] = "-Dnote=" + new string('x', 2048 - "-Dnote=".Length);

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(512, document.RootElement.GetProperty("motd").GetString()!.Length);
        Assert.Equal(128, document.RootElement.GetProperty("worldName").GetString()!.Length);
        Assert.Equal(2048, document.RootElement.GetProperty("jvmArguments").GetString()!.Length);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        Assert.Equal(2048, (await db.Servers.SingleAsync(x => x.Id == _serverId)).JvmArguments.Length);
    }

    [Fact]
    public async Task Configuration_rejects_less_than_heap_memory_minimum_and_accepts_minimum()
    {
        var configuration = ValidConfiguration();
        configuration["memoryMb"] = PanelOptions.MinimumServerMemoryMb - 256;
        using (var rejected = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString()))
            await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        configuration["memoryMb"] = PanelOptions.MinimumServerMemoryMb;
        using var accepted = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.Equal(PanelOptions.MinimumServerMemoryMb, document.RootElement.GetProperty("memoryMb").GetInt32());
    }

    [Fact]
    public async Task Properties_preserve_dynamic_content_sync_port_and_reject_stale_revisions()
    {
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        await File.WriteAllTextAsync(file, "# generated\nunknown-option=keep\nmotd=old\nmotd=effective\nserver-port=32123\n\n");
        using var loaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/properties");
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        using var loadedJson = JsonDocument.Parse(await loaded.Content.ReadAsStringAsync());
        var revision = loadedJson.RootElement.GetProperty("revision").GetString()!;
        Assert.Equal(["unknown-option", "motd", "server-port"], loadedJson.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("key").GetString()!));

        var body = JsonSerializer.Serialize(new
        {
            revision,
            values = new Dictionary<string, string>
            {
                ["unknown-option"] = "keep",
                ["motd"] = "updated",
                ["server-port"] = "32124"
            }
        });
        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", body))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("# generated", text);
        Assert.Contains("motd=old", text);
        Assert.Contains("motd=updated", text);
        Assert.Contains($"server-port=32124{Environment.NewLine}{Environment.NewLine}", text);
        Assert.Equal(32124, (await ReadServerAsync()).Port);

        using var stale = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", body);
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONFIGURATION_CHANGED");
    }

    [Fact]
    public async Task Properties_add_catalogued_keys_and_require_version_acknowledgement()
    {
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        await File.WriteAllTextAsync(file, "# keep\nunknown-option=keep\nserver-port=32123\n");
        using var loaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/properties");
        using var loadedJson = JsonDocument.Parse(await loaded.Content.ReadAsStringAsync());
        var revision = loadedJson.RootElement.GetProperty("revision").GetString()!;
        Assert.Equal("1.20.4", loadedJson.RootElement.GetProperty("minecraftVersion").GetString());
        Assert.Contains(loadedJson.RootElement.GetProperty("available").EnumerateArray(),
            item => item.GetProperty("key").GetString() == "simulation-distance" && item.GetProperty("compatibility").GetString() == "Supported");

        var compatible = JsonSerializer.Serialize(new
        {
            revision,
            values = new Dictionary<string, string> { ["simulation-distance"] = "10" }
        });
        using var added = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", compatible);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.EndsWith($"simulation-distance=10{Environment.NewLine}", await File.ReadAllTextAsync(file));
        using var addedJson = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var nextRevision = addedJson.RootElement.GetProperty("revision").GetString()!;

        var incompatible = JsonSerializer.Serialize(new
        {
            revision = nextRevision,
            values = new Dictionary<string, string> { ["accepts-transfers"] = "false" }
        });
        using (var rejected = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", incompatible))
            await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "PROPERTY_VERSION_ACKNOWLEDGEMENT_REQUIRED");

        var acknowledged = JsonSerializer.Serialize(new
        {
            revision = nextRevision,
            values = new Dictionary<string, string> { ["accepts-transfers"] = "false" },
            acknowledgedIncompatibleKeys = new[] { "accepts-transfers" }
        });
        using var accepted = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", acknowledged);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("# keep", text);
        Assert.Contains("unknown-option=keep", text);
        Assert.EndsWith($"accepts-transfers=false{Environment.NewLine}", text);

        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var arbitrary = JsonSerializer.Serialize(new
        {
            revision = acceptedJson.RootElement.GetProperty("revision").GetString(),
            values = new Dictionary<string, string> { ["arbitrary-plugin-key"] = "value" }
        });
        using var arbitraryResponse = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", arbitrary);
        await AssertProblemAsync(arbitraryResponse, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Colored_player_events_reconcile_uuid_and_remove_legacy_ansi_leak()
    {
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            db.Players.AddRange(
                new PlayerEntity { ServerId = _serverId, Name = "11mKaRiLaA", Online = true },
                new PlayerEntity { ServerId = _serverId, Name = "KaRiLaA", Uuid = "b67e9dfc-d4d7-4f31-b30b-e1ab374cb0ee" });
            await db.SaveChangesAsync();
        }
        using (var listed = await _client!.GetAsync($"/api/v1/servers/{_serverId}/players"))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            Assert.Equal("KaRiLaA", Assert.Single(listedJson.RootElement.EnumerateArray()).GetProperty("name").GetString());
        }
        var console = _factory!.Services.GetRequiredService<ConsoleService>();
        await Task.WhenAll(
            console.AppendAsync(_serverId, "stdout", "UUID of player KaRiLaA is b67e9dfc-d4d7-4f31-b30b-e1ab374cb0ee"),
            console.AppendAsync(_serverId, "stdout", "[Server thread/INFO]: \u001b[38;5;11mKaRiLaA\u001b[0m joined the game"));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var verify = await verifyFactory.CreateDbContextAsync();
        var player = Assert.Single(await verify.Players.Where(x => x.ServerId == _serverId).ToListAsync());
        Assert.Equal("KaRiLaA", player.Name);
        Assert.Equal("b67e9dfc-d4d7-4f31-b30b-e1ab374cb0ee", player.Uuid);
        Assert.True(player.Online);
    }

    [Fact]
    public async Task Server_icon_upload_get_replace_and_remove_updates_revision()
    {
        var first = IconPng(1);
        using var uploaded = await UploadIconAsync(first);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var firstRevision = uploadedJson.RootElement.GetProperty("revision").GetString();
        Assert.Equal(firstRevision, (await ReadServerAsync()).IconRevision);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(_paths!.Instance(_serverId), "server-icon.png")));
        using (var library = await _client!.GetAsync("/api/v1/icons"))
        {
            Assert.Equal(HttpStatusCode.OK, library.StatusCode);
            using var libraryJson = JsonDocument.Parse(await library.Content.ReadAsStringAsync());
            Assert.Contains(libraryJson.RootElement.EnumerateArray(), icon => icon.GetProperty("revision").GetString() == firstRevision);
        }
        using (var libraryIcon = await _client!.GetAsync($"/api/v1/icons/{firstRevision}"))
        {
            Assert.Equal(HttpStatusCode.OK, libraryIcon.StatusCode);
            Assert.Equal(first, await libraryIcon.Content.ReadAsByteArrayAsync());
        }
        using var standaloneUpload = await UploadLibraryIconAsync(IconPng(3));
        Assert.Equal(HttpStatusCode.OK, standaloneUpload.StatusCode);
        using var standaloneJson = JsonDocument.Parse(await standaloneUpload.Content.ReadAsStringAsync());
        var standaloneRevision = standaloneJson.RootElement.GetProperty("revision").GetString();
        using (var downloaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/icon"))
        {
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal("image/png", downloaded.Content.Headers.ContentType?.MediaType);
        }

        using var replaced = await UploadIconAsync(IconPng(2));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        using var replacedJson = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
        Assert.NotEqual(firstRevision, replacedJson.RootElement.GetProperty("revision").GetString());

        using (var invalid = await UploadIconAsync(new byte[64]))
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using var removed = await SendJsonAsync(HttpMethod.Delete, $"/api/v1/servers/{_serverId}/icon", "{}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null((await ReadServerAsync()).IconRevision);
        Assert.False(File.Exists(Path.Combine(_paths.Instance(_serverId), "server-icon.png")));

        using (var selected = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/icon/library", JsonSerializer.Serialize(new { revision = firstRevision })))
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(firstRevision, (await ReadServerAsync()).IconRevision);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(_paths.Instance(_serverId), "server-icon.png")));

        using var deletedLibraryIcon = await SendJsonAsync(HttpMethod.Delete, $"/api/v1/icons/{standaloneRevision}", "{}");
        Assert.Equal(HttpStatusCode.NoContent, deletedLibraryIcon.StatusCode);
        using var missingLibraryIcon = await _client!.GetAsync($"/api/v1/icons/{standaloneRevision}");
        Assert.Equal(HttpStatusCode.NotFound, missingLibraryIcon.StatusCode);
    }

    [Fact]
    public async Task Runtime_sets_xms_and_xmx_to_selected_ram_and_rejects_invalid_steps()
    {
        var runtime = JsonSerializer.Serialize(new
        {
            initialMemoryMb = 512,
            maximumMemoryMb = 1024,
            totalMemoryMb = 2048,
            javaRuntimeId = JavaId,
            jvmArguments = "-Dcustom=true",
            useAikarFlags = true,
            startOnBoot = true,
            crashRecovery = false
        });
        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/runtime", runtime))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var server = await ReadServerAsync();
        Assert.Equal(1024, server.InitialMemoryMb);
        Assert.Equal(1024, server.MemoryMb);
        Assert.Equal(1536, server.MemoryLimitMb);
        Assert.True(server.UseAikarFlags);
        Assert.Equal("-Dcustom=true", server.JvmArguments);

        var invalid = JsonNode.Parse(runtime)!.AsObject();
        invalid["maximumMemoryMb"] = 1025;
        using var rejected = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/runtime", invalid.ToJsonString());
        await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Legacy_configuration_preserves_aikar_and_keeps_xms_and_xmx_equal()
    {
        var runtime = JsonSerializer.Serialize(new
        {
            initialMemoryMb = 1024,
            maximumMemoryMb = 2048,
            totalMemoryMb = 3072,
            javaRuntimeId = JavaId,
            jvmArguments = "",
            useAikarFlags = true,
            startOnBoot = false,
            crashRecovery = true
        });
        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/runtime", runtime))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var raisedMaximum = ValidConfiguration();
        raisedMaximum["memoryMb"] = 3072;
        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", raisedMaximum.ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var server = await ReadServerAsync();
        Assert.Equal(3072, server.InitialMemoryMb);
        Assert.Equal(3072, server.MemoryMb);
        Assert.Equal(4096, server.MemoryLimitMb);
        Assert.True(server.UseAikarFlags);

        var loweredMaximum = ValidConfiguration();
        loweredMaximum["memoryMb"] = 512;
        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", loweredMaximum.ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        server = await ReadServerAsync();
        Assert.Equal(512, server.InitialMemoryMb);
        Assert.Equal(512, server.MemoryMb);
        Assert.Equal(1024, server.MemoryLimitMb);
        Assert.True(server.UseAikarFlags);
    }

    [Fact]
    public async Task Properties_endpoint_restores_file_and_port_when_database_commit_fails()
    {
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        var priorBytes = await File.ReadAllBytesAsync(file);
        using var loaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/properties");
        using var loadedJson = JsonDocument.Parse(await loaded.Content.ReadAsStringAsync());
        var revision = loadedJson.RootElement.GetProperty("revision").GetString()!;
        await using (var triggerScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = triggerScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("""
CREATE TRIGGER fail_properties_server_update
BEFORE UPDATE ON Servers
BEGIN
    SELECT RAISE(ABORT, 'forced properties update failure');
END;
""");
        }
        var body = JsonSerializer.Serialize(new
        {
            revision,
            values = new Dictionary<string, string> { ["server-port"] = "32124" }
        });

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", body);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "OPERATION_FAILED");
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(32123, (await ReadServerAsync()).Port);
        AssertNoPropertyWorkFiles(Path.GetDirectoryName(file)!);
    }

    [Fact]
    public async Task Properties_endpoint_rejects_a_port_owned_by_another_server_without_changing_the_file()
    {
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        var priorBytes = await File.ReadAllBytesAsync(file);
        await using (var seedScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = Guid.NewGuid(), Name = "Port owner", Kind = ServerKind.Paper, Version = "1.21.8",
                State = ServerState.Stopped, Port = 32124, MemoryMb = 512, InitialMemoryMb = 512,
                JavaRuntimeId = JavaId, EulaAcceptedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var loaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/properties");
        using var loadedJson = JsonDocument.Parse(await loaded.Content.ReadAsStringAsync());
        var body = JsonSerializer.Serialize(new
        {
            revision = loadedJson.RootElement.GetProperty("revision").GetString(),
            values = new Dictionary<string, string> { ["server-port"] = "32124" }
        });

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", body);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "PORT_IN_USE");
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(32123, (await ReadServerAsync()).Port);
    }

    [Fact]
    public async Task Running_properties_and_runtime_only_mark_restart_for_material_changes()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        await File.WriteAllTextAsync(file, "motd=Before\nserver-port=32123\n");
        var started = await QueueActionAndWaitAsync("start");
        Assert.Equal("Completed", started.GetProperty("state").GetString());

        try
        {
            using var loaded = await _client!.GetAsync($"/api/v1/servers/{_serverId}/properties");
            using var loadedJson = JsonDocument.Parse(await loaded.Content.ReadAsStringAsync());
            var propertiesBody = JsonSerializer.Serialize(new
            {
                revision = loadedJson.RootElement.GetProperty("revision").GetString(),
                values = new Dictionary<string, string> { ["motd"] = "After" }
            });
            using (var changed = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/properties", propertiesBody))
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.True(await ReadRestartRequiredAsync());

            await using (var resetScope = _factory!.Services.CreateAsyncScope())
            {
                var stateFactory = resetScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
                await using var db = await stateFactory.CreateDbContextAsync();
                var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
                server.RestartRequired = false;
                await db.SaveChangesAsync();
            }

            var nonMaterialRuntime = JsonSerializer.Serialize(new
            {
                initialMemoryMb = 512,
                maximumMemoryMb = 512,
                totalMemoryMb = 1024,
                javaRuntimeId = JavaId,
                jvmArguments = "",
                useAikarFlags = false,
                startOnBoot = true,
                crashRecovery = true
            });
            using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/runtime", nonMaterialRuntime))
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.False(await ReadRestartRequiredAsync());

            var materialRuntime = JsonNode.Parse(nonMaterialRuntime)!.AsObject();
            materialRuntime["useAikarFlags"] = true;
            using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/runtime", materialRuntime.ToJsonString()))
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.True(await ReadRestartRequiredAsync());
        }
        finally
        {
            await StopManagedServerAsync();
        }
    }

    [Fact]
    public async Task Stopped_offline_player_action_writes_authoritative_whitelist_and_returns_player()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths!.Instance(_serverId), "server.properties"), "online-mode=false\n");

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/Notch/whitelist", "{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Notch", result.RootElement.GetProperty("name").GetString());
        Assert.True(result.RootElement.GetProperty("whitelisted").GetBoolean());
        using var whitelist = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_paths.Instance(_serverId), "whitelist.json")));
        var entry = Assert.Single(whitelist.RootElement.EnumerateArray());
        Assert.Equal("Notch", entry.GetProperty("name").GetString());
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", entry.GetProperty("uuid").GetString());

        using var removed = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/notch/unwhitelist", "{}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var removedResult = JsonDocument.Parse(await removed.Content.ReadAsStringAsync());
        Assert.False(removedResult.RootElement.GetProperty("whitelisted").GetBoolean());
        using var emptyWhitelist = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_paths.Instance(_serverId), "whitelist.json")));
        Assert.Empty(emptyWhitelist.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Player_list_merges_authoritative_membership_by_uuid_then_case_insensitive_name()
    {
        var instance = _paths!.Instance(_serverId);
        await File.WriteAllTextAsync(Path.Combine(instance, "whitelist.json"),
            """[{"uuid":"069a79f4-44e9-4726-a5be-fca90e38aaf5","name":"Notch"}]""");
        await File.WriteAllTextAsync(Path.Combine(instance, "ops.json"),
            """[{"uuid":"11111111-1111-4111-8111-111111111111","name":"notch","level":4,"bypassesPlayerLimit":false}]""");
        await File.WriteAllTextAsync(Path.Combine(instance, "banned-players.json"),
            """[{"uuid":"069a79f4-44e9-4726-a5be-fca90e38aaf5","name":"FormerName","created":"2026-01-01 00:00:00 +0000","source":"Server","expires":"forever","reason":"Banned by an operator."}]""");

        using var response = await _client!.GetAsync($"/api/v1/servers/{_serverId}/players");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var player = Assert.Single(result.RootElement.EnumerateArray());
        Assert.Equal("Notch", player.GetProperty("name").GetString());
        Assert.True(player.GetProperty("whitelisted").GetBoolean());
        Assert.True(player.GetProperty("operator").GetBoolean());
        Assert.True(player.GetProperty("banned").GetBoolean());
    }

    [Fact]
    public async Task Malformed_player_list_is_reported_and_never_replaced()
    {
        var path = Path.Combine(_paths!.Instance(_serverId), "ops.json");
        const string malformed = "{not-json";
        await File.WriteAllTextAsync(path, malformed);

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/Notch/op", "{}");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "PLAYER_LIST_INVALID");
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_paths.Instance(_serverId), "*.mcpanel-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Stopped_player_list_restores_file_state_when_database_commit_fails()
    {
        var instance = _paths!.Instance(_serverId);
        var path = Path.Combine(instance, "banned-players.json");
        await File.WriteAllTextAsync(Path.Combine(instance, "server.properties"), "online-mode=false\n");
        await using (var triggerScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = triggerScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("""
CREATE TRIGGER fail_player_insert
BEFORE INSERT ON Players
BEGIN
    SELECT RAISE(ABORT, 'forced player insert failure');
END;
""");
        }

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/Notch/ban", "{}");

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "OPERATION_FAILED");
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(instance, "*.mcpanel-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Running_player_action_uses_console_without_directly_writing_the_list_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var instance = _paths!.Instance(_serverId);
        await File.WriteAllTextAsync(Path.Combine(instance, "usercache.json"),
            """[{"uuid":"ec561538-f3fd-461d-aff5-086b22154bce","name":"Alex"}]""");
        var started = await QueueActionAndWaitAsync("start");
        Assert.Equal("Completed", started.GetProperty("state").GetString());

        try
        {
            using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/Alex/op", "{}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(result.RootElement.GetProperty("operator").GetBoolean());
            Assert.False(File.Exists(Path.Combine(instance, "ops.json")));
            await using var scope = _factory!.Services.CreateAsyncScope();
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            Assert.True((await db.Players.SingleAsync(x => x.ServerId == _serverId && x.Name == "Alex")).Operator);
        }
        finally
        {
            await StopManagedServerAsync();
        }
    }

    [Fact]
    public async Task Player_actions_reject_transitional_server_state_before_writing_files()
    {
        await SetServerStateAsync(ServerState.Starting);

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/players/Notch/whitelist", "{}");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "SERVER_BUSY");
        Assert.False(File.Exists(Path.Combine(_paths!.Instance(_serverId), "whitelist.json")));
    }

    [Theory]
    [InlineData("motd")]
    [InlineData("worldName")]
    public async Task Configuration_rejects_property_line_injection(string property)
    {
        var configuration = ValidConfiguration();
        configuration[property] = "safe\nserver-port=1234";

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.DoesNotContain("server-port=1234", await File.ReadAllTextAsync(Path.Combine(_paths!.Instance(_serverId), "server.properties")));
    }

    [Fact]
    public async Task Configuration_rejects_world_names_beyond_filesystem_utf8_component_limit()
    {
        var configuration = ValidConfiguration();
        configuration["worldName"] = string.Concat(Enumerable.Repeat("😀", 64));

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Configuration_rejects_transitional_error_and_inconsistent_running_states_without_creating_instance()
    {
        var rejectedStates = new[]
        {
            ServerState.Installing, ServerState.Starting, ServerState.Stopping,
            ServerState.BackingUp, ServerState.Updating, ServerState.Error, ServerState.Running
        };

        foreach (var state in rejectedStates)
        {
            await SetServerStateAsync(state);
            var instance = _paths!.Instance(_serverId);
            if (Directory.Exists(instance)) Directory.Delete(instance, true);

            using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", ValidConfiguration().ToJsonString());

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "SERVER_BUSY");
            Assert.False(Directory.Exists(instance));
            await using var scope = _factory!.Services.CreateAsyncScope();
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            var server = await db.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId);
            Assert.Equal(state, server.State);
            Assert.False(server.RestartRequired);
        }
    }

    [Fact]
    public async Task Configuration_allows_crashed_server_without_a_process()
    {
        await SetServerStateAsync(ServerState.Crashed);
        var instance = _paths!.Instance(_serverId);
        if (Directory.Exists(instance)) Directory.Delete(instance, true);

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", ValidConfiguration().ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var properties = await File.ReadAllTextAsync(Path.Combine(instance, "server.properties"));
        Assert.Contains("motd=Validation server", properties);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        var server = await db.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId);
        Assert.Equal(ServerState.Crashed, server.State);
        Assert.False(server.RestartRequired);
    }

    [Fact]
    public async Task Configuration_revalidates_state_after_waiting_for_server_lock()
    {
        var instance = _paths!.Instance(_serverId);
        if (Directory.Exists(instance)) Directory.Delete(instance, true);
        var keyedLock = _factory!.Services.GetRequiredService<AsyncKeyedLock>();
        var properties = _factory.Services.GetRequiredService<PropertiesService>();
        Task<ServerConfigurationDto> saveTask;

        using (await keyedLock.AcquireAsync(_serverId))
        {
            saveTask = properties.SaveAsync(_serverId, ValidConfigurationDto(), CancellationToken.None);
            await Task.Delay(50);
            Assert.False(saveTask.IsCompleted);
            await SetServerStateAsync(ServerState.Installing);
        }

        var exception = await Assert.ThrowsAsync<PanelException>(() => saveTask);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("SERVER_BUSY", exception.Code);
        Assert.False(Directory.Exists(instance));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configuration_restores_exact_prior_file_state_when_database_commit_fails(bool existingFile)
    {
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        if (!existingFile) File.Delete(file);
        var priorBytes = existingFile ? await File.ReadAllBytesAsync(file) : null;
        await using (var triggerScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = triggerScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("""
CREATE TRIGGER fail_server_update
BEFORE UPDATE ON Servers
BEGIN
    SELECT RAISE(ABORT, 'forced server update failure');
END;
""");
        }
        var configuration = ValidConfiguration();
        configuration["motd"] = "Must be rolled back";
        configuration["port"] = 32_124;

        using var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString());

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "OPERATION_FAILED");
        if (existingFile) Assert.Equal(priorBytes, await File.ReadAllBytesAsync(file));
        else Assert.False(File.Exists(file));
        AssertNoPropertyWorkFiles(Path.GetDirectoryName(file)!);
        var server = await ReadServerAsync();
        Assert.Equal(32_123, server.Port);
        Assert.False(server.RestartRequired);
    }

    [Fact]
    public async Task Configuration_atomic_success_and_noop_leave_no_work_files()
    {
        var configuration = ValidConfiguration();
        configuration["motd"] = "Atomically saved";
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");

        using (var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBytes = await File.ReadAllBytesAsync(file);
        Assert.Contains("motd=Atomically saved", Encoding.UTF8.GetString(savedBytes));
        AssertNoPropertyWorkFiles(Path.GetDirectoryName(file)!);

        using (var noop = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", configuration.ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(savedBytes, await File.ReadAllBytesAsync(file));
        AssertNoPropertyWorkFiles(Path.GetDirectoryName(file)!);
    }

    [Fact]
    public async Task Running_configuration_save_only_marks_restart_required_for_a_material_change()
    {
        if (OperatingSystem.IsWindows()) return;

        using (var initial = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", ValidConfiguration().ToJsonString()))
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var started = await QueueActionAndWaitAsync("start");
        Assert.Equal("Completed", started.GetProperty("state").GetString());

        try
        {
            using (var unchanged = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", ValidConfiguration().ToJsonString()))
                Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.False(await ReadRestartRequiredAsync());

            var changed = ValidConfiguration();
            changed["motd"] = "A materially changed MOTD";
            using (var updated = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration", changed.ToJsonString()))
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.True(await ReadRestartRequiredAsync());
        }
        finally
        {
            await StopManagedServerAsync();
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("disable-recovery")]
    public async Task Pending_crash_recovery_revalidates_cancellation_under_server_lock(string cancellation)
    {
        if (OperatingSystem.IsWindows()) return;

        var started = await QueueActionAndWaitAsync("start");
        Assert.Equal("Completed", started.GetProperty("state").GetString());
        var supervisor = _factory!.Services.GetRequiredService<ProcessSupervisor>();

        try
        {
            await supervisor.CommandAsync(_serverId, "crash", CancellationToken.None);
            await WaitForServerAsync(server => server.State == ServerState.Crashed && server.CrashAttempts == 1);
            var console = _factory.Services.GetRequiredService<ConsoleService>();
            Assert.True(await console.WaitForAsync(_serverId, 0,
                line => line.Text.Contains("Crash recovery attempt 1/3 in 5 seconds.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), CancellationToken.None));

            var keyedLock = _factory.Services.GetRequiredService<AsyncKeyedLock>();
            Task cancellationTask;
            using (await keyedLock.AcquireAsync(_serverId))
            {
                cancellationTask = cancellation == "stop"
                    ? supervisor.StopAsync(_serverId, CancellationToken.None)
                    : _factory.Services.GetRequiredService<PropertiesService>().SaveAsync(
                        _serverId, ValidConfigurationDto() with { CrashRecovery = false }, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(6));
                Assert.False(cancellationTask.IsCompleted);
            }

            await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);
            Assert.False(supervisor.IsRunning(_serverId));
            var server = await ReadServerAsync();
            if (cancellation == "stop")
            {
                Assert.Equal(ServerState.Stopped, server.State);
                Assert.Equal(0, server.CrashAttempts);
            }
            else
            {
                Assert.Equal(ServerState.Crashed, server.State);
                Assert.Equal(1, server.CrashAttempts);
                Assert.False(server.CrashRecovery);
            }
            Assert.Null(server.ProcessId);
        }
        finally
        {
            await StopManagedServerAsync();
        }
    }

    [Fact]
    public async Task Backup_creation_rejects_transitional_error_crashed_and_inconsistent_running_states_before_side_effects()
    {
        var rejectedStates = new[]
        {
            ServerState.Installing, ServerState.Starting, ServerState.Stopping, ServerState.BackingUp,
            ServerState.Updating, ServerState.Error, ServerState.Crashed, ServerState.Running
        };

        foreach (var state in rejectedStates)
        {
            await SetServerStateAsync(state);

            using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}");

            await AssertProblemAsync(response, HttpStatusCode.Conflict, "SERVER_BUSY");
            Assert.False(Directory.Exists(_paths!.ServerBackups(_serverId)));
            Assert.Empty(Directory.EnumerateDirectories(_paths.Staging, $"backup-{_serverId:N}-*"));
        }

        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Empty(await db.Backups.ToListAsync());
        var consoleFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConsoleDbContext>>();
        await using var consoleDb = await consoleFactory.CreateDbContextAsync();
        Assert.Empty(await consoleDb.Lines.ToListAsync());
    }

    [Fact]
    public async Task Backup_queue_revalidates_state_after_waiting_for_server_lock()
    {
        var keyedLock = _factory!.Services.GetRequiredService<AsyncKeyedLock>();
        var backups = _factory.Services.GetRequiredService<BackupService>();
        Task<JobDto> queueTask;

        using (await keyedLock.AcquireAsync(_serverId))
        {
            queueTask = backups.QueueCreateAsync(_serverId, "Manual", CancellationToken.None);
            await Task.Delay(50);
            Assert.False(queueTask.IsCompleted);
            await SetServerStateAsync(ServerState.Updating);
        }

        var exception = await Assert.ThrowsAsync<PanelException>(() => queueTask);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("SERVER_BUSY", exception.Code);
        Assert.False(Directory.Exists(_paths!.ServerBackups(_serverId)));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Staging, $"backup-{_serverId:N}-*"));
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task Backup_creation_succeeds_for_stopped_server_without_a_process()
    {
        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var job = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Completed", job.GetProperty("state").GetString());
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        var backup = Assert.Single(await db.Backups.AsNoTracking().ToListAsync());
        Assert.True(File.Exists(Path.Combine(_paths!.ServerBackups(_serverId), backup.FileName)));
        Assert.Equal(ServerState.Stopped, (await db.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId)).State);
    }

    [Fact]
    public async Task Backup_restore_uses_the_software_metadata_captured_before_a_core_change()
    {
        using (var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var creation = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("Completed", creation.GetProperty("state").GetString());
        }

        Guid backupId;
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            var backup = Assert.Single(await db.Backups.AsNoTracking().ToListAsync());
            Assert.False(string.IsNullOrWhiteSpace(backup.SoftwareMetadataJson));
            backupId = backup.Id;
            var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
            server.Kind = ServerKind.CustomJar;
            server.Version = "1.21.8";
            server.LaunchTarget = "custom-server.jar";
            server.RequiredJavaMajor = 21;
            await db.SaveChangesAsync();
        }
        await File.WriteAllBytesAsync(Path.Combine(_paths!.Instance(_serverId), "custom-server.jar"), []);

        using var restore = await SendJsonAsync(HttpMethod.Post,
            $"/api/v1/servers/{_serverId}/backups/{backupId}/restore", "{}");
        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        using var restoreDocument = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        var restoration = await WaitForJobAsync(restoreDocument.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Completed", restoration.GetProperty("state").GetString());
        var restored = await ReadServerAsync();
        Assert.Equal(ServerKind.Paper, restored.Kind);
        Assert.Equal("1.20.4", restored.Version);
        Assert.Equal("server.jar", restored.LaunchTarget);
        Assert.False(File.Exists(Path.Combine(_paths.Instance(_serverId), "custom-server.jar")));
        Assert.True(File.Exists(Path.Combine(_paths.Instance(_serverId), "server.jar")));
    }

    [Fact]
    public async Task Failed_custom_jar_change_restores_the_upload_for_retry()
    {
        string token;
        using (var upload = await UploadCustomJarAsync("retry.jar", CreateExecutableJar()))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            using var document = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            token = document.RootElement.GetProperty("token").GetString()!;
        }
        var conflict = Path.Combine(_paths!.Instance(_serverId), "custom-server.jar");
        Directory.CreateDirectory(conflict);
        var request = new
        {
            kind = "CustomJar",
            version = "1.21.8",
            javaRuntimeId = JavaId,
            includeExperimental = false,
            createBackup = false,
            customJarImportToken = token,
            clientRequestId = Guid.NewGuid()
        };
        using (var response = await SendJsonAsync(HttpMethod.Post,
                   $"/api/v1/servers/{_serverId}/software/change", JsonSerializer.Serialize(request)))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var failed = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("Failed", failed.GetProperty("state").GetString());
        }
        Directory.Delete(conflict);

        using var retry = await SendJsonAsync(HttpMethod.Post,
            $"/api/v1/servers/{_serverId}/software/change",
            JsonSerializer.Serialize(request with { clientRequestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var retryDocument = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        var completed = await WaitForJobAsync(retryDocument.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Completed", completed.GetProperty("state").GetString());
        Assert.Equal(ServerKind.CustomJar, (await ReadServerAsync()).Kind);
    }

    [Fact]
    public async Task Backup_restore_recovers_the_modpack_baseline_with_its_link_metadata()
    {
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
            server.ModpackName = "Review Pack";
            server.ModpackVersion = "1.0.0";
            server.ModrinthProjectId = "project";
            server.ModrinthVersionId = "version";
            server.ModpackSource = "Modrinth";
            await db.SaveChangesAsync();
        }
        var modpack = _paths!.ServerModpack(_serverId);
        Directory.CreateDirectory(modpack);
        await File.WriteAllTextAsync(Path.Combine(modpack, "baseline.json"), "baseline-state");
        await File.WriteAllTextAsync(Path.Combine(modpack, "source.mrpack"), "pack-archive");

        Guid backupId;
        using (var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var creation = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("Completed", creation.GetProperty("state").GetString());
        }
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            backupId = (await db.Backups.AsNoTracking().SingleAsync()).Id;
            var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
            server.ModpackName = null;
            server.ModpackVersion = null;
            server.ModrinthProjectId = null;
            server.ModrinthVersionId = null;
            server.ModpackSource = null;
            await db.SaveChangesAsync();
        }
        Directory.Delete(modpack, true);

        using var restore = await SendJsonAsync(HttpMethod.Post,
            $"/api/v1/servers/{_serverId}/backups/{backupId}/restore", "{}");
        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        using var restoreDocument = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        var restoration = await WaitForJobAsync(restoreDocument.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Completed", restoration.GetProperty("state").GetString());
        var restored = await ReadServerAsync();
        Assert.Equal("Review Pack", restored.ModpackName);
        Assert.Equal("1.0.0", restored.ModpackVersion);
        Assert.Equal("baseline-state", await File.ReadAllTextAsync(Path.Combine(modpack, "baseline.json")));
        Assert.Equal("pack-archive", await File.ReadAllTextAsync(Path.Combine(modpack, "source.mrpack")));
    }

    [Fact]
    public async Task Startup_recovery_rolls_back_a_restore_interrupted_before_its_commit_marker()
    {
        var backups = _factory!.Services.GetRequiredService<BackupService>();
        SoftwareActivationService.SoftwareMetadataSnapshot original;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            original = SoftwareActivationService.SoftwareMetadataSnapshot.Capture(
                await db.Servers.SingleAsync(x => x.Id == _serverId));
        }
        var target = original with
        {
            Kind = ServerKind.CustomJar,
            Version = "1.21.8",
            LaunchTarget = "custom-server.jar"
        };
        var transaction = new BackupService.RestoreTransaction(
            _paths!, Guid.NewGuid(), _serverId, original, target, false, false);
        Directory.CreateDirectory(transaction.Stage);
        await File.WriteAllTextAsync(Path.Combine(transaction.Stage, "custom-server.jar"), "custom");
        transaction.Activate();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            target.Restore(await db.Servers.SingleAsync(x => x.Id == _serverId));
            await db.SaveChangesAsync();
            await backups.RecoverInterruptedRestoresAsync(db, CancellationToken.None);
        }

        var restored = await ReadServerAsync();
        Assert.Equal(ServerKind.Paper, restored.Kind);
        Assert.Equal("server.jar", restored.LaunchTarget);
        Assert.True(File.Exists(Path.Combine(_paths!.Instance(_serverId), "server.jar")));
        Assert.False(File.Exists(Path.Combine(_paths.Instance(_serverId), "custom-server.jar")));
        Assert.Empty(Directory.EnumerateFiles(_paths.Staging, "backup-restore-*.json"));
    }

    [Fact]
    public async Task Startup_recovery_finishes_a_restore_with_a_committed_marker()
    {
        var backups = _factory!.Services.GetRequiredService<BackupService>();
        SoftwareActivationService.SoftwareMetadataSnapshot original;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            original = SoftwareActivationService.SoftwareMetadataSnapshot.Capture(
                await db.Servers.SingleAsync(x => x.Id == _serverId));
        }
        var target = original with
        {
            Kind = ServerKind.CustomJar,
            Version = "1.21.8",
            LaunchTarget = "custom-server.jar"
        };
        var transaction = new BackupService.RestoreTransaction(
            _paths!, Guid.NewGuid(), _serverId, original, target, false, false);
        Directory.CreateDirectory(transaction.Stage);
        await File.WriteAllTextAsync(Path.Combine(transaction.Stage, "custom-server.jar"), "custom");
        transaction.Activate();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            target.Restore(await db.Servers.SingleAsync(x => x.Id == _serverId));
            await db.SaveChangesAsync();
            transaction.MarkCommitted();
            await backups.RecoverInterruptedRestoresAsync(db, CancellationToken.None);
        }

        var restored = await ReadServerAsync();
        Assert.Equal(ServerKind.CustomJar, restored.Kind);
        Assert.Equal("custom-server.jar", restored.LaunchTarget);
        Assert.False(File.Exists(Path.Combine(_paths!.Instance(_serverId), "server.jar")));
        Assert.Equal("custom", await File.ReadAllTextAsync(Path.Combine(_paths.Instance(_serverId), "custom-server.jar")));
        Assert.Empty(Directory.EnumerateFiles(_paths.Staging, "backup-restore-*.json"));
    }

    [Fact]
    public async Task Backup_creation_removes_activated_archive_when_metadata_commit_fails()
    {
        await using (var triggerScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = triggerScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("""
CREATE TRIGGER fail_backup_insert
BEFORE INSERT ON Backups
BEGIN
    SELECT RAISE(ABORT, 'forced backup insert failure');
END;
""");
        }

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var job = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Failed", job.GetProperty("state").GetString());
        var backupDirectory = _paths!.ServerBackups(_serverId);
        Assert.True(Directory.Exists(backupDirectory));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Staging, $"backup-{_serverId:N}-*"));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var verifyDb = await verifyFactory.CreateDbContextAsync();
        Assert.Empty(await verifyDb.Backups.AsNoTracking().ToListAsync());
        Assert.Equal(ServerState.Stopped, (await verifyDb.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_creation_enforces_restore_archive_limits_before_commit(bool entryLimit)
    {
        var panelOptions = _factory!.Services.GetRequiredService<IOptions<PanelOptions>>().Value;
        if (entryLimit) panelOptions.MaxArchiveEntries = 1;
        else panelOptions.MaxExtractedBytes = 1;

        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var job = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());

        Assert.Equal("Failed", job.GetProperty("state").GetString());
        Assert.Contains(entryLimit ? "too many entries" : "expands beyond", job.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var backupDirectory = _paths!.ServerBackups(_serverId);
        Assert.True(Directory.Exists(backupDirectory));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Staging, $"backup-{_serverId:N}-*"));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var verifyDb = await verifyFactory.CreateDbContextAsync();
        Assert.Empty(await verifyDb.Backups.AsNoTracking().ToListAsync());
        Assert.Equal(ServerState.Stopped, (await verifyDb.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId)).State);
    }

    [Fact]
    public async Task Backup_creation_rejects_stopped_with_live_process_then_succeeds_when_state_is_running()
    {
        if (OperatingSystem.IsWindows()) return;

        var started = await QueueActionAndWaitAsync("start");
        Assert.Equal("Completed", started.GetProperty("state").GetString());

        try
        {
            var jobsBefore = await CountJobsAsync();
            await SetServerStateAsync(ServerState.Stopped);
            using (var rejected = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}"))
                await AssertProblemAsync(rejected, HttpStatusCode.Conflict, "SERVER_BUSY");
            Assert.Equal(jobsBefore, await CountJobsAsync());
            Assert.False(Directory.Exists(_paths!.ServerBackups(_serverId)));

            await SetServerStateAsync(ServerState.Running);
            using var accepted = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/backups", "{}");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            var job = await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("Completed", job.GetProperty("state").GetString());

            await using var scope = _factory!.Services.CreateAsyncScope();
            var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            var backup = Assert.Single(await db.Backups.AsNoTracking().ToListAsync());
            Assert.True(File.Exists(Path.Combine(_paths.ServerBackups(_serverId), backup.FileName)));
            Assert.Equal(ServerState.Running, (await db.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId)).State);
        }
        finally
        {
            await StopManagedServerAsync();
        }
    }

    [Fact]
    public async Task Backup_delete_waits_for_server_lock_and_exposes_no_partial_state()
    {
        var backupId = Guid.NewGuid();
        var fileName = $"{backupId:N}.zip";
        var backupDirectory = _paths!.ServerBackups(_serverId);
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, fileName);
        await File.WriteAllBytesAsync(backupPath, [1, 2, 3]);
        await using (var seedScope = _factory!.Services.CreateAsyncScope())
        {
            var stateFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            db.Backups.Add(new BackupEntity { Id = backupId, ServerId = _serverId, FileName = fileName, Size = 3 });
            await db.SaveChangesAsync();
        }

        var keyedLock = _factory.Services.GetRequiredService<AsyncKeyedLock>();
        var backups = _factory.Services.GetRequiredService<BackupService>();
        Task deleteTask;
        using (await keyedLock.AcquireAsync(_serverId))
        {
            deleteTask = backups.DeleteAsync(_serverId, backupId, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(deleteTask.IsCompleted);
            Assert.True(File.Exists(backupPath));
            await using var blockedScope = _factory.Services.CreateAsyncScope();
            var stateFactory = blockedScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
            await using var db = await stateFactory.CreateDbContextAsync();
            Assert.True(await db.Backups.AnyAsync(x => x.Id == backupId));
        }

        await deleteTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(backupPath));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyFactory = verifyScope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var verifyDb = await verifyFactory.CreateDbContextAsync();
        Assert.False(await verifyDb.Backups.AnyAsync(x => x.Id == backupId));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<HttpResponseMessage> UploadAsync(string fileName, byte[] content)
    {
        var csrf = await GetAntiforgeryAsync();
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(content), "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/servers/{_serverId}/files/upload") { Content = multipart };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadIconAsync(byte[] content)
    {
        var csrf = await GetAntiforgeryAsync();
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("image/png");
        multipart.Add(file, "file", "server-icon.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/servers/{_serverId}/icon") { Content = multipart };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadCustomJarAsync(string fileName, byte[] content)
    {
        var csrf = await GetAntiforgeryAsync();
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("application/java-archive");
        multipart.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/server-jars/imports") { Content = multipart };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return await _client!.SendAsync(request);
    }

    private static byte[] CreateExecutableJar()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write("Manifest-Version: 1.0\r\nMain-Class: example.Main\r\n");
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> UploadLibraryIconAsync(byte[] content)
    {
        var csrf = await GetAntiforgeryAsync();
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("image/png");
        multipart.Add(file, "file", "panel-icon.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/icons") { Content = multipart };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string body)
    {
        var csrf = await GetAntiforgeryAsync();
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return await _client!.SendAsync(request);
    }

    private async Task<string> GetAntiforgeryAsync()
    {
        using var response = await _client!.GetAsync("/api/v1/auth/antiforgery");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private async Task SetServerStateAsync(ServerState state)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
        server.State = state;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<bool> ReadRestartRequiredAsync()
    {
        var server = await ReadServerAsync();
        Assert.Equal(ServerState.Running, server.State);
        return server.RestartRequired;
    }

    private async Task<ServerEntity> ReadServerAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        return await db.Servers.AsNoTracking().SingleAsync(x => x.Id == _serverId);
    }

    private async Task WaitForServerAsync(Func<ServerEntity, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (predicate(await ReadServerAsync())) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The server did not reach the expected state.");
    }

    private async Task<int> CountJobsAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using var db = await stateFactory.CreateDbContextAsync();
        return await db.Jobs.CountAsync();
    }

    private async Task<JsonElement> QueueActionAndWaitAsync(string action)
    {
        using var response = await SendJsonAsync(HttpMethod.Post, $"/api/v1/servers/{_serverId}/{action}", "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return await WaitForJobAsync(document.RootElement.GetProperty("id").GetGuid());
    }

    private async Task StopManagedServerAsync()
    {
        var supervisor = _factory!.Services.GetRequiredService<ProcessSupervisor>();
        if (!supervisor.IsRunning(_serverId)) return;
        var stopped = await QueueActionAndWaitAsync("stop");
        Assert.Equal("Completed", stopped.GetProperty("state").GetString());
    }

    private async Task<JsonElement> WaitForJobAsync(Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client!.GetAsync($"/api/v1/jobs/{id}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = document.RootElement.GetProperty("state").GetString();
            if (state is "Completed" or "Failed") return document.RootElement.Clone();
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {id} did not finish.");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }

    private static void AssertNoPropertyWorkFiles(string directory) =>
        Assert.Empty(Directory.EnumerateFiles(directory, ".server.properties.*", SearchOption.TopDirectoryOnly));

    private static byte[] IconPng(byte marker)
    {
        byte[] bytes =
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 0, 64, 0, 0, 0, 64,
            8, 6, 0, 0, 0, 0, 0, 0, 0
        ];
        bytes[^1] = marker;
        return bytes;
    }

    private static string Schedule(
        string name,
        string frequency = "Interval",
        string timeZone = "UTC",
        string? cron = null,
        string? timeOfDay = null,
        IReadOnlyList<int>? daysOfWeek = null) => JsonSerializer.Serialize(new
    {
        name,
        frequency,
        timeZone,
        enabled = true,
        intervalMinutes = 5,
        cron,
        timeOfDay,
        daysOfWeek,
        actions = new[] { new { action = "start" } }
    });

    private static JsonObject ValidConfiguration() => new()
    {
        ["motd"] = "Validation server",
        ["maxPlayers"] = 20,
        ["gameMode"] = "survival",
        ["difficulty"] = "normal",
        ["whitelist"] = false,
        ["onlineMode"] = true,
        ["pvp"] = true,
        ["commandBlocks"] = false,
        ["allowFlight"] = false,
        ["spawnProtection"] = 16,
        ["viewDistance"] = 10,
        ["simulationDistance"] = 10,
        ["worldName"] = "world",
        ["port"] = 32_123,
        ["memoryMb"] = PanelOptions.MinimumServerMemoryMb,
        ["javaRuntimeId"] = JavaId,
        ["jvmArguments"] = "",
        ["startOnBoot"] = false,
        ["crashRecovery"] = true
    };

    private static ServerConfigurationDto ValidConfigurationDto() => new(
        "Validation server", 20, "survival", "normal", false, true, true, false, false,
        16, 10, 10, "world", 32_123, PanelOptions.MinimumServerMemoryMb, JavaId, "", false, true);
}

public sealed class PanelProblemTests
{
    [Fact]
    public void Kestrel_payload_limit_maps_to_file_too_large()
    {
        var exception = PanelProblems.BadRequest(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exception.StatusCode);
        Assert.Equal("FILE_TOO_LARGE", exception.Code);
    }

    [Fact]
    public void Create_server_contract_advertises_heap_memory_minimum()
    {
        var attribute = Assert.Single(typeof(CreateServerRequest).GetProperty(nameof(CreateServerRequest.MemoryMb))!
            .GetCustomAttributes(typeof(RangeAttribute), false).Cast<RangeAttribute>());

        Assert.Equal(PanelOptions.MinimumServerMemoryMb, Convert.ToInt32(attribute.Minimum));
    }
}
