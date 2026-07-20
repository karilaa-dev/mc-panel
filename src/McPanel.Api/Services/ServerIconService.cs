using System.Buffers.Binary;
using System.Security.Cryptography;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class ServerIconService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    AsyncKeyedLock keyedLock,
    IServerProcessStatus processStatus)
{
    public const int MaximumIconBytes = 256 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task<string> GetPathAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
            throw PanelProblems.NotFound("Server");
        var path = IconPath(id);
        if (server.IconRevision is null || !File.Exists(path)) throw PanelProblems.NotFound("Server icon");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        ValidatePng(bytes);
        return path;
    }

    public async Task<ServerIconDto> SaveAsync(Guid id, IFormFile upload, CancellationToken cancellationToken)
    {
        if (upload is null || upload.Length <= 0) throw PanelProblems.Validation("A PNG server icon is required.");
        if (upload.Length > MaximumIconBytes)
            throw new PanelException(413, "ICON_TOO_LARGE", $"The final server icon cannot exceed {MaximumIconBytes / 1024} KiB.");

        byte[] bytes;
        await using (var input = upload.OpenReadStream())
        await using (var output = new MemoryStream((int)upload.Length))
        {
            await input.CopyToAsync(output, cancellationToken);
            if (output.Length > MaximumIconBytes)
                throw new PanelException(413, "ICON_TOO_LARGE", $"The final server icon cannot exceed {MaximumIconBytes / 1024} KiB.");
            bytes = output.ToArray();
        }
        ValidatePng(bytes);
        var revision = Revision(bytes);

        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        EnsureStableState(server, processStatus.IsRunning(id));
        var destination = IconPath(id);
        var existed = File.Exists(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $".server-icon.{nonce}.tmp");
        var rollback = Path.Combine(directory, $".server-icon.{nonce}.rollback");
        var activated = false;
        var committed = false;
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (existed) File.Replace(temporary, destination, rollback);
            else File.Move(temporary, destination);
            activated = true;
            server.IconRevision = revision;
            server.RestartRequired |= server.State == ServerState.Running;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            committed = true;
        }
        finally
        {
            if (activated && !committed)
            {
                if (existed)
                {
                    if (!File.Exists(rollback)) throw new IOException("The prior server icon rollback file is missing.");
                    if (File.Exists(destination)) File.Replace(rollback, destination, null);
                    else File.Move(rollback, destination);
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(rollback)) File.Delete(rollback);
        }
        return new(revision);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        EnsureStableState(server, processStatus.IsRunning(id));
        var destination = IconPath(id);
        var existed = File.Exists(destination);
        var rollback = Path.Combine(paths.Instance(id), $".server-icon.{Guid.NewGuid():N}.rollback");
        var moved = false;
        var committed = false;
        try
        {
            if (existed) { File.Move(destination, rollback); moved = true; }
            server.IconRevision = null;
            server.RestartRequired |= server.State == ServerState.Running && existed;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            committed = true;
        }
        finally
        {
            if (moved && !committed && File.Exists(rollback)) File.Move(rollback, destination);
            if (committed && File.Exists(rollback)) File.Delete(rollback);
        }
    }

    public async Task BackfillAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.Servers.Where(x => x.IconRevision == null).ToListAsync(cancellationToken);
        var changed = false;
        foreach (var server in servers)
        {
            var path = IconPath(server.Id);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > MaximumIconBytes) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                ValidatePng(bytes);
                server.IconRevision = Revision(bytes);
                changed = true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or PanelException) { }
        }
        if (changed) await db.SaveChangesAsync(cancellationToken);
    }

    public static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || bytes.Length > MaximumIconBytes || !bytes[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) != 64 || BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) != 64)
            throw PanelProblems.Validation("The server icon must be a valid 64×64 PNG no larger than 256 KiB.");
    }

    private string IconPath(Guid id) => Path.Combine(paths.Instance(id), "server-icon.png");
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void EnsureStableState(ServerEntity server, bool processRunning)
    {
        var consistent = server.State switch
        {
            ServerState.Running => processRunning,
            ServerState.Stopped or ServerState.Crashed => !processRunning,
            _ => false
        };
        if (!consistent) throw PanelProblems.Conflict("SERVER_BUSY", "The server icon cannot be changed in its current state.");
    }
}
