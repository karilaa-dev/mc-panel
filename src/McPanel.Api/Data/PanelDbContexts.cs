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
