using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class PropertiesDocument
{
    private readonly List<string> _lines;

    private PropertiesDocument(List<string> lines) => _lines = lines;
    public static PropertiesDocument Parse(string text) => new(text.Replace("\r\n", "\n").Split('\n').ToList());
    public static PropertiesDocument Empty() => new([]);

    public string? Get(string key)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i].TrimStart();
            if (line.StartsWith('#') || line.StartsWith('!')) continue;
            var index = line.IndexOf('=');
            if (index >= 0 && line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(index + 1)..];
        }
        return null;
    }

    public void Set(string key, string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0')) throw PanelProblems.Validation($"Property '{key}' contains invalid characters.");
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i].TrimStart();
            if (line.StartsWith('#') || line.StartsWith('!')) continue;
            var index = line.IndexOf('=');
            if (index >= 0 && line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{key}={value}";
                return;
            }
        }
        _lines.Add($"{key}={value}");
    }

    public override string ToString() => string.Join(Environment.NewLine, _lines).TrimEnd() + Environment.NewLine;
}

public sealed class PropertiesService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    IOptions<PanelOptions> options,
    AsyncKeyedLock keyedLock,
    IServerProcessStatus processStatus)
{
    private const int MaxMotdLength = 512;
    private const int MaxWorldNameLength = 128;
    private const int MaxWorldNameUtf8Bytes = 255;
    private const int MaxJvmArgumentsLength = 2_048;

    public async Task<ServerConfigurationDto> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var file = Path.Combine(paths.Instance(id), "server.properties");
        var document = File.Exists(file) ? PropertiesDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken)) : PropertiesDocument.Empty();
        return new(
            document.Get("motd") ?? "A Minecraft Server",
            Integer(document, "max-players", 20), document.Get("gamemode") ?? "survival", document.Get("difficulty") ?? "easy",
            Boolean(document, "white-list", false), Boolean(document, "online-mode", true), Boolean(document, "pvp", true),
            Boolean(document, "enable-command-block", false), Boolean(document, "allow-flight", false),
            Integer(document, "spawn-protection", 16), Integer(document, "view-distance", 10), Integer(document, "simulation-distance", 10),
            document.Get("level-name") ?? "world", Integer(document, "server-port", server.Port), server.MemoryMb,
            server.JavaRuntimeId, server.JvmArguments, server.StartOnBoot, server.CrashRecovery);
    }

    public async Task<ServerConfigurationDto> SaveAsync(Guid id, ServerConfigurationDto dto, CancellationToken cancellationToken)
    {
        Validate(dto);
        _ = JvmArgumentParser.Parse(dto.JvmArguments);
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var processRunning = processStatus.IsRunning(id);
        var stateIsConsistent = server.State switch
        {
            ServerState.Running => processRunning,
            ServerState.Stopped or ServerState.Crashed => !processRunning,
            _ => false
        };
        if (!stateIsConsistent)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server configuration cannot be changed in its current state.");
        if (await db.Servers.AnyAsync(x => x.Id != id && x.Port == dto.Port, cancellationToken))
            throw PanelProblems.Conflict("PORT_IN_USE", "The selected port is already assigned.");
        if (!await db.JavaRuntimes.AnyAsync(x => x.Id == dto.JavaRuntimeId, cancellationToken))
            throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        var totalMemory = HostMetricsService.ReadMemory().Total;
        if ((long)dto.MemoryMb * 1024 * 1024 > totalMemory * options.Value.MemoryAllocationFraction)
            throw new PanelException(400, "MEMORY_LIMIT_EXCEEDED", "The selected memory exceeds the host allocation limit.");

        var file = Path.Combine(paths.Instance(id), "server.properties");
        var fileExists = File.Exists(file);
        var originalText = fileExists ? await File.ReadAllTextAsync(file, cancellationToken) : "";
        var document = fileExists ? PropertiesDocument.Parse(originalText) : PropertiesDocument.Empty();
        document.Set("motd", dto.Motd); document.Set("max-players", dto.MaxPlayers.ToString());
        document.Set("gamemode", dto.GameMode); document.Set("difficulty", dto.Difficulty);
        document.Set("white-list", Lower(dto.Whitelist)); document.Set("online-mode", Lower(dto.OnlineMode));
        document.Set("pvp", Lower(dto.Pvp)); document.Set("enable-command-block", Lower(dto.CommandBlocks));
        document.Set("allow-flight", Lower(dto.AllowFlight)); document.Set("spawn-protection", dto.SpawnProtection.ToString());
        document.Set("view-distance", dto.ViewDistance.ToString()); document.Set("simulation-distance", dto.SimulationDistance.ToString());
        document.Set("level-name", dto.WorldName); document.Set("server-port", dto.Port.ToString());
        var updatedText = document.ToString();
        var changedProperties = !fileExists || !string.Equals(originalText, updatedText, StringComparison.Ordinal);
        var changedRuntime = server.MemoryMb != dto.MemoryMb || server.JavaRuntimeId != dto.JavaRuntimeId || server.JvmArguments != dto.JvmArguments || server.Port != dto.Port;
        server.Port = dto.Port; server.MemoryMb = dto.MemoryMb; server.JavaRuntimeId = dto.JavaRuntimeId;
        server.JvmArguments = dto.JvmArguments; server.StartOnBoot = dto.StartOnBoot; server.CrashRecovery = dto.CrashRecovery;
        server.RestartRequired |= server.State == ServerState.Running && (changedProperties || changedRuntime);
        server.UpdatedAt = DateTimeOffset.UtcNow;
        if (changedProperties) await SaveWithAtomicPropertiesAsync(db, file, updatedText, fileExists, cancellationToken);
        else await db.SaveChangesAsync(cancellationToken);
        return dto;
    }

    private static async Task SaveWithAtomicPropertiesAsync(
        StateDbContext db,
        string destination,
        string content,
        bool destinationExisted,
        CancellationToken requestCancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $".server.properties.{nonce}.tmp");
        var rollback = Path.Combine(directory, $".server.properties.{nonce}.rollback");
        var activated = false;
        var committed = false;
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), requestCancellationToken);
            requestCancellationToken.ThrowIfCancellationRequested();
            if (destinationExisted) File.Replace(temporary, destination, rollback);
            else File.Move(temporary, destination);
            activated = true;

            // The live file is now activated. Finish the matching DB update even if the HTTP request disconnects.
            await db.SaveChangesAsync(CancellationToken.None);
            committed = true;
        }
        finally
        {
            if (activated && !committed)
            {
                if (destinationExisted)
                {
                    if (!File.Exists(rollback)) throw new IOException("The prior server.properties rollback file is missing.");
                    if (File.Exists(destination)) File.Replace(rollback, destination, null);
                    else File.Move(rollback, destination);
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(rollback)) File.Delete(rollback);
        }
    }

    private static int Integer(PropertiesDocument d, string key, int fallback) => int.TryParse(d.Get(key), out var value) ? value : fallback;
    private static bool Boolean(PropertiesDocument d, string key, bool fallback) => bool.TryParse(d.Get(key), out var value) ? value : fallback;
    private static string Lower(bool value) => value ? "true" : "false";

    private static void Validate(ServerConfigurationDto dto)
    {
        if (dto is null || dto.Motd is null || string.IsNullOrWhiteSpace(dto.GameMode) || string.IsNullOrWhiteSpace(dto.Difficulty) ||
            string.IsNullOrWhiteSpace(dto.WorldName) || string.IsNullOrWhiteSpace(dto.JavaRuntimeId) || dto.JvmArguments is null)
            throw PanelProblems.Validation("Required server settings cannot be null.");
        if (dto.Motd.Length > MaxMotdLength)
            throw PanelProblems.Validation($"MOTD may contain at most {MaxMotdLength} characters.");
        if (dto.WorldName.Length > MaxWorldNameLength || Encoding.UTF8.GetByteCount(dto.WorldName) > MaxWorldNameUtf8Bytes)
            throw PanelProblems.Validation($"World name may contain at most {MaxWorldNameLength} characters and {MaxWorldNameUtf8Bytes} UTF-8 bytes.");
        if (dto.JvmArguments.Length > MaxJvmArgumentsLength)
            throw PanelProblems.Validation($"JVM arguments may contain at most {MaxJvmArgumentsLength} characters.");
        if (dto.Motd.Any(char.IsControl) || dto.WorldName.Any(char.IsControl))
            throw PanelProblems.Validation("MOTD and world name cannot contain control characters.");
        if (dto.MaxPlayers is < 1 or > 10_000 || dto.Port is < 1024 or > 65535 ||
            dto.MemoryMb < PanelOptions.MinimumServerMemoryMb || dto.MemoryMb % PanelOptions.ServerMemoryStepMb != 0 ||
            dto.ViewDistance is < 2 or > 64 || dto.SimulationDistance is < 2 or > 64 || dto.SpawnProtection is < 0 or > 10_000)
            throw PanelProblems.Validation("One or more numeric settings are outside the supported range.");
        if (!new[] { "survival", "creative", "adventure", "spectator" }.Contains(dto.GameMode, StringComparer.OrdinalIgnoreCase) ||
            !new[] { "peaceful", "easy", "normal", "hard" }.Contains(dto.Difficulty, StringComparer.OrdinalIgnoreCase))
            throw PanelProblems.Validation("Game mode or difficulty is invalid.");
        if (string.IsNullOrWhiteSpace(dto.WorldName) || dto.WorldName is "." or ".." ||
            dto.WorldName.Contains(Path.DirectorySeparatorChar) || dto.WorldName.Contains(Path.AltDirectorySeparatorChar) ||
            dto.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw PanelProblems.Validation("World name is invalid.");
    }
}
