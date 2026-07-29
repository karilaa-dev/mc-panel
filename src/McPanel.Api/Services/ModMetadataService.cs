using System.IO.Compression;
using System.Text;
using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tomlyn;
using Tomlyn.Model;

namespace McPanel.Api.Services;

public sealed class ModMetadataService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    IOptions<PanelOptions> options)
{
    public Task<IReadOnlyList<ModFileDto>> ListAsync(Guid serverId, CancellationToken cancellationToken) =>
        ListDirectoryAsync(serverId, plugin: false, cancellationToken);

    public Task<IReadOnlyList<ModFileDto>> ListPluginsAsync(Guid serverId, CancellationToken cancellationToken) =>
        ListDirectoryAsync(serverId, plugin: true, cancellationToken);

    private async Task<IReadOnlyList<ModFileDto>> ListDirectoryAsync(
        Guid serverId, bool plugin, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
            ?? throw PanelProblems.NotFound("Server");
        if (!plugin && server.Kind is not (ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge))
            throw PanelProblems.Validation("Mods are available only for Fabric, Forge, and NeoForge servers.");
        if (plugin && server.Kind != ServerKind.Paper)
            throw PanelProblems.Validation("Plugins are available only for Paper servers.");

        var directory = Path.Combine(paths.Instance(serverId), plugin ? "plugins" : "mods");
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(IsRegularFile)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<ModFileDto>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(plugin
                ? await ReadPluginFileAsync(file, cancellationToken)
                : await ReadFileAsync(server.Kind, file, cancellationToken));
        }
        return result;
    }

    internal async Task<ModFileDto> ReadFileAsync(ServerKind kind, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            return Invalid(info, "Symbolic-link mod files are not scanned.");
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > options.Value.MaxArchiveEntries)
                return Invalid(info, "The JAR contains too many archive entries.");
            var manifest = await ReadManifestAsync(archive, cancellationToken);
            return await ReadMetadataAsync(kind, info, archive, manifest, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or TomlException)
        {
            return Invalid(info, CleanMessage(exception.Message));
        }
    }

    internal async Task<ModFileDto> ReadPluginFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            return Invalid(info, "Symbolic-link plugin files are not scanned.");
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > options.Value.MaxArchiveEntries)
                return Invalid(info, "The JAR contains too many archive entries.");
            var descriptor = FindEntry(archive, "paper-plugin.yml") ?? FindEntry(archive, "plugin.yml");
            if (descriptor is null) return Unrecognized(info, "No Paper or Bukkit plugin descriptor was found.");
            var values = ParsePluginDescriptor(await ReadTextAsync(descriptor, cancellationToken));
            var name = Value(values, "name");
            var version = Value(values, "version");
            var description = Value(values, "description");
            var authors = Values(values, "authors", "author");
            var declaration = new ModDeclarationDto(name, name, version, description, authors);
            return Parsed(info, descriptor.FullName, null, [declaration]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Invalid(info, CleanMessage(exception.Message));
        }
    }

    private async Task<ModFileDto> ReadMetadataAsync(
        ServerKind kind,
        FileInfo file,
        ZipArchive archive,
        IReadOnlyDictionary<string, string> manifest,
        CancellationToken cancellationToken)
    {
        var fabric = FindEntry(archive, "fabric.mod.json");
        var forge = FindEntry(archive, "META-INF/mods.toml");
        var neoForge = FindEntry(archive, "META-INF/neoforge.mods.toml");
        var legacyForge = FindEntry(archive, "mcmod.info");

        if (kind == ServerKind.Fabric && fabric is not null)
            return await ReadFabricAsync(file, fabric, cancellationToken);
        if (kind == ServerKind.Forge)
        {
            if (forge is not null) return await ReadTomlAsync(file, forge, "mods.toml", manifest, cancellationToken);
            if (legacyForge is not null) return await ReadLegacyForgeAsync(file, legacyForge, cancellationToken);
        }
        if (kind == ServerKind.NeoForge)
        {
            if (neoForge is not null) return await ReadTomlAsync(file, neoForge, "neoforge.mods.toml", manifest, cancellationToken);
            if (forge is not null) return await ReadTomlAsync(file, forge, "mods.toml", manifest, cancellationToken);
        }

        // Still expose valid metadata from a JAR built for a different loader. The inventory is
        // intentionally descriptive; it does not claim compatibility or perform dependency checks.
        if (fabric is not null) return await ReadFabricAsync(file, fabric, cancellationToken);
        if (neoForge is not null) return await ReadTomlAsync(file, neoForge, "neoforge.mods.toml", manifest, cancellationToken);
        if (forge is not null) return await ReadTomlAsync(file, forge, "mods.toml", manifest, cancellationToken);
        if (legacyForge is not null) return await ReadLegacyForgeAsync(file, legacyForge, cancellationToken);
        return Unrecognized(file);
    }

    private async Task<ModFileDto> ReadFabricAsync(FileInfo file, ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var text = await ReadTextAsync(entry, cancellationToken);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var declaration = new ModDeclarationDto(
            JsonString(root, "id"), JsonString(root, "name"), JsonString(root, "version"),
            JsonString(root, "description"), ReadJsonAuthors(root));
        var license = ReadJsonStringOrArray(root, "license");
        return Parsed(file, "fabric.mod.json", license, [declaration]);
    }

    private async Task<ModFileDto> ReadTomlAsync(
        FileInfo file,
        ZipArchiveEntry entry,
        string format,
        IReadOnlyDictionary<string, string> manifest,
        CancellationToken cancellationToken)
    {
        var text = await ReadTextAsync(entry, cancellationToken);
        var model = TomlSerializer.Deserialize<TomlTable>(text)
            ?? throw new InvalidDataException("The TOML metadata is empty.");
        var license = ValueString(TomlValue(model, "license"));
        var properties = TomlValue(model, "properties") as TomlTable;
        var declarations = new List<ModDeclarationDto>();
        if (TomlValue(model, "mods") is TomlTableArray mods)
        {
            foreach (var mod in mods)
            {
                var version = ResolveVersion(ValueString(TomlValue(mod, "version")), properties, manifest);
                declarations.Add(new ModDeclarationDto(
                    ValueString(TomlValue(mod, "modId")),
                    ValueString(TomlValue(mod, "displayName")),
                    version,
                    ValueString(TomlValue(mod, "description")),
                    ReadTomlAuthors(TomlValue(mod, "authors"))));
            }
        }
        return Parsed(file, format, license, declarations);
    }

    private async Task<ModFileDto> ReadLegacyForgeAsync(FileInfo file, ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var text = await ReadTextAsync(entry, cancellationToken);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var modList) ? modList : default;
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("mcmod.info does not contain a mod list.");
        var declarations = new List<ModDeclarationDto>();
        foreach (var mod in list.EnumerateArray())
        {
            declarations.Add(new ModDeclarationDto(
                JsonString(mod, "modid"), JsonString(mod, "name"), JsonString(mod, "version"),
                JsonString(mod, "description"), ReadJsonAuthors(mod, "authorList", "authors")));
        }
        return Parsed(file, "mcmod.info", null, declarations);
    }

    private ModFileDto Parsed(FileInfo file, string format, string? license, IReadOnlyList<ModDeclarationDto> mods)
    {
        var partial = mods.Count == 0 || mods.Any(x => IsMissingOrUnresolved(x.Id) || IsMissingOrUnresolved(x.Version));
        return new ModFileDto(file.Name, file.Length, format,
            partial ? ModParseStatus.Partial : ModParseStatus.Parsed,
            mods.Count == 0 ? "No mod declarations were found in the metadata." : partial ? "Some metadata fields are missing or unresolved." : null,
            license, mods);
    }

    private static ModFileDto Invalid(FileInfo file, string message) =>
        new(file.Name, SafeLength(file), null, ModParseStatus.Invalid, message, null, []);

    private static ModFileDto Unrecognized(FileInfo file, string message = "No supported loader metadata was found.") =>
        new(file.Name, SafeLength(file), null, ModParseStatus.Unrecognized, message, null, []);

    private async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var limit = Math.Min(options.Value.MaxTextFileBytes, 2L * 1024 * 1024);
        if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException("A metadata entry exceeds the safe size limit.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 16 * 1024, leaveOpen: false);
        var buffer = new char[16 * 1024];
        var builder = new StringBuilder((int)Math.Min(entry.Length, 64 * 1024));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > limit)
                throw new InvalidDataException("A metadata entry exceeds the safe size limit.");
        }
        return builder.ToString();
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = FindEntry(archive, "META-INF/MANIFEST.MF");
        if (entry is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = await ReadTextAsync(entry, cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith(' ') && current is not null) { values[current] += line[1..]; continue; }
            var separator = line.IndexOf(':');
            if (separator <= 0) { current = null; continue; }
            current = line[..separator].Trim();
            values[current] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static string? ResolveVersion(string? value, TomlTable? properties, IReadOnlyDictionary<string, string> manifest)
    {
        if (value == "${file.jarVersion}") return manifest.GetValueOrDefault("Implementation-Version") ?? value;
        if (value is { Length: > 9 } && value.StartsWith("${file.", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            var key = value[7..^1];
            return properties is not null ? ValueString(TomlValue(properties, key)) ?? value : value;
        }
        return value;
    }

    private static IReadOnlyList<string> ReadTomlAuthors(object? value) => value switch
    {
        string text => SplitAuthors(text),
        TomlArray array => array.Select(ValueString).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList(),
        _ => []
    };

    private static IReadOnlyList<string> ReadJsonAuthors(JsonElement root, params string[] names)
    {
        if (names.Length == 0) names = ["authors"];
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return SplitAuthors(value.GetString());
            if (value.ValueKind != JsonValueKind.Array) continue;
            return value.EnumerateArray().Select(x => x.ValueKind switch
                {
                    JsonValueKind.String => x.GetString(),
                    JsonValueKind.Object when x.TryGetProperty("name", out var authorName) => authorName.GetString(),
                    _ => null
                })
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        }
        return [];
    }

    private static IReadOnlyList<string> SplitAuthors(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParsePluginDescriptor(string text)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? listKey = null;
        foreach (var sourceLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (line.Length != trimmed.Length)
            {
                if (listKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    values[listKey].Add(Unquote(trimmed[2..].Trim()));
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0) { listKey = null; continue; }
            var key = line[..separator].Trim();
            var raw = line[(separator + 1)..].Trim();
            if (!values.TryGetValue(key, out var items)) values[key] = items = [];
            items.Clear();
            if (raw.Length == 0) { listKey = key; continue; }
            listKey = null;
            if (raw.StartsWith('[') && raw.EndsWith(']'))
                items.AddRange(raw[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Unquote));
            else items.Add(Unquote(raw));
        }
        return values.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Value(IReadOnlyDictionary<string, IReadOnlyList<string>> values, string key) =>
        values.TryGetValue(key, out var items) ? items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;

    private static IReadOnlyList<string> Values(
        IReadOnlyDictionary<string, IReadOnlyList<string>> values, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var items) && items.Count > 0)
                return items.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return [];
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1] : value;
    }

    private static string? ReadJsonStringOrArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
            : null;
    }

    private static string? JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static string? ValueString(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static object? TomlValue(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(x => x.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static long SafeLength(FileInfo file) { try { return file.Length; } catch { return 0; } }
    private static bool IsMissingOrUnresolved(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("${", StringComparison.Ordinal);
    private static bool IsRegularFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && file.LinkTarget is null;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static string CleanMessage(string value) => string.IsNullOrWhiteSpace(value) ? "The mod metadata could not be read." : value.Length <= 256 ? value : value[..256];
}
