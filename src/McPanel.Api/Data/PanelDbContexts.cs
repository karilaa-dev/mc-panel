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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<ServerEntity>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<ServerEntity>().Property(x => x.State).HasConversion<string>();
        modelBuilder.Entity<JobEntity>().Property(x => x.State).HasConversion<string>();
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

            if (!adminColumns.Contains(nameof(AdminEntity.SessionStamp)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Admins\" ADD COLUMN \"SessionStamp\" TEXT NOT NULL DEFAULT '';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.InitialMemoryMb)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"InitialMemoryMb\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverColumns.Count > 0 && !serverColumns.Contains(nameof(ServerEntity.UseAikarFlags)))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Servers\" ADD COLUMN \"UseAikarFlags\" INTEGER NOT NULL DEFAULT 0;";
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
}
