using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McPanel.Api;

internal static class ServerImportCommand
{
    private const string StageSwitch = "--mcpanel-import-stage";
    private const string ImportSwitch = "--mcpanel-import-server";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsStageInvocation(string[] args) => args.Length > 0 && args[0] == StageSwitch;
    public static bool IsImportInvocation(string[] args) => args.Length > 0 && args[0] == ImportSwitch;

    public static async Task<int> RunStageAsync(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            if (args.Length is < 3 or > 4 || args[0] != StageSwitch || args.Length == 4 && args[3] != "--json")
                throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_USAGE", "The internal staging invocation is invalid.");
            await ServerImportSource.StageAsync(args[1], args[2], CancellationToken.None);
            return 0;
        }
        catch (ServerImportException exception)
        {
            WriteError(exception, json);
            return (int)exception.Kind;
        }
        catch (Exception exception)
        {
            var wrapped = new ServerImportException(ServerImportFailureKind.Operation, "IMPORT_STAGE_FAILED", "The import source could not be staged.", innerException: exception);
            WriteError(wrapped, json);
            return (int)wrapped.Kind;
        }
    }

    public static async Task<int> RunImportAsync(string[] args)
    {
        ImportCliOptions? parsed = null;
        try
        {
            parsed = ImportCliOptions.Parse(args);
            var panelOptions = new PanelOptions();
            var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables();
            configuration.GetSection("Panel").Bind(panelOptions);
            var paths = new PanelPaths(panelOptions);
            paths.EnsureCreated();
            var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
                .UseSqlite($"Data Source={paths.StateDatabase};Cache=Shared")
                .Options;
            var factory = new ImportDbContextFactory(dbOptions);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.EnsureCompatibleSchemaAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }
            var java = new JavaDiscoveryService(factory, NullLogger<JavaDiscoveryService>.Instance);
            var service = new ServerImportService(paths, factory, java, Options.Create(panelOptions));
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                return await RunAsync(parsed, service, cancellation.Token);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException)
        {
            var exception = new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_CANCELLED", "The import was cancelled.");
            WriteError(exception, parsed?.Json ?? args.Contains("--json", StringComparer.Ordinal));
            return (int)exception.Kind;
        }
        catch (ServerImportException exception)
        {
            WriteError(exception, parsed?.Json ?? args.Contains("--json", StringComparer.Ordinal));
            return (int)exception.Kind;
        }
        catch (Exception exception)
        {
            var wrapped = new ServerImportException(ServerImportFailureKind.Operation, "IMPORT_FAILED", "The import failed unexpectedly.", innerException: exception);
            WriteError(wrapped, parsed?.Json ?? args.Contains("--json", StringComparer.Ordinal));
            return (int)wrapped.Kind;
        }
    }

    private static async Task<int> RunAsync(
        ImportCliOptions options,
        ServerImportService service,
        CancellationToken cancellationToken)
    {
        var inspection = await service.InspectAsync(options.Root, cancellationToken);
        var interactive = !options.NonInteractive && !Console.IsInputRedirected;
        if (!interactive) options.NonInteractive = true;

        if (interactive)
            Console.Error.WriteLine($"Found {inspection.Launchers.Count} supported launch target(s) in the server root.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteOptions(options, inspection, await service.JavaRuntimesAsync(cancellationToken), interactive);
            EnsureComplete(options, inspection);
            var request = options.ToRequest(inspection);
            try
            {
                var validated = await service.ValidateAsync(options.Root, request, cancellationToken);
                if (interactive)
                {
                    PrintSummary(validated, options.DryRun);
                    if (!PromptYesNo(options.DryRun ? "Run this validation?" : "Import this server?", false))
                        throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_CANCELLED", "The import was cancelled.");
                }

                if (options.DryRun)
                {
                    WriteSuccess(options, new
                    {
                        ok = true,
                        dryRun = true,
                        resolved = ResultShape(validated),
                        warnings = Array.Empty<string>()
                    }, $"Import validation passed for {validated.Name}. No panel state was changed.");
                    return 0;
                }

                var result = await service.ImportAsync(options.Root, request, cancellationToken);
                WriteSuccess(options, new
                {
                    ok = true,
                    dryRun = false,
                    serverId = result.ServerId,
                    result.Name,
                    kind = result.Kind.ToString(),
                    result.Version,
                    state = result.State.ToString(),
                    result.InstanceDirectory,
                    warnings = Array.Empty<string>()
                }, $"Imported {result.Name} as server {result.ServerId}. It is stopped with start-on-boot disabled.");
                return 0;
            }
            catch (ServerImportException exception) when (interactive && exception.Field is not null && exception.Kind is ServerImportFailureKind.Usage or ServerImportFailureKind.Conflict)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                ClearOption(options, exception.Field);
            }
        }
    }

    private static void CompleteOptions(
        ImportCliOptions options,
        ServerImportInspection inspection,
        IReadOnlyList<JavaRuntimeEntity> runtimes,
        bool interactive)
    {
        if (!interactive) return;
        if (string.IsNullOrWhiteSpace(options.Name))
            options.Name = PromptText("Server name", SuggestedName(options.SourceLabel), required: true);
        if (options.Kind is null)
            options.Kind = PromptKind(inspection.SuggestedKind);
        if (string.IsNullOrWhiteSpace(options.Version))
            options.Version = PromptText("Minecraft version", inspection.SuggestedVersion, required: true);
        if (options.Kind is ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge && string.IsNullOrWhiteSpace(options.LoaderVersion))
            options.LoaderVersion = PromptText($"{options.Kind} loader version", inspection.SuggestedLoaderVersion, required: true);
        if (string.IsNullOrWhiteSpace(options.LaunchTarget))
            options.LaunchTarget = PromptLauncher(inspection.Launchers, options.Kind);
        if (string.IsNullOrWhiteSpace(options.JavaRuntime))
            options.JavaRuntime = PromptJava(runtimes);
        if (options.MemoryMb is null)
            options.MemoryMb = PromptInteger("Java heap in MiB", 4096);
        if (options.Port is null)
            options.Port = PromptInteger("Server port", inspection.PropertiesPort ?? 25565);
        if (options.JvmArguments is null)
            options.JvmArguments = PromptText("Extra JVM arguments", "", required: false);
        if (!options.EulaAccepted)
        {
            Console.Error.WriteLine("Minecraft EULA: https://aka.ms/MinecraftEULA");
            options.EulaAccepted = PromptYesNo("I accept the Minecraft EULA", false);
        }
    }

    private static void EnsureComplete(ImportCliOptions options, ServerImportInspection inspection)
    {
        if (string.IsNullOrWhiteSpace(options.Name)) Missing("--name");
        if (options.Kind is null) Missing("--kind");
        if (string.IsNullOrWhiteSpace(options.Version)) Missing("--version");
        if (options.Kind is ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge && string.IsNullOrWhiteSpace(options.LoaderVersion)) Missing("--loader-version");
        if (string.IsNullOrWhiteSpace(options.LaunchTarget)) Missing("--launch-target");
        if (string.IsNullOrWhiteSpace(options.JavaRuntime)) Missing("--java-runtime");
        if (options.MemoryMb is null) Missing("--memory-mb");
        options.Port ??= inspection.PropertiesPort;
        if (options.Port is null) Missing("--port because server.properties does not contain a valid server-port");
        options.JvmArguments ??= "";
        if (!options.EulaAccepted) Missing("--accept-eula");
    }

    private static void Missing(string option) => throw new ServerImportException(
        ServerImportFailureKind.Usage,
        "IMPORT_OPTION_REQUIRED",
        $"Non-interactive import requires {option}.");

    private static object ResultShape(ServerImportValidation value) => new
    {
        value.Name,
        kind = value.Kind.ToString(),
        value.Version,
        value.LoaderVersion,
        value.LaunchTarget,
        launchMode = value.LaunchMode.ToString(),
        javaRuntimeId = value.Runtime.Id,
        javaPath = value.Runtime.Path,
        javaMajor = value.Runtime.Major,
        value.RequiredJavaMajor,
        value.MemoryMb,
        value.MemoryLimitMb,
        value.Port,
        value.JvmArguments,
        state = ServerState.Stopped.ToString(),
        startOnBoot = false
    };

    private static void PrintSummary(ServerImportValidation value, bool dryRun)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(dryRun ? "Validation summary" : "Import summary");
        Console.Error.WriteLine($"  Name: {value.Name}");
        Console.Error.WriteLine($"  Server: {value.Kind} {value.Version}");
        if (value.LoaderVersion is not null) Console.Error.WriteLine($"  Loader: {value.LoaderVersion}");
        Console.Error.WriteLine($"  Launcher: {value.LaunchTarget} ({value.LaunchMode})");
        Console.Error.WriteLine($"  Java: {value.Runtime.Path} (Java {value.Runtime.Major})");
        Console.Error.WriteLine($"  Heap: {value.MemoryMb} MiB");
        Console.Error.WriteLine($"  Port: {value.Port}");
        Console.Error.WriteLine("  Final state: Stopped, start-on-boot disabled");
        Console.Error.WriteLine();
    }

    private static string PromptText(string label, string? suggested, bool required)
    {
        while (true)
        {
            Console.Error.Write(suggested is null ? $"{label}: " : $"{label} [{suggested}]: ");
            var value = Console.ReadLine();
            if (value is null) throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_CANCELLED", "Input ended before the wizard completed.");
            value = value.Trim();
            if (value.Length > 0) return value;
            if (suggested is not null) return suggested;
            if (!required) return "";
        }
    }

    private static int PromptInteger(string label, int suggested)
    {
        while (true)
        {
            var value = PromptText(label, suggested.ToString(), true);
            if (int.TryParse(value, out var parsed)) return parsed;
            Console.Error.WriteLine("Enter a whole number.");
        }
    }

    private static bool PromptYesNo(string label, bool suggested)
    {
        while (true)
        {
            Console.Error.Write($"{label} [{(suggested ? "Y/n" : "y/N")}]: ");
            var value = Console.ReadLine();
            if (value is null) throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_CANCELLED", "Input ended before the wizard completed.");
            value = value.Trim();
            if (value.Length == 0) return suggested;
            if (value.Equals("y", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("n", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        }
    }

    private static ServerKind PromptKind(ServerKind? suggested)
    {
        var supported = new[] { ServerKind.Vanilla, ServerKind.Paper, ServerKind.Fabric, ServerKind.Forge, ServerKind.NeoForge };
        Console.Error.WriteLine("Server kind:");
        for (var index = 0; index < supported.Length; index++)
            Console.Error.WriteLine($"  {index + 1}. {supported[index]}{(supported[index] == suggested ? " (suggested)" : "")}");
        while (true)
        {
            int? defaultIndex = suggested is null ? null : Array.IndexOf(supported, suggested.Value) + 1;
            var value = PromptText("Choose kind", defaultIndex?.ToString(), true);
            if (int.TryParse(value, out var parsed) && parsed >= 1 && parsed <= supported.Length) return supported[parsed - 1];
            if (Enum.TryParse<ServerKind>(value, true, out var kind) && supported.Contains(kind)) return kind;
            Console.Error.WriteLine("Choose Vanilla, Paper, Fabric, Forge, or NeoForge.");
        }
    }

    private static string PromptLauncher(IReadOnlyList<ServerImportLauncher> launchers, ServerKind? kind)
    {
        var suggested = launchers.FirstOrDefault(x => x.SuggestedKind == kind) ?? launchers.First();
        Console.Error.WriteLine("Launch targets:");
        for (var index = 0; index < launchers.Count; index++)
            Console.Error.WriteLine($"  {index + 1}. {launchers[index].Path} ({launchers[index].Mode}){(launchers[index] == suggested ? " (suggested)" : "")}");
        while (true)
        {
            var defaultIndex = Enumerable.Range(0, launchers.Count).First(index => launchers[index] == suggested) + 1;
            var value = PromptText("Choose target number or enter a relative path", defaultIndex.ToString(), true);
            if (int.TryParse(value, out var parsed) && parsed >= 1 && parsed <= launchers.Count) return launchers[parsed - 1].Path;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
    }

    private static string PromptJava(IReadOnlyList<JavaRuntimeEntity> runtimes)
    {
        if (runtimes.Count == 0) return PromptText("Absolute Java executable path", "/usr/bin/java", true);
        Console.Error.WriteLine("Java runtimes:");
        for (var index = 0; index < runtimes.Count; index++)
            Console.Error.WriteLine($"  {index + 1}. Java {runtimes[index].Major} at {runtimes[index].Path}");
        while (true)
        {
            var value = PromptText("Choose runtime number, ID, or absolute path", "1", true);
            if (int.TryParse(value, out var parsed) && parsed >= 1 && parsed <= runtimes.Count) return runtimes[parsed - 1].Id;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
    }

    private static string SuggestedName(string? sourceLabel)
    {
        var value = string.IsNullOrWhiteSpace(sourceLabel) ? "Imported Server" : sourceLabel;
        value = Path.GetFileNameWithoutExtension(value);
        if (value.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) value = Path.GetFileNameWithoutExtension(value);
        value = new string(value.Select(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' ? character : ' ').ToArray()).Trim();
        if (value.Length > 48) value = value[..48].Trim();
        return value.Length >= 2 ? value : "Imported Server";
    }

    private static void ClearOption(ImportCliOptions options, string field)
    {
        switch (field)
        {
            case "name": options.Name = null; break;
            case "kind": options.Kind = null; break;
            case "version": options.Version = null; break;
            case "loader-version": options.LoaderVersion = null; break;
            case "launch-target": options.LaunchTarget = null; break;
            case "java-runtime": options.JavaRuntime = null; break;
            case "memory-mb": options.MemoryMb = null; break;
            case "port": options.Port = null; break;
            case "jvm-args": options.JvmArguments = null; break;
            case "accept-eula": options.EulaAccepted = false; break;
            default: throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_INVALID", "The import settings are invalid.");
        }
    }

    private static void WriteSuccess(ImportCliOptions options, object jsonValue, string humanValue)
    {
        if (options.Json) Console.Out.WriteLine(JsonSerializer.Serialize(jsonValue, JsonOptions));
        else Console.Out.WriteLine(humanValue);
    }

    private static void WriteError(ServerImportException exception, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                code = exception.Code,
                message = exception.Message
            }, JsonOptions));
        }
        else Console.Error.WriteLine($"error: {exception.Message}");
    }

    private sealed class ImportDbContextFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }

    private sealed class ImportCliOptions
    {
        public required string Root { get; init; }
        public string? SourceLabel { get; set; }
        public string? Name { get; set; }
        public ServerKind? Kind { get; set; }
        public string? Version { get; set; }
        public string? LoaderVersion { get; set; }
        public string? LaunchTarget { get; set; }
        public string? JavaRuntime { get; set; }
        public int? MemoryMb { get; set; }
        public int? Port { get; set; }
        public string? JvmArguments { get; set; }
        public bool EulaAccepted { get; set; }
        public bool NonInteractive { get; set; }
        public bool DryRun { get; set; }
        public bool Json { get; set; }

        public ServerImportRequest ToRequest(ServerImportInspection inspection) => new(
            Name!, Kind!.Value, Version!, LoaderVersion, LaunchTarget!, JavaRuntime!, MemoryMb!.Value,
            Port ?? inspection.PropertiesPort!.Value, JvmArguments ?? "", EulaAccepted);

        public static ImportCliOptions Parse(string[] args)
        {
            if (args.Length < 2 || args[0] != ImportSwitch)
                throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_USAGE", "The internal import invocation is invalid.");
            var root = args[1];
            var options = new ImportCliOptions { Root = root };
            for (var index = 2; index < args.Length; index++)
            {
                var option = args[index];
                string Value()
                {
                    if (++index >= args.Length)
                        throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_USAGE", $"{option} requires a value.");
                    return args[index];
                }
                switch (option)
                {
                    case "--source-label": options.SourceLabel = Value(); break;
                    case "--name": options.Name = Value(); break;
                    case "--kind":
                        var kindValue = Value();
                        if (!Enum.TryParse<ServerKind>(kindValue, true, out var kind) ||
                            !Enum.IsDefined(kind) ||
                            kind is not (ServerKind.Vanilla or ServerKind.Paper or ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge))
                            throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_KIND_INVALID", "--kind must be vanilla, paper, fabric, forge, or neoforge.");
                        options.Kind = kind;
                        break;
                    case "--version": options.Version = Value(); break;
                    case "--loader-version": options.LoaderVersion = Value(); break;
                    case "--launch-target": options.LaunchTarget = Value(); break;
                    case "--java-runtime": options.JavaRuntime = Value(); break;
                    case "--memory-mb": options.MemoryMb = Integer(option, Value()); break;
                    case "--port": options.Port = Integer(option, Value()); break;
                    case "--jvm-args": options.JvmArguments = Value(); break;
                    case "--accept-eula": options.EulaAccepted = true; break;
                    case "--non-interactive": options.NonInteractive = true; break;
                    case "--dry-run": options.DryRun = true; break;
                    case "--json": options.Json = true; options.NonInteractive = true; break;
                    default: throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_USAGE", $"Unknown import option: {option}");
                }
            }
            return options;
        }

        private static int Integer(string option, string value)
        {
            if (!int.TryParse(value, out var parsed))
                throw new ServerImportException(ServerImportFailureKind.Usage, "IMPORT_USAGE", $"{option} requires a whole number.");
            return parsed;
        }
    }
}
