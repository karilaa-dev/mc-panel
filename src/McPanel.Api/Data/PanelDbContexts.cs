using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace McPanel.Api.Data;

public sealed class StateDbContext(DbContextOptions<StateDbContext> options) : DbContext(options)
{
    public DbSet<AdminEntity> Admins => Set<AdminEntity>();
    public DbSet<ServerEntity> Servers => Set<ServerEntity>();
    public DbSet<JavaRuntimeEntity> JavaRuntimes => Set<JavaRuntimeEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<BackupEntity> Backups => Set<BackupEntity>();
    public DbSet<ScheduleEntity> Schedules => Set<ScheduleEntity>();
    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<ProxySettingsEntity> ProxySettings => Set<ProxySettingsEntity>();
    public DbSet<PanelSettingsEntity> PanelSettings => Set<PanelSettingsEntity>();
    public DbSet<GateSettingsEntity> GateSettings => Set<GateSettingsEntity>();
    public DbSet<GateBackendEntity> GateBackends => Set<GateBackendEntity>();
    public DbSet<GateExternalBackendEntity> GateExternalBackends => Set<GateExternalBackendEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<ServerEntity>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<ServerEntity>().Property(x => x.State).HasConversion<string>();
        modelBuilder.Entity<ServerEntity>().Property(x => x.LaunchMode).HasConversion<string>();
        modelBuilder.Entity<ProxySettingsEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<ProxySettingsEntity>().Property(x => x.Mode).HasConversion<string>();
        modelBuilder.Entity<ProxySettingsEntity>().Property(x => x.ClassicForwardingMode).HasConversion<string>();
        modelBuilder.Entity<PanelSettingsEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<GateSettingsEntity>().HasKey(x => x.ServerId);
        modelBuilder.Entity<GateSettingsEntity>().Property(x => x.Mode).HasConversion<string>();
        modelBuilder.Entity<GateSettingsEntity>().Property(x => x.ClassicForwardingMode).HasConversion<string>();
        modelBuilder.Entity<GateBackendEntity>().HasKey(x => new { x.GateServerId, x.BackendServerId });
        modelBuilder.Entity<GateBackendEntity>().HasIndex(x => x.BackendServerId);
        modelBuilder.Entity<GateExternalBackendEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<GateExternalBackendEntity>().HasIndex(x => new { x.GateServerId, x.Host, x.Port }).IsUnique();
        modelBuilder.Entity<ServerEntity>().Property(x => x.PublicHost).UseCollation("NOCASE");
        modelBuilder.Entity<JobEntity>().Property(x => x.State).HasConversion<string>();
        modelBuilder.Entity<JobEntity>().HasIndex(x => x.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        modelBuilder.Entity<PlayerEntity>().HasIndex(x => new { x.ServerId, x.Name }).IsUnique();
        modelBuilder.Entity<ScheduleEntity>().HasIndex(x => new { x.Enabled, x.NextRunAt });
        modelBuilder.Entity<BackupEntity>().HasIndex(x => new { x.ServerId, x.CreatedAt });
        ConfigureUtcTimestamps(modelBuilder);
    }

