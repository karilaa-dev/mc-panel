using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class OperationsMonitor(IDbContextFactory<StateDbContext> factory, PanelPaths paths,
    IOptions<PanelOptions> options, IHttpClientFactory clients, ILogger<OperationsMonitor> logger, PersistentRuntimeClient runtime) : BackgroundService
{
    private readonly SemaphoreSlim _incidentLock = new(1, 1);
    private readonly Dictionary<Guid, long> _lastDrops = new();

    public async Task SetIncidentAsync(string code, Guid? serverId, string message, bool active, CancellationToken token)
    {
        await _incidentLock.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var incident = await db.Incidents.Where(x => x.Code == code && x.ServerId == serverId && x.ResolvedAt == null).FirstOrDefaultAsync(token);
            if (incident is null && !active) return;
            if (incident is null) { incident = new() { Code = code, ServerId = serverId, Message = message }; db.Incidents.Add(incident); }
            incident.Message = message; incident.UpdatedAt = DateTimeOffset.UtcNow;
            if (!active) incident.ResolvedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
        }
        finally { _incidentLock.Release(); }
    }

    public async Task AuditAsync(string actor, string action, string target, string outcome, string? correlation, string? remote, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        db.AuditEvents.Add(new() { Actor = Limit(actor, 128), Action = Limit(action, 128), Target = Limit(target, 4608), Outcome = Limit(outcome, 64), CorrelationId = correlation, RemoteAddress = remote });
        await db.SaveChangesAsync(token);
    }
    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await InspectAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Operational monitoring failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    internal async Task InspectAsync(CancellationToken token)
    {
        if (runtime.Enabled)
        {
            foreach (var snapshot in await runtime.RefreshAsync(token))
            {
                await SetIncidentAsync("RUNTIME_STORAGE", snapshot.ServerId, snapshot.StorageError ?? "Runtime storage is available.", snapshot.StorageError is not null, token);
                await SetIncidentAsync("RUNTIME_LOGS_DROPPED", snapshot.ServerId, $"The runtime dropped {snapshot.DroppedLogLines:N0} console lines. Check storage and log volume.", snapshot.DroppedLogLines > _lastDrops.GetValueOrDefault(snapshot.ServerId), token);
                _lastDrops[snapshot.ServerId] = snapshot.DroppedLogLines;
            }
        }
        var runtimeIncidents = Path.Combine(paths.Runtime, "incidents");
        if (Directory.Exists(runtimeIncidents))
            foreach (var file in Directory.EnumerateFiles(runtimeIncidents, "*.json"))
            {
                var incident = JsonSerializer.Deserialize<RuntimeIncident>(await File.ReadAllTextAsync(file, token));
                if (incident is not null) await SetIncidentAsync(incident.Code, incident.ServerId, incident.Message, !incident.Resolved, token);
            }
        var drive = ArchiveIO.DataDrive(paths.Data);
        await SetIncidentAsync("LOW_DISK_SPACE", null, "The data filesystem is below its free-space reserve.",
            drive.AvailableFreeSpace < Math.Max(options.Value.ReservedDiskBytes * 2, drive.TotalSize / 10), token);
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var server in await db.Servers.AsNoTracking().ToListAsync(token))
        {
            await SetIncidentAsync("RECOVERY_REQUIRED", server.Id, server.RecoveryReason ?? "Server recovery requires attention.", server.RecoveryRequired, token);
            var backup = await db.Backups.Where(x => x.ServerId == server.Id && x.VerifiedAt != null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
            if (server.Kind != ServerKind.Gate)
                await SetIncidentAsync("BACKUP_OVERDUE", server.Id, "No verified server backup is available from the last hour.", backup is null || backup.CreatedAt < DateTimeOffset.UtcNow.AddHours(-1), token);
        }
        foreach (var schedule in await db.Schedules.AsNoTracking().ToListAsync(token))
            await SetIncidentAsync("SCHEDULE_" + schedule.Id.ToString("N"), schedule.ServerId,
                $"Schedule '{schedule.Name}': {schedule.LastResult}", schedule.LastResult?.StartsWith("Failed", StringComparison.Ordinal) == true, token);
        var failedBackups = await db.Jobs.Where(x => x.Type == "Backup" && x.State == JobState.Failed).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
        foreach (var job in failedBackups.GroupBy(x => x.ServerId).Select(x => x.First()))
        {
            var recovered = await db.Jobs.AnyAsync(x => x.Type == "Backup" && x.ServerId == job.ServerId && x.State == JobState.Completed && x.CreatedAt > job.CreatedAt, token);
            await SetIncidentAsync("BACKUP_FAILED", job.ServerId, job.Error ?? "A backup failed.", !recovered, token);
        }
        var point = await db.RecoveryPoints.Where(x => x.ReplicatedAt != null && x.VerifiedAt != null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
        await SetIncidentAsync("OFF_HOST_RECOVERY_OVERDUE", null, "No verified off-host recovery point is available from the last hour.", point is null || point.CreatedAt < DateTimeOffset.UtcNow.AddHours(-1), token);
        await DeliverAlertsAsync(token);
        var auditCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.Value.AuditRetentionDays));
        var historyCutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await db.AuditEvents.Where(x => x.Timestamp < auditCutoff).ExecuteDeleteAsync(token);
        await db.ScheduleRuns.Where(x => x.FinishedAt < historyCutoff).ExecuteDeleteAsync(token);
        await db.Incidents.Where(x => x.ResolvedAt < historyCutoff).ExecuteDeleteAsync(token);
    }

    private async Task DeliverAlertsAsync(CancellationToken token)
    {
        if (options.Value.AlertWebhookFile is not { Length: > 0 } file || !File.Exists(file)) return;
        var value = (await File.ReadAllTextAsync(file, token)).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
        { logger.LogWarning("Alert webhook credential must contain an HTTPS URL"); return; }
        await using var db = await factory.CreateDbContextAsync(token);
        var pending = await db.Incidents.Where(x => x.NotifiedAt == null && x.ResolvedAt == null || x.NotifiedAt != null && x.ResolvedAt != null && x.RecoveryNotifiedAt == null).OrderBy(x => x.OpenedAt).Take(20).ToListAsync(token);
        foreach (var incident in pending)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new { id = incident.Id, code = incident.Code, serverId = incident.ServerId, status = incident.ResolvedAt is null ? "open" : "resolved", message = incident.Message, timestamp = incident.UpdatedAt })
            };
            using var response = await clients.CreateClient("alerts").SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            if (incident.ResolvedAt is null) incident.NotifiedAt = DateTimeOffset.UtcNow;
            else incident.RecoveryNotifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
        }
    }
}
