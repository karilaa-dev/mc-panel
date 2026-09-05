using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class GateBackendConfigurationService(
    PanelPaths paths, IDbContextFactory<StateDbContext> stateFactory, AsyncKeyedLock keyedLock)
{
    private static readonly string[] Keys = ["online-mode", "enforce-secure-profile", "server-ip"];

    public async Task PrepareAsync(Guid gateId, string expectedRevision, CancellationToken token)
    {
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var settings = await db.GateSettings.SingleOrDefaultAsync(x => x.ServerId == gateId, token)
            ?? throw PanelProblems.NotFound("Gate");
        var ids = await db.GateBackends.Where(x => x.GateServerId == gateId).Select(x => x.BackendServerId).ToListAsync(token);
        var locks = new List<IDisposable>();
        var changes = new List<(string Path, string Before, string After)>();
        try
        {
            foreach (var id in ids.Append(gateId).Distinct().Order()) locks.Add(await keyedLock.AcquireAsync(id, token));
            await db.Entry(settings).ReloadAsync(token);
            if (settings.Revision != expectedRevision)
                throw new PanelException(409, "GATE_CONFIG_CHANGED", "Gate settings changed. Refresh before preparing backends.");
            var servers = await db.Servers.Where(x => ids.Contains(x.Id) || x.Id == gateId).ToListAsync(token);
            if (servers.Any(x => x.State != ServerState.Stopped || x.ProcessId != null || x.RecoveryRequired))
                throw new PanelException(409, "GATE_BACKEND_BUSY", "Stop Gate and all selected managed backends before preparing their network settings.");
            if (File.Exists(paths.RuntimeSocket))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var snapshots = await PersistentRuntimeProtocol.SendAsync<RuntimeServerSnapshot[]>(paths.RuntimeSocket, "snapshot", null, timeout.Token);
                if (snapshots is null || snapshots.Any(x => servers.Any(s => s.Id == x.ServerId) && PersistentRuntimeClient.IsActive(x.State)))
                    throw new PanelException(409, "GATE_BACKEND_BUSY", "The runtime still has an active proxy or backend. Wait for it to stop.");
            }
            if (settings.Mode == GateMode.Classic && !GateConfigurationService.Classic(settings).OnlineMode)
                throw new PanelException(409, "GATE_AUTHENTICATION_REQUIRED", "Enable Classic online authentication before preparing offline backends.");
            var conflicting = await (from link in db.GateBackends join other in db.GateSettings on link.GateServerId equals other.ServerId
                                     where ids.Contains(link.BackendServerId) && other.ServerId != gateId && other.Mode != settings.Mode
                                     select other.ServerId).AnyAsync(token);
            if (conflicting) throw new PanelException(409, "GATE_BACKEND_MODE_CONFLICT", "A selected backend also belongs to a Gate instance using a different mode. Separate those backends before changing their authentication.");
            foreach (var server in servers.Where(x => x.Id != gateId))
            {
                if (settings.Mode == GateMode.Classic && server.Kind == ServerKind.Vanilla && settings.ClassicForwardingMode != GateForwardingMode.None)
                    throw new PanelException(409, "GATE_BACKEND_AUTHENTICATION", "Vanilla requires forwarding None in Classic mode. It does not implement Velocity or Bungee forwarding.");
                var file = Path.Combine(paths.Instance(server.Id), "server.properties");
                var before = await File.ReadAllTextAsync(file, token);
                var document = PropertiesDocument.Parse(before);
                var saved = Path.Combine(paths.Instance(server.Id), ".mcpanel-proxy", "original-network.json");
                Dictionary<string, string>? original = null;
                if (File.Exists(saved)) original = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(saved, token))
                    ?? throw new InvalidDataException("The saved backend network settings are invalid.");
                if (settings.Mode == GateMode.Classic)
                {
                    if (original is null)
                    {
                        original = Keys.ToDictionary(key => key, key => document.Get(key) ?? (key == "server-ip" ? "" : "true"));
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        await AtomicWriteAsync(saved, JsonSerializer.Serialize(original), token);
                    }
                    document.Set("online-mode", "false");
                    document.Set("enforce-secure-profile", "false");
                    document.Set("server-ip", "127.0.0.1");
                }
                else if (original is not null)
                    foreach (var key in Keys) document.Set(key, original[key]);
                else continue;
                changes.Add((file, before, document.ToString()));
            }
            token.ThrowIfCancellationRequested();
            var applied = new List<(string Path, string Before)>();
            try
            {
                foreach (var change in changes)
                {
                    // Keep the exact prior file for manual recovery; never overwrite a world.
                    await AtomicWriteAsync(change.Path + ".before-gate-" + Guid.NewGuid().ToString("N"), change.Before, CancellationToken.None);
                    await AtomicWriteAsync(change.Path, change.After, CancellationToken.None);
                    applied.Add((change.Path, change.Before));
                }
                settings.ConfigurationDirty = true;
                settings.Revision = Guid.NewGuid().ToString("N");
                settings.UpdatedAt = DateTimeOffset.UtcNow;
                db.AuditEvents.Add(new() { Actor = "administrator", Action = "prepare-gate-backends", Target = $"{gateId}:{settings.Mode}", Outcome = "succeeded" });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                foreach (var change in applied.AsEnumerable().Reverse())
                {
                    try { await AtomicWriteAsync(change.Path, change.Before, CancellationToken.None); }
                    catch
                    {
                        var server = servers.Single(x => change.Path == Path.Combine(paths.Instance(x.Id), "server.properties"));
                        server.RecoveryRequired = true; server.RecoveryReason = "Backend network settings could not be rolled back. Restore the retained before-gate properties file."; server.State = ServerState.Error;
                    }
                }
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        finally { foreach (var held in locks.AsEnumerable().Reverse()) held.Dispose(); }
    }

    private static async Task AtomicWriteAsync(string file, string text, CancellationToken token)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