    public async Task EnsureCompatibleSchemaAsync(CancellationToken cancellationToken = default)
    {
        var connection = Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            var adminColumns = await ColumnsAsync(connection, "Admins", cancellationToken);
            var serverColumns = await ColumnsAsync(connection, "Servers", cancellationToken);
            var backupColumns = await ColumnsAsync(connection, "Backups", cancellationToken);
            var addingLaunchTarget = serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.LaunchTarget));
            var addingAdvertisedPort = serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.PublicPort));

            await using (var createProxy = connection.CreateCommand())
            {
                createProxy.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ProxySettings" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ProxySettings" PRIMARY KEY,
                        "Mode" TEXT NOT NULL DEFAULT 'Lite',
                        "GlobalPublicHost" TEXT NULL,
                        "PublicPort" INTEGER NOT NULL DEFAULT 25565,
                        "DefaultServerId" TEXT NULL,
                        "ClassicForwardingMode" TEXT NOT NULL DEFAULT 'Velocity',
                        "BackendSetupAcknowledgementHash" TEXT NULL,
                        "ApiPort" INTEGER NOT NULL DEFAULT 0,
                        "Revision" TEXT NOT NULL DEFAULT '',
                        "UpdatedAt" INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                await createProxy.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createManagedGate = connection.CreateCommand())
            {
                createManagedGate.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PanelSettings" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_PanelSettings" PRIMARY KEY,
                        "KeepServersRunningOnPanelStop" INTEGER NOT NULL DEFAULT 1,
                        "GlobalServerHost" TEXT NULL,
                        "Revision" TEXT NOT NULL DEFAULT '',
                        "UpdatedAt" INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS "GateSettings" (
                        "ServerId" TEXT NOT NULL CONSTRAINT "PK_GateSettings" PRIMARY KEY,
                        "Mode" TEXT NOT NULL DEFAULT 'Lite',
                        "DefaultBackendServerId" TEXT NULL,
                        "DefaultExternalBackendId" TEXT NULL,
                        "ClassicForwardingMode" TEXT NOT NULL DEFAULT 'Velocity',
                        "ClassicConfigJson" TEXT NULL,
                        "BackendSetupAcknowledgementHash" TEXT NULL,
                        "ApiPort" INTEGER NOT NULL DEFAULT 0,
                        "Revision" TEXT NOT NULL DEFAULT '',
                        "ConfigurationDirty" INTEGER NOT NULL DEFAULT 1,
                        "LastApplyError" TEXT NULL,
                        "UpdatedAt" INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS "GateBackends" (
                        "GateServerId" TEXT NOT NULL,
                        "BackendServerId" TEXT NOT NULL,
                        CONSTRAINT "PK_GateBackends" PRIMARY KEY ("GateServerId", "BackendServerId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_GateBackends_BackendServerId" ON "GateBackends" ("BackendServerId");
                    CREATE TABLE IF NOT EXISTS "GateExternalBackends" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_GateExternalBackends" PRIMARY KEY,
                        "GateServerId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Host" TEXT NOT NULL,
                        "Port" INTEGER NOT NULL DEFAULT 25565
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_GateExternalBackends_GateServerId_Host_Port"
                        ON "GateExternalBackends" ("GateServerId", "Host", "Port");
                    """;
                await createManagedGate.ExecuteNonQueryAsync(cancellationToken);
            }

            var gateSettingsColumns = await ColumnsAsync(connection, "GateSettings", cancellationToken);
            if (gateSettingsColumns.Count > 0 && !gateSettingsColumns.Contains(nameof(GateSettingsEntity.DefaultExternalBackendId)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"GateSettings\" ADD COLUMN \"DefaultExternalBackendId\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (gateSettingsColumns.Count > 0 && !gateSettingsColumns.Contains(nameof(GateSettingsEntity.ClassicConfigJson)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"GateSettings\" ADD COLUMN \"ClassicConfigJson\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.PublicHost)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"PublicHost\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0)
            {
                await using var index = connection.CreateCommand();
                index.CommandText = "DROP INDEX IF EXISTS \"IX_Servers_PublicHost\";";
                await index.ExecuteNonQueryAsync(cancellationToken);
            }

            if (addingAdvertisedPort)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"PublicPort\" INTEGER NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.AddressRevision)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"AddressRevision\" TEXT NOT NULL DEFAULT '';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var seedProxy = connection.CreateCommand())
            {
                seedProxy.CommandText = """
                    INSERT OR IGNORE INTO "ProxySettings" ("Id", "Mode", "PublicPort", "ClassicForwardingMode", "ApiPort", "Revision", "UpdatedAt")
                    VALUES (1, 'Lite', 25565, 'Velocity', 0, lower(hex(randomblob(16))), 0);
                    UPDATE "ProxySettings" SET "Revision" = lower(hex(randomblob(16))) WHERE "Id" = 1 AND length(trim("Revision")) = 0;
                    """;
                await seedProxy.ExecuteNonQueryAsync(cancellationToken);
            }

            if (addingAdvertisedPort)
            {
                // A legacy per-server public hostname was displayed through the singleton
                // proxy's shared public port. Materialize that effective port so the new
                // advertised-address model preserves exactly what administrators copied.
                await using var migrateAdvertisedPorts = connection.CreateCommand();
                migrateAdvertisedPorts.CommandText = """
                    UPDATE "Servers"
                    SET "PublicPort" = COALESCE((SELECT "PublicPort" FROM "ProxySettings" WHERE "Id" = 1), 25565)
                    WHERE "PublicHost" IS NOT NULL AND length(trim("PublicHost")) > 0;
                    """;
                await migrateAdvertisedPorts.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!adminColumns.Contains(nameof(AdminEntity.SessionStamp)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Admins\" ADD COLUMN \"SessionStamp\" TEXT NOT NULL DEFAULT '';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (adminColumns.Count > 0 && !adminColumns.Contains(nameof(AdminEntity.KeepServersRunningOnPanelStop)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Admins\" ADD COLUMN \"KeepServersRunningOnPanelStop\" INTEGER NOT NULL DEFAULT 1;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (adminColumns.Count > 0 && !adminColumns.Contains(nameof(AdminEntity.LastConsoleSequence)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Admins\" ADD COLUMN \"LastConsoleSequence\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var seedSettings = connection.CreateCommand())
            {
                seedSettings.CommandText = """
                    INSERT OR IGNORE INTO "PanelSettings" ("Id", "KeepServersRunningOnPanelStop", "GlobalServerHost", "Revision", "UpdatedAt")
                    SELECT 1,
                           COALESCE((SELECT "KeepServersRunningOnPanelStop" FROM "Admins" LIMIT 1), 1),
                           (SELECT "GlobalPublicHost" FROM "ProxySettings" WHERE "Id" = 1),
                           lower(hex(randomblob(16))), 0;
                    UPDATE "PanelSettings" SET "Revision" = lower(hex(randomblob(16))) WHERE length(trim("Revision")) = 0;
                    """;
                await seedSettings.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.InitialMemoryMb)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"InitialMemoryMb\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.MemoryLimitMb)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"MemoryLimitMb\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.UseAikarFlags)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"UseAikarFlags\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.IconRevision)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"IconRevision\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var column in new[]
            {
                nameof(ServerEntity.ModpackName), nameof(ServerEntity.ModpackVersion),
                nameof(ServerEntity.ModrinthProjectId), nameof(ServerEntity.ModrinthVersionId),
                nameof(ServerEntity.ModpackSource)
            })
            {
                if (serverColumns.Count == 0 || serverColumns.Contains(column)) continue;
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"Servers\" ADD COLUMN \"{column}\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.LoaderVersion)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"LoaderVersion\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.InstallerVersion)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"InstallerVersion\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.LaunchMode)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"LaunchMode\" TEXT NOT NULL DEFAULT 'Jar';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (addingLaunchTarget)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"LaunchTarget\" TEXT NOT NULL DEFAULT 'server.jar';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var initialize = connection.CreateCommand();
            initialize.CommandText = "UPDATE \"Admins\" SET \"SessionStamp\" = lower(hex(randomblob(16))) WHERE \"SessionStamp\" IS NULL OR length(trim(\"SessionStamp\")) = 0;";
            await initialize.ExecuteNonQueryAsync(cancellationToken);

            if (serverColumns.Count > 0)
            {
                await using var initializeServers = connection.CreateCommand();
                initializeServers.CommandText = "UPDATE \"Servers\" SET \"InitialMemoryMb\" = \"MemoryMb\" WHERE \"InitialMemoryMb\" IS NULL OR \"InitialMemoryMb\" <= 0;";
                await initializeServers.ExecuteNonQueryAsync(cancellationToken);
                initializeServers.CommandText = "UPDATE \"Servers\" SET \"MemoryLimitMb\" = \"MemoryMb\" + MAX(512, ((\"MemoryMb\" + 2047) / 2048) * 512) WHERE \"MemoryLimitMb\" IS NULL OR \"MemoryLimitMb\" <= \"MemoryMb\";";
                await initializeServers.ExecuteNonQueryAsync(cancellationToken);
                initializeServers.CommandText = "UPDATE \"Servers\" SET \"AddressRevision\" = lower(hex(randomblob(16))) WHERE \"AddressRevision\" IS NULL OR length(trim(\"AddressRevision\")) = 0;";
                await initializeServers.ExecuteNonQueryAsync(cancellationToken);

                if (serverColumns.Contains("FabricLoaderVersion"))
                {
                    await using var migrateLoader = connection.CreateCommand();
                    migrateLoader.CommandText = "UPDATE \"Servers\" SET \"LoaderVersion\" = \"FabricLoaderVersion\" WHERE \"LoaderVersion\" IS NULL AND \"FabricLoaderVersion\" IS NOT NULL;";
                    await migrateLoader.ExecuteNonQueryAsync(cancellationToken);
                }
                if (serverColumns.Contains("FabricInstallerVersion"))
                {
                    await using var migrateInstaller = connection.CreateCommand();
                    migrateInstaller.CommandText = "UPDATE \"Servers\" SET \"InstallerVersion\" = \"FabricInstallerVersion\" WHERE \"InstallerVersion\" IS NULL AND \"FabricInstallerVersion\" IS NOT NULL;";
                    await migrateInstaller.ExecuteNonQueryAsync(cancellationToken);
                }
                if (serverColumns.Contains("ExecutableJar"))
                {
                    if (addingLaunchTarget)
                    {
                        await using var migrateTarget = connection.CreateCommand();
                        migrateTarget.CommandText = "UPDATE \"Servers\" SET \"LaunchTarget\" = \"ExecutableJar\" WHERE \"ExecutableJar\" IS NOT NULL AND length(trim(\"ExecutableJar\")) > 0;";
                        await migrateTarget.ExecuteNonQueryAsync(cancellationToken);
                    }

                    // ExecutableJar was required in the original schema. Leaving it behind
                    // makes SQLite reject inserts from the current model, which replaced it
                    // with LaunchMode and LaunchTarget.
                    await using var removeLegacyTarget = connection.CreateCommand();
                    removeLegacyTarget.CommandText = "ALTER TABLE \"Servers\" DROP COLUMN \"ExecutableJar\";";
                    await removeLegacyTarget.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var jobColumns = await ColumnsAsync(connection, "Jobs", cancellationToken);
            if (jobColumns.Count > 0 && !jobColumns.Contains(nameof(JobEntity.ClientRequestId)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Jobs\" ADD COLUMN \"ClientRequestId\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            if (jobColumns.Count > 0)
            {
                await using var index = connection.CreateCommand();
                index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Jobs_ClientRequestId\" ON \"Jobs\" (\"ClientRequestId\") WHERE \"ClientRequestId\" IS NOT NULL;";
                await index.ExecuteNonQueryAsync(cancellationToken);
            }
            if (backupColumns.Count > 0 && !backupColumns.Contains(nameof(BackupEntity.SoftwareMetadataJson)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Backups\" ADD COLUMN \"SoftwareMetadataJson\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    internal static void ConfigureUtcTimestamps(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(converter);
            else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableConverter);
        }
    }
}

public sealed class ConsoleDbContext(DbContextOptions<ConsoleDbContext> options) : DbContext(options)
{
    public DbSet<ConsoleLineEntity> Lines => Set<ConsoleLineEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConsoleLineEntity>().HasKey(x => x.Sequence);
        modelBuilder.Entity<ConsoleLineEntity>().Property(x => x.Sequence).ValueGeneratedOnAdd();
        modelBuilder.Entity<ConsoleLineEntity>().HasIndex(x => new { x.ServerId, x.Sequence });
        modelBuilder.Entity<ConsoleLineEntity>().HasIndex(x => x.Timestamp);
        StateDbContext.ConfigureUtcTimestamps(modelBuilder);
    }

    public Task<int> EnsureCompatibleSchemaAsync(CancellationToken cancellationToken = default) =>
        Database.ExecuteSqlRawAsync("UPDATE \"Lines\" SET \"ServerId\" = upper(\"ServerId\") WHERE \"ServerId\" <> upper(\"ServerId\");", cancellationToken);
}
