using System.IO.Compression;
using fNbt;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class PlayerInventoryServiceTests : IAsyncLifetime
{
    private const string Uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _serverId = Guid.NewGuid();
    private PanelPaths _paths = null!;
    private DbContextOptions<StateDbContext> _options = null!;
    private PlayerInventoryService _service = null!;

    public async Task InitializeAsync()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = Path.Combine(_root, "data"), ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
        _options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={Path.Combine(_root, "state.db")}").Options;
        await using (var db = new StateDbContext(_options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new ServerEntity { Id = _serverId, Name = "Test", Kind = ServerKind.Paper, Version = "1.21.8", JavaRuntimeId = "java", EulaAcceptedAt = DateTimeOffset.UtcNow, State = ServerState.Stopped });
            db.Players.Add(new PlayerEntity { ServerId = _serverId, Name = "Notch", Uuid = Uuid, Online = false });
            await db.SaveChangesAsync();
        }
        var instance = _paths.Instance(_serverId); Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "server.properties"), "level-name=world\n");
        var playerData = Path.Combine(instance, "world", "playerdata"); Directory.CreateDirectory(playerData);
        CreateFixture(Path.Combine(playerData, Uuid + ".dat"));
        _service = new PlayerInventoryService(_paths, new Factory(_options), new AsyncKeyedLock(), new StoppedStatus());
    }

    [Fact]
    public async Task Modern_inventory_and_ender_chest_are_mapped_and_metadata_is_preserved_when_moved()
    {
        var before = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        Assert.Equal(68, before.Slots.Count);
        var diamond = before.Slots.Single(x => x.Section == "hotbar" && x.Index == 0).Item!;
        Assert.Equal("minecraft:diamond_sword", diamond.Id);
        Assert.Contains(diamond.Metadata, value => value.StartsWith("components:", StringComparison.Ordinal));
        Assert.Equal("minecraft:ender_pearl", before.Slots.Single(x => x.Section == "ender" && x.Index == 3).Item!.Id);

        var saved = await _service.SaveAsync(_serverId, Uuid, new SavePlayerInventoryRequest(before.Revision,
        [
            new("storage", 0, "hotbar", 0, "minecraft:diamond_sword", 2),
            new("ender", 3, "ender", 3, "minecraft:ender_pearl", 16)
        ]), CancellationToken.None);

        Assert.Null(saved.Slots.Single(x => x.Section == "hotbar" && x.Index == 0).Item);
        Assert.Equal(2, saved.Slots.Single(x => x.Section == "storage" && x.Index == 0).Item!.Count);
        var file = new NbtFile(PlayerPath());
        var moved = Assert.IsType<NbtList>(file.RootTag.Get("Inventory")).OfType<NbtCompound>().Single();
        Assert.NotNull(Assert.IsType<NbtCompound>(moved.Get("components")).Get("minecraft:custom_data"));
        Assert.Equal(20f, Assert.IsType<NbtFloat>(file.RootTag.Get("Health")).Value);
        Assert.Single(await _service.ListBackupsAsync(_serverId, Uuid, CancellationToken.None));
    }

    [Fact]
    public async Task Save_rejects_online_players_without_modifying_the_file()
    {
        var before = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        await using (var db = new StateDbContext(_options))
        {
            (await db.Players.SingleAsync()).Online = true;
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<PanelException>(() => _service.SaveAsync(_serverId, Uuid,
            new SavePlayerInventoryRequest(before.Revision, []), CancellationToken.None));
        Assert.Equal("PLAYER_ONLINE", exception.Code);
        Assert.Equal(before.Revision, (await _service.GetAsync(_serverId, Uuid, CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task Online_inventory_can_be_viewed_and_backed_up_without_modifying_player_data()
    {
        await using (var db = new StateDbContext(_options))
        {
            (await db.Players.SingleAsync()).Online = true;
            await db.SaveChangesAsync();
        }

        var inventory = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        Assert.True(inventory.Online);
        Assert.True(inventory.SnapshotMayBeStale);
        Assert.False(inventory.WriteAllowed);

        var backup = await _service.CreateBackupAsync(_serverId, Uuid,
            new CreatePlayerInventoryBackupRequest(inventory.Revision), CancellationToken.None);

        Assert.Equal(inventory.Revision, backup.SourceRevision);
        Assert.Equal(inventory.Revision, (await _service.GetAsync(_serverId, Uuid, CancellationToken.None)).Revision);
        Assert.Equal(backup.Id, Assert.Single(await _service.ListBackupsAsync(_serverId, Uuid, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Scheduled_inventory_backup_captures_every_saved_player_while_online()
    {
        await using (var db = new StateDbContext(_options))
        {
            (await db.Players.SingleAsync()).Online = true;
            await db.SaveChangesAsync();
        }
        const string otherUuid = "16f4f71b-4f1f-4f95-8d43-2e553a52e1a1";
        var modernDirectory = Path.Combine(_paths.Instance(_serverId), "world", "players", "data");
        Directory.CreateDirectory(modernDirectory);
        CreateFixture(Path.Combine(modernDirectory, otherUuid + ".dat"));

        var count = await _service.CreateScheduledBackupsAsync(_serverId, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Single(await _service.ListBackupsAsync(_serverId, Uuid, CancellationToken.None));
        Assert.Single(await _service.ListBackupsAsync(_serverId, otherUuid, CancellationToken.None));
    }

    [Fact]
    public async Task Minecraft_26_player_storage_layout_is_supported()
    {
        var modernDirectory = Path.Combine(_paths.Instance(_serverId), "world", "players", "data");
        Directory.CreateDirectory(modernDirectory);
        File.Move(PlayerPath(), Path.Combine(modernDirectory, Uuid + ".dat"));

        var inventory = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        Assert.Equal("minecraft:diamond_sword", inventory.Slots.Single(x => x.Section == "hotbar" && x.Index == 0).Item!.Id);
    }

    [Theory]
    [InlineData("Inventory")]
    [InlineData("EnderItems")]
    public async Task Empty_lists_are_valid_regardless_of_their_serialized_element_marker(string listName)
    {
        var root = new NbtCompound("");
        root.Add(new NbtInt("DataVersion", 4440));
        root.Add(new NbtList("Inventory", listName == "Inventory" ? NbtTagType.Byte : NbtTagType.Compound));
        root.Add(new NbtList("EnderItems", listName == "EnderItems" ? NbtTagType.Byte : NbtTagType.Compound));
        new NbtFile(root).SaveToFile(PlayerPath(), NbtCompression.GZip);

        var inventory = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        Assert.All(inventory.Slots, slot => Assert.Null(slot.Item));
    }

    [Theory]
    [InlineData("Inventory")]
    [InlineData("EnderItems")]
    public async Task Missing_empty_inventory_lists_are_treated_as_empty(string missingList)
    {
        var root = new NbtCompound("");
        root.Add(new NbtInt("DataVersion", 4440));
        if (missingList != "Inventory") root.Add(new NbtList("Inventory", NbtTagType.Compound));
        if (missingList != "EnderItems") root.Add(new NbtList("EnderItems", NbtTagType.Compound));
        new NbtFile(root).SaveToFile(PlayerPath(), NbtCompression.GZip);

        var inventory = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        Assert.All(inventory.Slots, slot => Assert.Null(slot.Item));
    }

    [Fact]
    public async Task Armor_slots_are_mapped_from_their_signed_inventory_slots()
    {
        var file = new NbtFile(PlayerPath());
        var inventory = Assert.IsType<NbtList>(file.RootTag.Get("Inventory"));
        var helmet = new NbtCompound();
        helmet.Add(new NbtByte("Slot", 103));
        helmet.Add(new NbtString("id", "minecraft:diamond_helmet"));
        helmet.Add(new NbtInt("count", 1));
        inventory.Add(helmet);
        file.SaveToFile(PlayerPath(), NbtCompression.GZip);

        var loaded = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        Assert.Equal("minecraft:diamond_helmet", loaded.Slots.Single(x => x.Section == "armor" && x.Index == 0).Item!.Id);
    }

    [Fact]
    public async Task Revision_conflicts_are_rejected_before_snapshot_or_write()
    {
        var exception = await Assert.ThrowsAsync<PanelException>(() => _service.SaveAsync(_serverId, Uuid,
            new SavePlayerInventoryRequest(new string('0', 64), []), CancellationToken.None));
        Assert.Equal("PLAYER_DATA_CHANGED", exception.Code);
        Assert.Empty(await _service.ListBackupsAsync(_serverId, Uuid, CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_numeric_items_and_signed_offhand_slot_are_supported()
    {
        CreateLegacyFixture(PlayerPath());

        var inventory = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        var item = inventory.Slots.Single(x => x.Section == "offhand" && x.Index == 0).Item!;

        Assert.Equal("276", item.Id);
        Assert.Equal(1, item.Count);
        Assert.Contains(item.Metadata, value => value.StartsWith("tag:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clear_metadata_removes_components_when_an_existing_item_id_changes()
    {
        var before = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        await _service.SaveAsync(_serverId, Uuid, new SavePlayerInventoryRequest(before.Revision,
        [
            new("hotbar", 0, "hotbar", 0, "minecraft:stick", 1, ClearMetadata: true),
            new("ender", 3, "ender", 3, "minecraft:ender_pearl", 16)
        ]), CancellationToken.None);

        var stack = Assert.IsType<NbtList>(new NbtFile(PlayerPath()).RootTag.Get("Inventory")).OfType<NbtCompound>().Single();
        Assert.Equal("minecraft:stick", Assert.IsType<NbtString>(stack.Get("id")).Value);
        Assert.Null(stack.Get("components"));
    }

    [Fact]
    public async Task Snapshot_restore_changes_only_inventory_tags()
    {
        var before = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        var changed = await _service.SaveAsync(_serverId, Uuid, new SavePlayerInventoryRequest(before.Revision,
        [new("storage", 0, "hotbar", 0, "minecraft:diamond_sword", 2)]), CancellationToken.None);
        var backup = Assert.Single(await _service.ListBackupsAsync(_serverId, Uuid, CancellationToken.None));
        var file = new NbtFile(PlayerPath());
        Assert.IsType<NbtFloat>(file.RootTag.Get("Health")).Value = 7f;
        file.SaveToFile(PlayerPath(), NbtCompression.GZip);
        var current = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);

        var restored = await _service.RestoreAsync(_serverId, Uuid, backup.Id,
            new RestorePlayerInventoryRequest(current.Revision), CancellationToken.None);

        Assert.Equal("minecraft:diamond_sword", restored.Slots.Single(x => x.Section == "hotbar" && x.Index == 0).Item!.Id);
        Assert.Null(restored.Slots.Single(x => x.Section == "storage" && x.Index == 0).Item);
        Assert.Equal(7f, Assert.IsType<NbtFloat>(new NbtFile(PlayerPath()).RootTag.Get("Health")).Value);
        Assert.NotEqual(changed.Revision, restored.Revision);
    }

    [Fact]
    public async Task Snapshot_restore_rejects_online_players()
    {
        var current = await _service.GetAsync(_serverId, Uuid, CancellationToken.None);
        var backup = await _service.CreateBackupAsync(_serverId, Uuid,
            new CreatePlayerInventoryBackupRequest(current.Revision), CancellationToken.None);
        await using (var db = new StateDbContext(_options))
        {
            (await db.Players.SingleAsync()).Online = true;
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<PanelException>(() => _service.RestoreAsync(
            _serverId, Uuid, backup.Id, new RestorePlayerInventoryRequest(current.Revision), CancellationToken.None));

        Assert.Equal("PLAYER_ONLINE", exception.Code);
    }

    [Fact]
    public async Task Symlinked_playerdata_paths_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var world = Path.Combine(_paths.Instance(_serverId), "world");
        var outside = Path.Combine(_root, "outside-world");
        Directory.Move(world, outside);
        Directory.CreateSymbolicLink(world, outside);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            _service.GetAsync(_serverId, Uuid, CancellationToken.None));

        Assert.Equal("PLAYER_DATA_INVALID", exception.Code);
    }

    [Fact]
    public async Task Gzip_bombs_are_rejected_at_the_decompressed_limit()
    {
        await using (var target = File.Create(PlayerPath()))
        await using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize))
            await gzip.WriteAsync(new byte[33 * 1024 * 1024]);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            _service.GetAsync(_serverId, Uuid, CancellationToken.None));

        Assert.Equal("PLAYER_DATA_INVALID", exception.Code);
        Assert.Contains("decompressed", exception.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private string PlayerPath() => Path.Combine(_paths.Instance(_serverId), "world", "playerdata", Uuid + ".dat");

    private static void CreateFixture(string path)
    {
        var inventory = new NbtList("Inventory", NbtTagType.Compound);
        var sword = new NbtCompound();
        sword.Add(new NbtByte("Slot", 0)); sword.Add(new NbtString("id", "minecraft:diamond_sword")); sword.Add(new NbtInt("count", 1));
        var components = new NbtCompound("components"); components.Add(new NbtCompound("minecraft:custom_data") { new NbtInt("kept", 42) }); sword.Add(components);
        inventory.Add(sword);
        var ender = new NbtList("EnderItems", NbtTagType.Compound);
        var pearl = new NbtCompound(); pearl.Add(new NbtByte("Slot", 3)); pearl.Add(new NbtString("id", "minecraft:ender_pearl")); pearl.Add(new NbtInt("count", 16)); ender.Add(pearl);
        var root = new NbtCompound(""); root.Add(new NbtInt("DataVersion", 3953)); root.Add(new NbtFloat("Health", 20)); root.Add(inventory); root.Add(ender);
        new NbtFile(root).SaveToFile(path, NbtCompression.GZip);
    }

    private static void CreateLegacyFixture(string path)
    {
        var inventory = new NbtList("Inventory", NbtTagType.Compound);
        var sword = new NbtCompound();
        sword.Add(new NbtByte("Slot", 150));
        sword.Add(new NbtShort("id", 276));
        sword.Add(new NbtByte("Count", 1));
        sword.Add(new NbtCompound("tag") { new NbtInt("Damage", 4) });
        inventory.Add(sword);
        var root = new NbtCompound("");
        root.Add(new NbtInt("DataVersion", 1343));
        root.Add(inventory);
        root.Add(new NbtList("EnderItems", NbtTagType.Compound));
        new NbtFile(root).SaveToFile(path, NbtCompression.GZip);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private sealed class Factory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StateDbContext(options));
    }
    private sealed class StoppedStatus : IServerProcessStatus { public bool IsRunning(Guid serverId) => false; }
}
