using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed partial class PlayerService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    AsyncKeyedLock keyedLock,
    ProcessSupervisor supervisor,
    IHttpClientFactory clients)
{
    private const int MaximumListBytes = 1_048_576;
    private const int MaximumProfileBytes = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<PlayerDto>> ListAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        _ = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var observed = await db.Players.AsNoTracking().Where(x => x.ServerId == id).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var root = paths.Instance(id);
        var whitelist = await ReadListAsync(root, "whitelist.json", cancellationToken);
        var operators = await ReadListAsync(root, "ops.json", cancellationToken);
        var banned = await ReadListAsync(root, "banned-players.json", cancellationToken);
        var players = new List<PlayerView>();

        var canonicalProfiles = whitelist.Profiles.Concat(operators.Profiles).Concat(banned.Profiles)
            .Concat(observed.Where(player => ValidUuidOrNull(player.Uuid) is not null)
                .Select(player => new Profile(player.Name, ValidUuidOrNull(player.Uuid))))
            .ToList();
        foreach (var player in observed.Where(player => player.Uuid is not null))
            GetOrAdd(players, new Profile(player.Name, ValidUuidOrNull(player.Uuid))).Online = player.Online;
        foreach (var profile in whitelist.Profiles) GetOrAdd(players, profile).Whitelisted = true;
        foreach (var profile in operators.Profiles) GetOrAdd(players, profile).Operator = true;
        foreach (var profile in banned.Profiles) GetOrAdd(players, profile).Banned = true;
        foreach (var player in observed.Where(player => player.Uuid is null))
        {
            if (canonicalProfiles.Any(profile => MinecraftLogText.IsLegacyAnsiLeakOf(player.Name, profile.Name))) continue;
            GetOrAdd(players, new Profile(player.Name, null)).Online = player.Online;
        }

        PopulateInventoryAvailability(root, players);
        return players.OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Select(player => player.ToDto()).ToList();
    }

    public async Task<PlayerDto> ActionAsync(Guid id, string requestedName, string action, CancellationToken cancellationToken)
    {
        if (!PlayerNameRegex().IsMatch(requestedName)) throw PanelProblems.Validation("Player name is invalid.");
        var normalizedAction = action.ToLowerInvariant();
        if (normalizedAction is not ("whitelist" or "unwhitelist" or "op" or "deop" or "ban" or "pardon" or "kick"))
            throw PanelProblems.Validation("Unknown player action.");

        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var processRunning = supervisor.IsRunning(id);
        var running = server.State == ServerState.Running && processRunning;
        var stopped = (server.State is ServerState.Stopped or ServerState.Crashed) && !processRunning;
        if (!running && !stopped) throw PanelProblems.Conflict("SERVER_BUSY", "Player lists cannot be changed in the server's current state.");
        if (normalizedAction == "kick" && !running) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "Start the server before kicking a player.");

        var root = paths.Instance(id);
        var whitelist = await ReadListAsync(root, "whitelist.json", cancellationToken);
        var operators = await ReadListAsync(root, "ops.json", cancellationToken);
        var banned = await ReadListAsync(root, "banned-players.json", cancellationToken);
        var observed = await db.Players.Where(x => x.ServerId == id).ToListAsync(cancellationToken);
        var known = whitelist.Profiles.Concat(operators.Profiles).Concat(banned.Profiles)
            .Concat(observed.Select(player => new Profile(player.Name, ValidUuidOrNull(player.Uuid)))).ToList();
        var requiresUuid = normalizedAction is "whitelist" or "op" or "ban";
        var knownProfile = FindProfile(known, requestedName);
        var profile = requiresUuid && knownProfile?.Uuid is null
            ? await ResolveProfileAsync(server, requestedName, known, root, cancellationToken)
            : knownProfile ?? new Profile(requestedName, null);

        var player = FindObserved(observed, profile) ?? new PlayerEntity
        {
            ServerId = id,
            Name = profile.Name,
            Uuid = profile.Uuid
        };
        if (player.Id == 0) db.Players.Add(player);
        player.Name = profile.Name;
        if (profile.Uuid is not null) player.Uuid = profile.Uuid;
        player.Whitelisted = whitelist.Profiles.Any(item => Matches(item, profile));
        player.Operator = operators.Profiles.Any(item => Matches(item, profile));
        player.Banned = banned.Profiles.Any(item => Matches(item, profile));

        if (running)
        {
            await supervisor.CommandAsync(id, Command(normalizedAction, profile.Name), cancellationToken);
            ApplyState(player, normalizedAction);
            player.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Player(player);
        }

        var target = normalizedAction switch
        {
            "whitelist" or "unwhitelist" => whitelist,
            "op" or "deop" => operators,
            "ban" or "pardon" => banned,
            _ => throw PanelProblems.Validation("Unknown player action.")
        };
        var add = normalizedAction is "whitelist" or "op" or "ban";
        var changed = UpdateList(target.Root, profile, normalizedAction, add);
        ApplyState(player, normalizedAction);
        player.Online = false;
        player.LastSeenAt = DateTimeOffset.UtcNow;
        if (changed) await SaveListWithDatabaseAsync(db, target, cancellationToken);
        else await db.SaveChangesAsync(cancellationToken);
        return Player(player);
    }

    private async Task<Profile> ResolveProfileAsync(
        ServerEntity server,
        string requestedName,
        IReadOnlyList<Profile> known,
        string root,
        CancellationToken cancellationToken)
    {
        var existing = FindProfile(known, requestedName);
        if (existing?.Uuid is not null) return existing;
        var cache = await ReadListAsync(root, "usercache.json", cancellationToken);
        existing = FindProfile(cache.Profiles, requestedName);
        if (existing?.Uuid is not null) return existing;

        var propertiesPath = Path.Combine(root, "server.properties");
        var properties = File.Exists(propertiesPath)
            ? PropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken))
            : PropertiesDocument.Empty();
        if (bool.TryParse(properties.Get("online-mode"), out var onlineMode) && !onlineMode)
            return new Profile(requestedName, OfflineUuid(requestedName));
        return await LookupOnlineProfileAsync(requestedName, cancellationToken);
    }

    private async Task<Profile> LookupOnlineProfileAsync(string name, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient("minecraft-profile").GetAsync(
                $"minecraft/profile/lookup/name/{Uri.EscapeDataString(name)}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile service timed out.");
        }
        catch (HttpRequestException)
        {
            throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile service is unavailable.");
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new PanelException(404, "PLAYER_NOT_FOUND", "No Minecraft profile was found for that nickname.");
            if (!response.IsSuccessStatusCode)
                throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile service is unavailable.");
            if (response.Content.Headers.ContentLength is > MaximumProfileBytes)
                throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile response was unexpectedly large.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumProfileBytes)
                    throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile response was unexpectedly large.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            try
            {
                using var document = JsonDocument.Parse(buffer.ToArray());
                var profileName = document.RootElement.GetProperty("name").GetString();
                var uuid = document.RootElement.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(uuid) || !PlayerNameRegex().IsMatch(profileName))
                    throw new JsonException();
                return new Profile(profileName, NormalizeUuid(uuid));
            }
            catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The Minecraft profile service returned an invalid response.");
            }
        }
    }

    private static async Task<ListFile> ReadListAsync(string root, string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, fileName);
        if (!File.Exists(path)) return new ListFile(path, false, new JsonArray(), []);
        var info = new FileInfo(path);
        if (info.Length > MaximumListBytes) throw InvalidList(fileName);
        try
        {
            var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken);
            var rootNode = JsonNode.Parse(text) as JsonArray ?? throw new JsonException();
            var profiles = new List<Profile>();
            foreach (var node in rootNode)
            {
                if (node is not JsonObject item) throw new JsonException();
                var name = item["name"]?.GetValue<string>();
                var uuid = item["uuid"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uuid)) throw new JsonException();
                profiles.Add(new Profile(name, NormalizeUuid(uuid)));
            }
            return new ListFile(path, true, rootNode, profiles);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or InvalidOperationException)
        {
            throw InvalidList(fileName);
        }
    }

    private static bool UpdateList(JsonArray list, Profile profile, string action, bool add)
    {
        var matches = list.Select((node, index) => new { node, index })
            .Where(item => item.node is JsonObject value && Matches(
                value["name"]?.GetValue<string>() ?? "",
                value["uuid"]?.GetValue<string>(),
                profile))
            .Select(item => item.index).ToList();
        if (!add)
        {
            for (var index = matches.Count - 1; index >= 0; index--) list.RemoveAt(matches[index]);
            return matches.Count > 0;
        }
        if (matches.Count > 0) return false;
        list.Add(action switch
        {
            "op" => new JsonObject
            {
                ["uuid"] = profile.Uuid,
                ["name"] = profile.Name,
                ["level"] = 4,
                ["bypassesPlayerLimit"] = false
            },
            "ban" => new JsonObject
            {
                ["uuid"] = profile.Uuid,
                ["name"] = profile.Name,
                ["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss '+0000'", CultureInfo.InvariantCulture),
                ["source"] = "MC Panel",
                ["expires"] = "forever",
                ["reason"] = "Banned by an operator."
            },
            _ => new JsonObject { ["uuid"] = profile.Uuid, ["name"] = profile.Name }
        });
        return true;
    }

    private static async Task SaveListWithDatabaseAsync(StateDbContext db, ListFile list, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(list.Path)!);
        var temporary = list.Path + $".mcpanel-{Guid.NewGuid():N}.tmp";
        var rollback = list.Path + $".mcpanel-{Guid.NewGuid():N}.rollback";
        var activated = false;
        var committed = false;
        try
        {
            var content = list.Root.ToJsonString(JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (list.Existed) File.Replace(temporary, list.Path, rollback);
            else File.Move(temporary, list.Path);
            activated = true;
            await db.SaveChangesAsync(CancellationToken.None);
            committed = true;
        }
        finally
        {
            if (activated && !committed)
            {
                if (list.Existed)
                {
                    if (!File.Exists(rollback)) throw new IOException("The prior player list rollback file is missing.");
                    if (File.Exists(list.Path)) File.Replace(rollback, list.Path, null);
                    else File.Move(rollback, list.Path);
                }
                else if (File.Exists(list.Path)) File.Delete(list.Path);
            }
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(rollback)) File.Delete(rollback);
        }
    }

    private static string Command(string action, string name) => action switch
    {
        "whitelist" => $"whitelist add {name}",
        "unwhitelist" => $"whitelist remove {name}",
        "op" => $"op {name}",
        "deop" => $"deop {name}",
        "ban" => $"ban {name}",
        "pardon" => $"pardon {name}",
        "kick" => $"kick {name}",
        _ => throw PanelProblems.Validation("Unknown player action.")
    };

    private static void ApplyState(PlayerEntity player, string action)
    {
        switch (action)
        {
            case "whitelist": player.Whitelisted = true; break;
            case "unwhitelist": player.Whitelisted = false; break;
            case "op": player.Operator = true; break;
            case "deop": player.Operator = false; break;
            case "ban": player.Banned = true; player.Online = false; break;
            case "pardon": player.Banned = false; break;
            case "kick": player.Online = false; break;
        }
    }

    private static Profile? FindProfile(IEnumerable<Profile> profiles, string name) =>
        profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(Profile left, Profile right) => Matches(left.Name, left.Uuid, right);

    private static bool Matches(string name, string? uuid, Profile profile) =>
        (uuid is not null && profile.Uuid is not null && UuidEquals(uuid, profile.Uuid)) ||
        name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase);

    private static PlayerView GetOrAdd(List<PlayerView> players, Profile profile)
    {
        var player = profile.Uuid is null ? null : players.FirstOrDefault(item =>
            item.Uuid is not null && UuidEquals(item.Uuid, profile.Uuid));
        player ??= players.FirstOrDefault(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (player is not null)
        {
            player.Uuid ??= profile.Uuid;
            return player;
        }
        player = new PlayerView(profile.Name, profile.Uuid);
        players.Add(player);
        return player;
    }

    private static PlayerEntity? FindObserved(IReadOnlyList<PlayerEntity> players, Profile profile)
    {
        var player = profile.Uuid is null ? null : players.FirstOrDefault(item =>
            item.Uuid is not null && UuidEquals(item.Uuid, profile.Uuid));
        return player ?? players.FirstOrDefault(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UuidEquals(string left, string right) =>
        left.Replace("-", "", StringComparison.Ordinal).Equals(
            right.Replace("-", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    private static PlayerDto Player(PlayerEntity player) => new(
        player.Name, player.Uuid, player.Online, player.Whitelisted, player.Operator, player.Banned);

    private static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return FormatUuid(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private static string NormalizeUuid(string value)
    {
        var hex = value.Replace("-", "", StringComparison.Ordinal);
        if (hex.Length != 32 || hex.Any(character => !Uri.IsHexDigit(character))) throw new FormatException("Player UUID is invalid.");
        return FormatUuid(hex.ToLowerInvariant());
    }

    private static string? ValidUuidOrNull(string? value)
    {
        if (value is null) return null;
        try { return NormalizeUuid(value); }
        catch (FormatException) { return null; }
    }

    private static string FormatUuid(string hex) => $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";

    private static void PopulateInventoryAvailability(string instanceRoot, IReadOnlyList<PlayerView> players)
    {
        try
        {
            var properties = Path.Combine(instanceRoot, "server.properties");
            var world = File.Exists(properties) ? PropertiesDocument.Parse(File.ReadAllText(properties)).Get("level-name") ?? "world" : "world";
            if (world is "." or ".." || world.Contains(Path.DirectorySeparatorChar) || world.Contains(Path.AltDirectorySeparatorChar) || world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            foreach (var player in players)
            {
                var uuid = ValidUuidOrNull(player.Uuid);
                if (uuid is null) continue;
                var path = PlayerInventoryService.FindSavedPlayerDataPath(instanceRoot, world, uuid);
                if (path is null || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                player.InventoryAvailable = true;
                player.InventorySavedAt = File.GetLastWriteTimeUtc(path);
            }
        }
        catch { }
    }
    private static PanelException InvalidList(string fileName) => new(
        409,
        "PLAYER_LIST_INVALID",
        $"{fileName} is malformed and was not changed.");

    private sealed record Profile(string Name, string? Uuid);
    private sealed record ListFile(string Path, bool Existed, JsonArray Root, IReadOnlyList<Profile> Profiles);

    private sealed class PlayerView(string name, string? uuid)
    {
        public string Name { get; } = name;
        public string? Uuid { get; set; } = uuid;
        public bool Online { get; set; }
        public bool Whitelisted { get; set; }
        public bool Operator { get; set; }
        public bool Banned { get; set; }
        public bool InventoryAvailable { get; set; }
        public DateTimeOffset? InventorySavedAt { get; set; }
        public PlayerDto ToDto() => new(Name, Uuid, Online, Whitelisted, Operator, Banned, InventoryAvailable, InventorySavedAt);
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerNameRegex();
}
