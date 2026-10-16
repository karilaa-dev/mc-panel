using McPanel.Api.Contracts;

namespace McPanel.Api.Services;

public sealed record PropertyDefaultRange(string From, string? To, string Value);

public sealed record ServerPropertyDefinition(
    string Key,
    string Type,
    string Section,
    bool Sensitive,
    IReadOnlyList<PropertyVersionRangeDto> SupportedRanges,
    IReadOnlyList<PropertyDefaultRange> Defaults);

public sealed record PropertyCatalogResult(
    ServerPropertyDefinition Definition,
    string SuggestedValue,
    PropertyCompatibility Compatibility);

/// <summary>
/// Versioned metadata for the dedicated-server properties exposed by the panel. The catalog starts
/// with the first release for which Mojang published a dedicated-server artifact (1.2.5). A
/// definition can have several support/default spans so removed and later reintroduced keys remain
/// representable without changing the server.properties parser.
/// </summary>
public static class ServerPropertyCatalog
{
    public static readonly IReadOnlyList<string> Sections =
    [
        "General", "World", "Gameplay", "Players & permissions", "Network & status", "Security",
        "Resource packs", "Remote administration", "Performance", "Other"
    ];

    // This explicit release baseline makes compatibility deterministic when Mojang's online
    // manifest is unavailable. Add new stable releases here before claiming support for them.
    public static readonly IReadOnlyList<string> StableVersions =
    [
        "1.2.5", "1.3.1", "1.3.2", "1.4.2", "1.4.4", "1.4.5", "1.4.6", "1.4.7",
        "1.5.1", "1.5.2", "1.6.1", "1.6.2", "1.6.4", "1.7.2", "1.7.3", "1.7.4",
        "1.7.5", "1.7.6", "1.7.7", "1.7.8", "1.7.9", "1.7.10", "1.8", "1.8.1",
        "1.8.2", "1.8.3", "1.8.4", "1.8.5", "1.8.6", "1.8.7", "1.8.8", "1.8.9",
        "1.9", "1.9.1", "1.9.2", "1.9.3", "1.9.4", "1.10", "1.10.1", "1.10.2",
        "1.11", "1.11.1", "1.11.2", "1.12", "1.12.1", "1.12.2", "1.13", "1.13.1",
        "1.13.2", "1.14", "1.14.1", "1.14.2", "1.14.3", "1.14.4", "1.15", "1.15.1",
        "1.15.2", "1.16", "1.16.1", "1.16.2", "1.16.3", "1.16.4", "1.16.5", "1.17",
        "1.17.1", "1.18", "1.18.1", "1.18.2", "1.19", "1.19.1", "1.19.2", "1.19.3",
        "1.19.4", "1.20", "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6",
        "1.21", "1.21.1", "1.21.2", "1.21.3", "1.21.4", "1.21.5", "1.21.6", "1.21.7",
        "1.21.8", "1.21.9", "1.21.10", "1.21.11", "26.1", "26.1.1", "26.1.2", "26.2"
    ];

