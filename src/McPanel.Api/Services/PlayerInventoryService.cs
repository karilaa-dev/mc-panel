using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using fNbt;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed partial class PlayerInventoryService(
    PanelPaths paths, IDbContextFactory<StateDbContext> stateFactory,
    AsyncKeyedLock keyedLock, IServerProcessStatus supervisor)
{
    private const int MaximumCompressedBytes = 8 * 1024 * 1024;
    private const int MaximumDecompressedBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly IReadOnlyList<SlotDefinition> Definitions = BuildDefinitions();
    private static readonly IReadOnlyDictionary<(string Section, int Index), SlotDefinition> ByUi =
        Definitions.ToDictionary(x => (x.Section, x.Index), SlotKeyComparer.Instance);
    private static readonly IReadOnlyDictionary<(string List, int Slot), SlotDefinition> ByNbt =
        Definitions.ToDictionary(x => (x.List, x.NbtSlot));

    public async Task<PlayerInventoryDto> GetAsync(Guid serverId, string requestedUuid, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(serverId, requestedUuid, requireStableState: false, cancellationToken);
        var loaded = await LoadAsync(context.Path, cancellationToken);
        return ToDto(context, loaded);
    }

    public async Task<PlayerInventoryDto> SaveAsync(
        Guid serverId, string requestedUuid, SavePlayerInventoryRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ExpectedRevision) || request.Items is null)
            throw PanelProblems.Validation("An expected player-data revision and complete inventory are required.");
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        var context = await ResolveAsync(serverId, requestedUuid, requireStableState: true, cancellationToken);
        EnsureOffline(context);
        var loaded = await LoadAsync(context.Path, cancellationToken);
        EnsureRevision(loaded.Revision, request.ExpectedRevision);
        var desired = ValidateItems(request.Items, loaded);
        await WriteBackupAsync(serverId, context.Uuid, loaded, cancellationToken);
        ReplaceInventory(loaded.File.RootTag, desired, loaded);
        await AtomicSaveAsync(context.Path, loaded.File, cancellationToken);
        var updated = await LoadAsync(context.Path, cancellationToken);
        return ToDto(context with { SavedAt = File.GetLastWriteTimeUtc(context.Path) }, updated);
    }

    public async Task<IReadOnlyList<PlayerInventoryBackupDto>> ListBackupsAsync(
        Guid serverId, string requestedUuid, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(serverId, requestedUuid, requireStableState: false, cancellationToken);
        var directory = paths.PlayerInventoryBackups(serverId, context.Uuid);
        if (!Directory.Exists(directory)) return [];
        EnsureNoLinks(paths.Backups, directory);
        var result = new List<PlayerInventoryBackupDto>();
        foreach (var metadataPath in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<SnapshotMetadata>(await File.ReadAllTextAsync(metadataPath, cancellationToken), Json);
                if (metadata is null) continue;
                var dataPath = Path.Combine(directory, metadata.Id.ToString("N") + ".dat");
                if (File.Exists(dataPath)) result.Add(new(metadata.Id, metadata.CreatedAt, metadata.SourceRevision, new FileInfo(dataPath).Length));
            }
            catch { }
        }
        return result.OrderByDescending(x => x.CreatedAt).Take(20).ToList();
    }

    public async Task<PlayerInventoryBackupDto> CreateBackupAsync(
        Guid serverId, string requestedUuid, CreatePlayerInventoryBackupRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
            throw PanelProblems.Validation("An expected player-data revision is required.");
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        var context = await ResolveAsync(serverId, requestedUuid, requireStableState: true, cancellationToken);
        var loaded = await LoadAsync(context.Path, cancellationToken);
        EnsureRevision(loaded.Revision, request.ExpectedRevision);
        return await WriteBackupAsync(serverId, context.Uuid, loaded, cancellationToken);
    }

    public async Task<int> CreateScheduledBackupsAsync(
        Guid serverId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        var storage = await ResolveStorageAsync(serverId, requireStableState: true, cancellationToken);
        var savedPlayers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in PlayerDataDirectories(storage.Instance, storage.World))
        {
            EnsureNoLinks(storage.Instance, directory);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.dat", SearchOption.TopDirectoryOnly))
            {
                EnsureNoLinks(storage.Instance, path);
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
                var uuid = id.ToString("D").ToLowerInvariant();
                if (!savedPlayers.TryGetValue(uuid, out var existing) || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(existing))
                    savedPlayers[uuid] = path;
            }
        }
        if (savedPlayers.Count == 0)
            throw new PanelException(404, "PLAYER_DATA_NOT_FOUND", "No saved player inventories are available to back up.");
        foreach (var (uuid, path) in savedPlayers)
            await WriteBackupAsync(serverId, uuid, await LoadAsync(path, cancellationToken), cancellationToken);
        return savedPlayers.Count;
    }

    public async Task<PlayerInventoryDto> RestoreAsync(
        Guid serverId, string requestedUuid, Guid backupId, RestorePlayerInventoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
            throw PanelProblems.Validation("An expected player-data revision is required.");
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        var context = await ResolveAsync(serverId, requestedUuid, requireStableState: true, cancellationToken);
        EnsureOffline(context);
        var loaded = await LoadAsync(context.Path, cancellationToken);
        EnsureRevision(loaded.Revision, request.ExpectedRevision);
        var backupDirectory = paths.PlayerInventoryBackups(serverId, context.Uuid);
        var backupPath = Path.Combine(backupDirectory, backupId.ToString("N") + ".dat");
        if (!File.Exists(backupPath)) throw PanelProblems.NotFound("Inventory backup");
        EnsureNoLinks(paths.ServerBackups(serverId), backupPath);
        var snapshot = await LoadAsync(backupPath, cancellationToken);
        var inventory = RequiredList(snapshot.File.RootTag, "Inventory");
        var ender = RequiredList(snapshot.File.RootTag, "EnderItems");
        ValidateList(inventory, "Inventory"); ValidateList(ender, "EnderItems");
        await WriteBackupAsync(serverId, context.Uuid, loaded, cancellationToken);
        Set(loaded.File.RootTag, new NbtList(inventory));
        Set(loaded.File.RootTag, new NbtList(ender));
        await AtomicSaveAsync(context.Path, loaded.File, cancellationToken);
        return ToDto(context with { SavedAt = File.GetLastWriteTimeUtc(context.Path) }, await LoadAsync(context.Path, cancellationToken));
    }

    private async Task<PlayerContext> ResolveAsync(
        Guid serverId, string requestedUuid, bool requireStableState, CancellationToken cancellationToken)
    {
        var uuid = NormalizeUuid(requestedUuid);
        var storage = await ResolveStorageAsync(serverId, requireStableState, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var observed = await db.Players.AsNoTracking().Where(x => x.ServerId == serverId).ToListAsync(cancellationToken);
        var player = observed.FirstOrDefault(x => x.Uuid is not null && UuidEquals(x.Uuid, uuid));
        var path = FindSavedPlayerDataPath(storage.Instance, storage.World, uuid);
        foreach (var candidate in CandidatePlayerDataPaths(storage.Instance, storage.World, uuid))
            EnsureNoLinks(storage.Instance, candidate);
        if (path is null) throw new PanelException(404, "PLAYER_DATA_NOT_FOUND", "No saved player data exists for this UUID.");
        return new PlayerContext(serverId, uuid, player?.Name ?? uuid, path, player?.Online == true, File.GetLastWriteTimeUtc(path));
    }

    private async Task<PlayerStorageContext> ResolveStorageAsync(
        Guid serverId, bool requireStableState, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
            ?? throw PanelProblems.NotFound("Server");
        if (requireStableState)
        {
            var running = supervisor.IsRunning(serverId);
            if (server.State is not (ServerState.Running or ServerState.Stopped) ||
                server.State == ServerState.Running != running || server.State == ServerState.Stopped && running)
                throw PanelProblems.Conflict("SERVER_BUSY", "Player inventory cannot be changed in the server's current state.");
        }
        var propertiesPath = Path.Combine(paths.Instance(serverId), "server.properties");
        var document = File.Exists(propertiesPath)
            ? PropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken)) : PropertiesDocument.Empty();
        var world = document.Get("level-name") ?? "world";
        if (world is "." or ".." || world.Contains(Path.DirectorySeparatorChar) || world.Contains(Path.AltDirectorySeparatorChar) ||
            world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw InvalidData("The configured level-name is not a safe player-data path.");
        var instance = Path.GetFullPath(paths.Instance(serverId));
        return new PlayerStorageContext(instance, world);
    }

    internal static string? FindSavedPlayerDataPath(string instance, string world, string uuid) =>
        CandidatePlayerDataPaths(instance, world, uuid)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static IReadOnlyList<string> CandidatePlayerDataPaths(string instance, string world, string uuid) =>
        PlayerDataDirectories(instance, world).Select(directory => Path.Combine(directory, uuid + ".dat")).ToList();

    private static IReadOnlyList<string> PlayerDataDirectories(string instance, string world) =>
    [
        Path.GetFullPath(Path.Combine(instance, world, "playerdata")),
        Path.GetFullPath(Path.Combine(instance, world, "players", "data"))
    ];

    private static async Task<LoadedPlayerData> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 2 or > MaximumCompressedBytes) throw InvalidData("The compressed player-data file is outside the allowed size.");
            var compressed = await File.ReadAllBytesAsync(path, cancellationToken);
            if (compressed[0] != 0x1f || compressed[1] != 0x8b) throw InvalidData("Only gzip-compressed player data is supported.");
            var revision = Convert.ToHexString(SHA256.HashData(compressed)).ToLowerInvariant();
            await using var source = new MemoryStream(compressed, writable: false);
            await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: false);
            await using var uncompressed = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (uncompressed.Length + read > MaximumDecompressedBytes) throw InvalidData("The decompressed player-data file is outside the allowed size.");
                await uncompressed.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var bytes = uncompressed.ToArray();
            var file = new NbtFile();
            file.LoadFromBuffer(bytes, 0, bytes.Length, NbtCompression.None);
            var inventory = RequiredList(file.RootTag, "Inventory");
            var ender = file.RootTag.Get("EnderItems") switch
            {
                null => new NbtList("EnderItems", NbtTagType.Compound),
                NbtList list => list,
                _ => throw InvalidData("EnderItems must be an NBT list.")
            };
            ValidateList(inventory, "Inventory"); ValidateList(ender, "EnderItems");
            var occupied = ParseOccupied(inventory, ender);
            var dataVersion = file.RootTag.Get("DataVersion") is NbtInt version ? (int?)version.Value : null;
            return new LoadedPlayerData(file, compressed, revision, occupied, dataVersion);
        }
        catch (PanelException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NbtFormatException or EndOfStreamException or InvalidCastException)
        {
            throw InvalidData(exception.Message);
        }
    }

    private static PlayerInventoryDto ToDto(PlayerContext context, LoadedPlayerData loaded)
    {
        var slots = Definitions.Select(definition => new InventorySlotDto(
            definition.Section, definition.Index, definition.NbtSlot,
            loaded.Occupied.TryGetValue((definition.Section, definition.Index), out var stack) ? Describe(stack) : null)).ToList();
        return new PlayerInventoryDto(context.Name, context.Uuid, loaded.Revision, context.SavedAt,
            context.Online, context.Online, !context.Online, loaded.DataVersion, slots);
    }

    private static InventoryItemDto Describe(NbtCompound stack)
    {
        var id = ReadId(stack);
        var count = ReadCount(stack);
        var metadata = stack.Tags
            .Where(x => x.Name is not ("id" or "Count" or "count" or "Slot"))
            .Select(x => x is NbtCompound compound ? $"{x.Name}: compound ({compound.Count})" :
                x is NbtList list ? $"{x.Name}: list ({list.Count})" : $"{x.Name}: {x.TagType.ToString().ToLowerInvariant()}")
            .Take(8).ToList();
        var path = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
        var display = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(path.Replace('_', ' ').Replace('/', ' '));
        return new InventoryItemDto(id, count, display, metadata);
    }

    private static IReadOnlyDictionary<(string Section, int Index), NbtCompound> ValidateItems(
        IReadOnlyList<InventoryItemUpdateDto> items, LoadedPlayerData loaded)
    {
        var result = new Dictionary<(string Section, int Index), NbtCompound>(SlotKeyComparer.Instance);
        var sources = new HashSet<(string Section, int Index)>(SlotKeyComparer.Instance);
        foreach (var item in items)
        {
            var targetKey = NormalizeUi(item.Section, item.Index);
            if (!ByUi.TryGetValue(targetKey, out var target)) throw PanelProblems.Validation("An inventory item targets an unknown slot.");
            if (!result.TryAdd(targetKey, null!)) throw PanelProblems.Validation("Inventory slots must be unique.");
            ValidateItem(item.Id, item.Count);
            NbtCompound stack;
            if (item.SourceSection is not null || item.SourceIndex is not null)
            {
                if (item.SourceSection is null || item.SourceIndex is null) throw PanelProblems.Validation("An original source requires both a section and index.");
                var sourceKey = NormalizeUi(item.SourceSection, item.SourceIndex.Value);
                if (!sources.Add(sourceKey)) throw PanelProblems.Validation("An original inventory source can only be used once.");
                if (!loaded.Occupied.TryGetValue(sourceKey, out var original)) throw PanelProblems.Validation("An original inventory source no longer contains an item.");
                stack = new NbtCompound(original);
            }
            else stack = NewStack(item.Id, item.Count, loaded.DataVersion);
            if (item.ClearMetadata) { stack.Remove("tag"); stack.Remove("components"); }
            SetId(stack, item.Id); SetCount(stack, item.Count, loaded.DataVersion); SetSlot(stack, target.NbtSlot);
            result[targetKey] = stack;
        }
        return result;
    }

    private static void ReplaceInventory(
        NbtCompound root, IReadOnlyDictionary<(string Section, int Index), NbtCompound> desired, LoadedPlayerData loaded)
    {
        var oldInventory = RequiredList(root, "Inventory");
        var oldEnder = root.Get("EnderItems") as NbtList ?? new NbtList("EnderItems", NbtTagType.Compound);
        var inventory = new NbtList("Inventory", NbtTagType.Compound);
        var ender = new NbtList("EnderItems", NbtTagType.Compound);
        foreach (var compound in oldInventory.OfType<NbtCompound>().Where(x => !TryDefinition("Inventory", x, out _))) inventory.Add(new NbtCompound(compound));
        foreach (var compound in oldEnder.OfType<NbtCompound>().Where(x => !TryDefinition("EnderItems", x, out _))) ender.Add(new NbtCompound(compound));
        foreach (var (key, stack) in desired)
        {
            var definition = ByUi[key];
            (definition.List == "Inventory" ? inventory : ender).Add(stack);
        }
        Set(root, inventory); Set(root, ender);
    }

    private async Task<PlayerInventoryBackupDto> WriteBackupAsync(
        Guid serverId, string uuid, LoadedPlayerData loaded, CancellationToken cancellationToken)
    {
        var directory = paths.PlayerInventoryBackups(serverId, uuid);
        EnsureNoLinks(paths.Backups, directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinks(paths.Backups, directory);
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        var root = new NbtCompound("");
        root.Add(new NbtList(RequiredList(loaded.File.RootTag, "Inventory")));
        root.Add(loaded.File.RootTag.Get("EnderItems") is NbtList ender ? new NbtList(ender) : new NbtList("EnderItems", NbtTagType.Compound));
        var bytes = new NbtFile(root).SaveToBuffer(NbtCompression.GZip);
        var dataPath = Path.Combine(directory, id.ToString("N") + ".dat");
        var metadataPath = Path.Combine(directory, id.ToString("N") + ".json");
        await WriteFlushedAsync(dataPath, bytes, cancellationToken);
        await GateReleaseService.AtomicJsonAsync(metadataPath, new SnapshotMetadata(id, created, loaded.Revision), cancellationToken);
        var old = Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try { return (Path: path, Value: JsonSerializer.Deserialize<SnapshotMetadata>(File.ReadAllText(path), Json)); }
            catch { return (Path: path, Value: null); }
        }).Where(x => x.Value is not null).OrderByDescending(x => x.Value!.CreatedAt).Skip(20).ToList();
        foreach (var entry in old)
        {
            File.Delete(entry.Path);
            var oldData = Path.Combine(directory, entry.Value!.Id.ToString("N") + ".dat");
            if (File.Exists(oldData)) File.Delete(oldData);
        }
        return new PlayerInventoryBackupDto(id, created, loaded.Revision, bytes.LongLength);
    }

    private static async Task AtomicSaveAsync(string destination, NbtFile file, CancellationToken cancellationToken)
    {
        var bytes = file.SaveToBuffer(NbtCompression.GZip);
        if (bytes.Length > MaximumCompressedBytes) throw InvalidData("The updated player-data file exceeds the allowed size.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteFlushedAsync(temporary, bytes, cancellationToken);
            _ = await LoadAsync(temporary, cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteFlushedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static Dictionary<(string Section, int Index), NbtCompound> ParseOccupied(NbtList inventory, NbtList ender)
    {
        var result = new Dictionary<(string Section, int Index), NbtCompound>(SlotKeyComparer.Instance);
        foreach (var (name, list) in new[] { ("Inventory", inventory), ("EnderItems", ender) })
        foreach (var compound in list.OfType<NbtCompound>())
        {
            _ = ReadId(compound); _ = ReadCount(compound);
            if (!TryDefinition(name, compound, out var definition)) continue;
            if (!result.TryAdd((definition.Section, definition.Index), compound)) throw InvalidData("The player data contains duplicate inventory slots.");
        }
        return result;
    }

    private static bool TryDefinition(string list, NbtCompound compound, out SlotDefinition definition)
    {
        if (compound.Get("Slot") is not NbtByte slot) throw InvalidData("Every inventory stack must contain a byte Slot tag.");
        var signed = unchecked((sbyte)slot.Value);
        return ByNbt.TryGetValue((list, signed), out definition!);
    }

    private static string ReadId(NbtCompound stack) => stack.Get("id") switch
    {
        NbtString value when ItemIdRegex().IsMatch(value.Value) => value.Value,
        NbtShort value when value.Value >= 0 => value.Value.ToString(CultureInfo.InvariantCulture),
        NbtInt value when value.Value >= 0 => value.Value.ToString(CultureInfo.InvariantCulture),
        _ => throw InvalidData("An inventory item has an invalid id tag.")
    };

    private static int ReadCount(NbtCompound stack) => stack.Get("count") switch
    {
        NbtInt value when value.Value is >= 1 and <= 127 => value.Value,
        null => stack.Get("Count") switch
        {
            NbtByte value when value.Value is >= 1 and <= 127 => value.Value,
            _ => throw InvalidData("An inventory item has an invalid count tag.")
        },
        _ => throw InvalidData("An inventory item has an invalid count tag.")
    };

    private static NbtCompound NewStack(string id, int count, int? dataVersion)
    {
        var stack = new NbtCompound();
        SetId(stack, id); SetCount(stack, count, dataVersion);
        return stack;
    }

    private static void SetId(NbtCompound stack, string id)
    {
        stack.Remove("id");
        if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric <= short.MaxValue) stack.Add(new NbtShort("id", (short)numeric));
            else stack.Add(new NbtInt("id", numeric));
        }
        else stack.Add(new NbtString("id", id));
    }

    private static void SetCount(NbtCompound stack, int count, int? dataVersion)
    {
        var modern = stack.Get("count") is NbtInt || stack.Get("Count") is null && dataVersion >= 3837;
        stack.Remove("Count"); stack.Remove("count");
        if (modern) stack.Add(new NbtInt("count", count));
        else stack.Add(new NbtByte("Count", (byte)count));
    }

    private static void SetSlot(NbtCompound stack, int nbtSlot)
    {
        stack.Remove("Slot"); stack.Add(new NbtByte("Slot", unchecked((byte)(sbyte)nbtSlot)));
    }

    private static void ValidateItem(string id, int count)
    {
        if (count is < 1 or > 127) throw PanelProblems.Validation("Item counts must be between 1 and 127.");
        if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric >= 0) return;
        if (!ItemIdRegex().IsMatch(id)) throw PanelProblems.Validation("Item IDs must be legacy numeric IDs or lowercase namespaced Minecraft IDs.");
    }

    private static NbtList RequiredList(NbtCompound root, string name) => root.Get(name) switch
    {
        NbtList list => list,
        null when name is "Inventory" or "EnderItems" => new NbtList(name, NbtTagType.Compound),
        _ => throw InvalidData($"{name} must be an NBT list.")
    };

    private static void ValidateList(NbtList list, string name)
    {
        if (list.Count == 0) return;
        if (list.ListType != NbtTagType.Compound)
            throw InvalidData($"{name} must contain compound item tags.");
        if (list.Any(x => x is not NbtCompound)) throw InvalidData($"{name} contains an invalid item tag.");
    }

    private static void Set(NbtCompound root, NbtTag tag) { root.Remove(tag.Name!); root.Add(tag); }
    private static (string Section, int Index) NormalizeUi(string section, int index) => (section.Trim().ToLowerInvariant(), index);
    private static void EnsureRevision(string actual, string expected)
    {
        var normalized = expected.Trim().ToLowerInvariant();
        if (actual.Length != normalized.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(normalized)))
            throw new PanelException(409, "PLAYER_DATA_CHANGED", "The saved player data changed after it was loaded. Refresh or rebase the staged inventory edits.");
    }
    private static void EnsureOffline(PlayerContext context)
    {
        if (context.Online) throw new PanelException(409, "PLAYER_ONLINE", "The player must be offline before inventory changes can be saved.");
    }

    private static string NormalizeUuid(string value)
    {
        if (!Guid.TryParse(value, out var uuid)) throw PanelProblems.Validation("Player UUID is invalid.");
        return uuid.ToString("D").ToLowerInvariant();
    }
    private static bool UuidEquals(string left, string right) =>
        left.Replace("-", "", StringComparison.Ordinal).Equals(right.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoLinks(string root, string destination)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDestination = Path.GetFullPath(destination);
        if (!fullDestination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw InvalidData("The player-data path escapes its managed root.");
        if (Directory.Exists(fullRoot) && new DirectoryInfo(fullRoot).LinkTarget is not null)
            throw InvalidData("Symlinked player-data paths are not allowed.");
        var relative = Path.GetRelativePath(fullRoot, fullDestination);
        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw InvalidData("Symlinked player-data paths are not allowed.");
        }
    }

    private static IReadOnlyList<SlotDefinition> BuildDefinitions()
    {
        var result = new List<SlotDefinition>();
        for (var i = 0; i < 9; i++) result.Add(new("hotbar", i, "Inventory", i));
        for (var i = 0; i < 27; i++) result.Add(new("storage", i, "Inventory", i + 9));
        result.Add(new("armor", 0, "Inventory", 103));
        result.Add(new("armor", 1, "Inventory", 102));
        result.Add(new("armor", 2, "Inventory", 101));
        result.Add(new("armor", 3, "Inventory", 100));
        result.Add(new("offhand", 0, "Inventory", -106));
        for (var i = 0; i < 27; i++) result.Add(new("ender", i, "EnderItems", i));
        return result;
    }

    private static PanelException InvalidData(string detail) => new(409, "PLAYER_DATA_INVALID", "The saved player data is not a supported, valid inventory file.", detail);
    private sealed record SlotDefinition(string Section, int Index, string List, int NbtSlot);
    private sealed record PlayerContext(Guid ServerId, string Uuid, string Name, string Path, bool Online, DateTimeOffset SavedAt);
    private sealed record PlayerStorageContext(string Instance, string World);
    private sealed record LoadedPlayerData(NbtFile File, byte[] Compressed, string Revision,
        IReadOnlyDictionary<(string Section, int Index), NbtCompound> Occupied, int? DataVersion);
    private sealed record SnapshotMetadata(Guid Id, DateTimeOffset CreatedAt, string SourceRevision);
    private sealed class SlotKeyComparer : IEqualityComparer<(string Section, int Index)>
    {
        public static readonly SlotKeyComparer Instance = new();
        public bool Equals((string Section, int Index) x, (string Section, int Index) y) =>
            x.Index == y.Index && x.Section.Equals(y.Section, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Section, int Index) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Section), value.Index);
    }

    [GeneratedRegex("^[a-z0-9_.-]+:[a-z0-9_./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdRegex();
}
