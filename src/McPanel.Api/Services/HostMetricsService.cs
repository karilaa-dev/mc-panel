using System.Collections.Concurrent;
using System.Diagnostics;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class HostMetricsService(PanelPaths paths, IHubContext<PanelHub> hub, SessionAudience audience, IServiceProvider services, ILogger<HostMetricsService> logger) : BackgroundService
{
    private readonly ConcurrentQueue<HostSampleDto> _samples = new();
    private readonly object _gate = new();
    private ulong _lastTotal;
    private ulong _lastIdle;
    private HostStatusDto? _last;

    public HostStatusDto GetStatus()
    {
        lock (_gate) return _last ?? Sample();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HostStatusDto status;
            lock (_gate) { status = Sample(); _last = status; }
            try
            {
                using var scope = services.CreateScope();
                var query = scope.ServiceProvider.GetRequiredService<ServerQueryService>();
                var servers = await query.ListAsync(stoppingToken);
                await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("MetricsUpdated", new { host = status, servers }, stoppingToken), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogDebug(exception, "Could not publish metrics"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private HostStatusDto Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var (totalMemory, availableMemory) = ReadMemory();
        var usedMemory = Math.Max(0, totalMemory - availableMemory);
        var cpu = ReadCpu();
        long diskTotal = 0, diskFree = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(paths.Data)!);
            diskTotal = drive.TotalSize; diskFree = drive.AvailableFreeSpace;
        }
        catch { }
        var sample = new HostSampleDto(now, cpu, totalMemory == 0 ? 0 : usedMemory * 100d / totalMemory);
        _samples.Enqueue(sample);
        while (_samples.Count > 180) _samples.TryDequeue(out _);
        return new(cpu, usedMemory, totalMemory, Math.Max(0, diskTotal - diskFree), diskTotal, now, _samples.ToArray());
    }

    private double ReadCpu()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try
        {
            var values = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            var total = values.Aggregate(0UL, (sum, value) => sum + value);
            var totalDelta = total - _lastTotal; var idleDelta = idle - _lastIdle;
            _lastTotal = total; _lastIdle = idle;
            return totalDelta == 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
        }
        catch { return 0; }
    }

    public static (long Total, long Available) ReadMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadLines("/proc/meminfo").Select(line => line.Split(':', 2)).Where(x => x.Length == 2)
                    .ToDictionary(x => x[0], x => long.Parse(x[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]) * 1024, StringComparer.Ordinal);
                return (values.GetValueOrDefault("MemTotal"), values.GetValueOrDefault("MemAvailable", values.GetValueOrDefault("MemFree")));
            }
            catch { }
        }
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, Math.Max(0, info.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)));
    }
}

public sealed class ServerQueryService(
    IDbContextFactory<StateDbContext> stateFactory,
    ProcessSupervisor supervisor,
    PanelPaths paths,
    GateApiClient gateApi)
{
    public async Task<IReadOnlyList<ServerSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.Servers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var settings = await db.PanelSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken) ?? new PanelSettingsEntity();
        var online = await db.Players.Where(x => x.Online).GroupBy(x => x.ServerId).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var routed = (await db.GateBackends.AsNoTracking().Select(x => x.BackendServerId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var apiPorts = await db.GateSettings.AsNoTracking().ToDictionaryAsync(x => x.ServerId, x => x.ApiPort, cancellationToken);
        var gateConnections = await GateConnectionsAsync(servers, apiPorts, cancellationToken);
        return servers.Select(x => Map(x,
            x.Kind == ServerKind.Gate ? gateConnections.GetValueOrDefault(x.Id) : online.GetValueOrDefault(x.Id),
            settings, routed.Contains(x.Id))).ToList();
    }

    public async Task<ServerSummaryDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Infrastructure.PanelProblems.NotFound("Server");
        var settings = await db.PanelSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken) ?? new PanelSettingsEntity();
        var routed = await db.GateBackends.AsNoTracking().AnyAsync(x => x.BackendServerId == id, cancellationToken);
        var online = server.Kind == ServerKind.Gate
            ? await GateConnectionAsync(server, await db.GateSettings.AsNoTracking().Where(x => x.ServerId == id).Select(x => (int?)x.ApiPort).SingleOrDefaultAsync(cancellationToken), cancellationToken)
            : await db.Players.CountAsync(x => x.ServerId == id && x.Online, cancellationToken);
        return Map(server, online, settings, routed);
    }

    private async Task<Dictionary<Guid, int>> GateConnectionsAsync(
        IReadOnlyList<ServerEntity> servers, IReadOnlyDictionary<Guid, int> apiPorts, CancellationToken cancellationToken)
    {
        var tasks = servers.Where(x => x.Kind == ServerKind.Gate).Select(async server =>
            (server.Id, Count: await GateConnectionAsync(server, apiPorts.GetValueOrDefault(server.Id), cancellationToken)));
        return (await Task.WhenAll(tasks)).ToDictionary(x => x.Id, x => x.Count);
    }

    private async Task<int> GateConnectionAsync(ServerEntity server, int? apiPort, CancellationToken cancellationToken)
    {
        if (server.State != ServerState.Running || apiPort is not (>= 1024 and <= 65535)) return 0;
        try { return (await gateApi.StatusAsync(apiPort.Value, cancellationToken)).ActiveConnections; }
        catch { return 0; }
    }

    private ServerSummaryDto Map(ServerEntity server, int playerCount, PanelSettingsEntity settings, bool hasGateHostnameRoute)
    {
        var runtime = supervisor.GetMetrics(server.Id);
        var maxPlayers = 20;
        var properties = Path.Combine(paths.Instance(server.Id), "server.properties");
        try
        {
            if (File.Exists(properties))
            {
                var document = PropertiesDocument.Parse(File.ReadAllText(properties));
                if (int.TryParse(document.Get("max-players"), out var parsed)) maxPlayers = parsed;
            }
        }
        catch { }
        var connection = GateConfigurationService.ResolveAddress(server, settings.GlobalServerHost, hasGateHostnameRoute);
        return new(server.Id, server.Name, server.Kind, server.Version, server.State, server.Port, server.MemoryMb,
            server.MemoryMb, playerCount, maxPlayers, runtime.CpuPercent, runtime.MemoryUsedMb, runtime.MemoryPeakMb,
            runtime.SwapUsedMb, runtime.AnonymousMemoryMb, runtime.FileMemoryMb, runtime.KernelMemoryMb, runtime.SocketMemoryMb,
            runtime.MemoryEnforced, runtime.UptimeSeconds, server.RestartRequired, server.StartOnBoot,
            server.IconRevision,
            server.ModpackName is null || server.ModpackVersion is null ? null : new ModpackSummaryDto(
                server.ModpackName, server.ModpackVersion, server.ModrinthProjectId, server.ModrinthVersionId,
                server.ModpackSource ?? "Upload"),
            server.PublicHost is null ? null : GateConfigurationService.FormatAddress(server.PublicHost, server.PublicPort ?? 25565),
            connection.Address, connection.Source, connection.Kind, connection.Note, server.AddressRevision);
    }
}