    private static readonly HashSet<string> StableVersionSet = new(StableVersions, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<int, string> SnapshotBaselines = new Dictionary<int, string>
    {
        [12] = "1.2.5", [13] = "1.4.7", [14] = "1.7.10", [15] = "1.8.9", [16] = "1.9.4",
        [17] = "1.11.2", [18] = "1.12.2", [19] = "1.13.2", [20] = "1.15.2", [21] = "1.16.5",
        [22] = "1.18.2", [23] = "1.19.3", [24] = "1.20.4", [25] = "1.21.4", [26] = "26.1"
    };

    public static readonly IReadOnlyList<ServerPropertyDefinition> Definitions =
    [
        P("motd", "text", "General", "A Minecraft Server"),
        P("bug-report-link", "text", "General", "", "1.20.5"),
        P("enable-code-of-conduct", "boolean", "General", "false", "1.21.6"),
        P("level-name", "text", "World", "world"),
        P("level-seed", "text", "World", ""),
        P("level-type", "text", "World", "minecraft:normal", defaults:
        [
            new("1.2.5", "1.18.2", "DEFAULT"), new("1.19", null, "minecraft:normal")
        ]),
        P("generator-settings", "text", "World", "{}", defaults:
        [
            new("1.2.5", "1.12.2", ""), new("1.13", null, "{}")
        ]),
        P("generate-structures", "boolean", "World", "true"),
        P("max-world-size", "integer", "World", "29999984", "1.8"),
        P("allow-nether", "boolean", "World", "true", "1.2.5", "1.21.11"),
        P("spawn-animals", "boolean", "World", "true", "1.2.5", "1.21.11"),
        P("spawn-monsters", "boolean", "World", "true", "1.2.5", "1.21.11"),
        P("spawn-npcs", "boolean", "World", "true", "1.2.5", "1.21.11"),
        P("gamemode", "text", "Gameplay", "survival", defaults:
        [
            new("1.2.5", "1.12.2", "0"), new("1.13", null, "survival")
        ]),
        P("difficulty", "text", "Gameplay", "easy", defaults:
        [
            new("1.2.5", "1.13.2", "1"), new("1.14", null, "easy")
        ]),
        P("hardcore", "boolean", "Gameplay", "false"),
        P("force-gamemode", "boolean", "Gameplay", "false", "1.3.1"),
        P("pvp", "boolean", "Gameplay", "true", "1.2.5", "1.21.11"),
        P("allow-flight", "boolean", "Gameplay", "false"),
        P("enable-command-block", "boolean", "Gameplay", "false", "1.4.2", "1.21.11"),
        P("spawn-protection", "integer", "Gameplay", "16"),
        P("max-players", "integer", "Players & permissions", "20"),
        P("player-idle-timeout", "integer", "Players & permissions", "0", "1.8"),
        P("white-list", "boolean", "Players & permissions", "false"),
        P("enforce-whitelist", "boolean", "Players & permissions", "false", "1.13"),
        P("op-permission-level", "integer", "Players & permissions", "4", "1.7.2"),
        P("function-permission-level", "integer", "Players & permissions", "2", "1.12"),
        P("broadcast-console-to-ops", "boolean", "Players & permissions", "true", "1.7.2"),
        P("broadcast-rcon-to-ops", "boolean", "Players & permissions", "true", "1.14"),
        P("announce-player-achievements", "boolean", "Players & permissions", "true", "1.5.1", "1.12.2"),
        P("server-ip", "text", "Network & status", ""),
        P("server-port", "integer", "Network & status", "25565"),
        P("enable-status", "boolean", "Network & status", "true", "1.7.2"),
        P("hide-online-players", "boolean", "Network & status", "false", "1.16.4"),
        P("status-heartbeat-interval", "integer", "Network & status", "0", "26.1"),
        P("network-compression-threshold", "integer", "Network & status", "256", "1.8.1"),
        P("prevent-proxy-connections", "boolean", "Network & status", "false", "1.9.1"),
        P("rate-limit", "integer", "Network & status", "0", "1.16.4"),
        P("chat-spam-threshold-seconds", "integer", "Network & status", "10", "26.2"),
        P("command-spam-threshold-seconds", "integer", "Network & status", "10", "26.2"),
        P("accepts-transfers", "boolean", "Network & status", "false", "1.20.5"),
        P("online-mode", "boolean", "Security", "true"),
        P("enforce-secure-profile", "boolean", "Security", "true", "1.19"),
        P("log-ips", "boolean", "Security", "true", "1.20.2"),
        P("text-filtering-config", "text", "Security", "", "1.16.4"),
        P("text-filtering-version", "integer", "Security", "0", "26.1"),
        P("resource-pack", "text", "Resource packs", "", "1.6.1"),
        P("resource-pack-sha1", "text", "Resource packs", "", "1.11"),
        P("resource-pack-id", "text", "Resource packs", "", "1.20.3"),
        P("resource-pack-prompt", "text", "Resource packs", "", "1.17"),
        P("require-resource-pack", "boolean", "Resource packs", "false", "1.17"),
        P("initial-enabled-packs", "text", "Resource packs", "vanilla", "1.19.3"),
        P("initial-disabled-packs", "text", "Resource packs", "", "1.19.3"),
        P("texture-pack", "text", "Resource packs", "", "1.2.5", "1.5.2"),
        P("resource-pack-hash", "text", "Resource packs", "", "1.6.1", "1.10.2"),
        P("enable-query", "boolean", "Remote administration", "false"),
        P("query.port", "integer", "Remote administration", "25565"),
        P("enable-rcon", "boolean", "Remote administration", "false"),
        P("rcon.port", "integer", "Remote administration", "25575"),
        P("rcon.password", "text", "Remote administration", "", sensitive: true),
        P("management-server-enabled", "boolean", "Remote administration", "false", "26.1"),
        P("management-server-host", "text", "Remote administration", "localhost", "26.1"),
        P("management-server-port", "integer", "Remote administration", "0", "26.1"),
        P("management-server-allowed-origins", "text", "Remote administration", "", "26.1"),
        P("management-server-secret", "text", "Remote administration", "", "26.1", sensitive: true),
        P("management-server-tls-enabled", "boolean", "Remote administration", "true", "26.1"),
        P("management-server-tls-keystore", "text", "Remote administration", "", "26.1"),
        P("management-server-tls-keystore-password", "text", "Remote administration", "", "26.1", sensitive: true),
        P("view-distance", "integer", "Performance", "10"),
        P("simulation-distance", "integer", "Performance", "10", "1.18"),
        P("entity-broadcast-range-percentage", "integer", "Performance", "100", "1.14"),
        P("max-tick-time", "integer", "Performance", "60000", "1.8"),
        P("max-chained-neighbor-updates", "integer", "Performance", "1000000", "1.19"),
        P("sync-chunk-writes", "boolean", "Performance", "true", "1.16"),
        P("use-native-transport", "boolean", "Performance", "true", "1.12"),
        P("region-file-compression", "text", "Performance", "deflate", "1.20.5"),
        P("pause-when-empty-seconds", "integer", "Performance", "60", "1.21.2"),
        P("enable-jmx-monitoring", "boolean", "Performance", "false", "1.16"),
        P("snooper-enabled", "boolean", "Other", "true", "1.3.1", "1.12.2"),
        P("max-build-height", "integer", "Other", "256", "1.2.5", "1.8.9"),
        P("debug", "boolean", "Other", "false", "1.2.5", "26.1.2")
    ];

    private static readonly IReadOnlyDictionary<string, ServerPropertyDefinition> ByKey =
        Definitions.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static ServerPropertyDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static PropertyCatalogResult Describe(ServerPropertyDefinition definition, string minecraftVersion)
    {
        var resolved = ResolveVersion(minecraftVersion);
        if (resolved is null)
            return new(definition, definition.Defaults[^1].Value, PropertyCompatibility.UnknownVersion);

        var supported = definition.SupportedRanges.Any(range => InRange(resolved, range.From, range.To));
        var compatibility = supported
            ? PropertyCompatibility.Supported
            : Compare(resolved, definition.SupportedRanges[0].From) < 0
                ? PropertyCompatibility.IntroducedLater
                : PropertyCompatibility.RemovedBefore;
        var historicalDefault = definition.Defaults.LastOrDefault(range => InRange(resolved, range.From, range.To));
        return new(definition, historicalDefault?.Value ?? definition.Defaults[^1].Value, compatibility);
    }

    public static string? ResolveVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var trimmed = version.Trim();
        if (StableVersionSet.Contains(trimmed)) return StableVersions.First(x => x.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        // Pre-releases and release candidates are evaluated against the latest stable release that
        // precedes their numeric target.
        var qualifier = trimmed.IndexOfAny(['-', ' ']);
        if (qualifier > 0 && TryParts(trimmed[..qualifier], out var target))
            return StableVersions.Where(candidate => CompareParts(Parts(candidate), target) < 0).LastOrDefault();

        // Weekly snapshots use the last stable baseline from the preceding development cycle.
        if (trimmed.Length >= 5 && int.TryParse(trimmed[..2], out var year) &&
            trimmed[2] is 'w' or 'W' && SnapshotBaselines.TryGetValue(year, out var baseline))
            return baseline;

        return null;
    }

    private static ServerPropertyDefinition P(
        string key, string type, string section, string defaultValue, string from = "1.2.5", string? to = null,
        bool sensitive = false, IReadOnlyList<PropertyDefaultRange>? defaults = null) =>
        new(key, type, section, sensitive,
            [new PropertyVersionRangeDto(from, to)],
            defaults ?? [new PropertyDefaultRange(from, to, defaultValue)]);

    private static bool InRange(string version, string from, string? to) =>
        Compare(version, from) >= 0 && (to is null || Compare(version, to) <= 0);

    private static int Compare(string left, string right) => CompareParts(Parts(left), Parts(right));

    private static int[] Parts(string value) => TryParts(value, out var parts) ? parts : [];

    private static bool TryParts(string value, out int[] parts)
    {
        var tokens = value.Split('.');
        parts = new int[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            if (!int.TryParse(tokens[i], out parts[i])) { parts = []; return false; }
        return parts.Length > 0;
    }

    private static int CompareParts(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var comparison = left.ElementAtOrDefault(i).CompareTo(right.ElementAtOrDefault(i));
            if (comparison != 0) return comparison;
        }
        return 0;
    }
}
