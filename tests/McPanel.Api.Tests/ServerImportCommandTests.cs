using System.Text.Json;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

[Collection(ApiIntegrationCollection.Name)]
public sealed class ServerImportCommandTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-import-command-" + Guid.NewGuid().ToString("N"));
    private TextWriter _originalOut = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", Path.Combine(_root, "config"));
        _originalOut = Console.Out;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Console.SetOut(_originalOut);
        Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", null);
        Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", null);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Json_dry_run_returns_resolved_settings_without_registering_a_server()
    {
        var source = CreateSource();
        var output = new StringWriter();
        Console.SetOut(output);

        var exitCode = await ServerImportCommand.RunImportAsync([
            "--mcpanel-import-server", source,
            "--name", "Dry run world",
            "--kind", "vanilla",
            "--version", "1.20.4",
            "--launch-target", "server.jar",
            "--java-runtime", "/usr/bin/java",
            "--memory-mb", "2048",
            "--accept-eula",
            "--dry-run",
            "--json"
        ]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(document.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(25578, document.RootElement.GetProperty("resolved").GetProperty("port").GetInt32());
        Assert.True(Directory.Exists(source));

        var database = Path.Combine(_root, "data", "state.db");
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={database}").Options;
        await using var db = new StateDbContext(options);
        Assert.Empty(await db.Servers.AsNoTracking().ToListAsync());
        Assert.Empty(await db.JavaRuntimes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Json_non_interactive_failure_uses_the_usage_exit_code()
    {
        var source = CreateSource();
        var output = new StringWriter();
        Console.SetOut(output);

        var exitCode = await ServerImportCommand.RunImportAsync([
            "--mcpanel-import-server", source,
            "--name", "Missing settings",
            "--json"
        ]);

        Assert.Equal((int)ServerImportFailureKind.Usage, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("IMPORT_OPTION_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Numeric_undefined_server_kind_is_rejected()
    {
        var source = CreateSource();
        var output = new StringWriter();
        Console.SetOut(output);

        var exitCode = await ServerImportCommand.RunImportAsync([
            "--mcpanel-import-server", source,
            "--kind", "99",
            "--json"
        ]);

        Assert.Equal((int)ServerImportFailureKind.Usage, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("IMPORT_KIND_INVALID", document.RootElement.GetProperty("code").GetString());
    }

    private string CreateSource()
    {
        var source = Path.Combine(_root, "staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "server.properties"), "server-port=25578\n");
        File.WriteAllBytes(Path.Combine(source, "server.jar"), [0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        return source;
    }
}
