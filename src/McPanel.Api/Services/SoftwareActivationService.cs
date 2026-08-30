using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class SoftwareActivationService(
    PanelPaths paths,
    ILogger<SoftwareActivationService> logger)
{
    internal ActivationTransaction Begin(
        Guid serverId,
        string source,
        string rollback,
        SoftwareMetadataSnapshot originalMetadata) =>
        new(serverId, source, paths.Instance(serverId), rollback, originalMetadata);

    public async Task<IReadOnlySet<Guid>> RecoverInterruptedAsync(StateDbContext state, CancellationToken cancellationToken)
    {
        var unrecovered = new HashSet<Guid>();
        if (!Directory.Exists(paths.Staging)) return unrecovered;
        foreach (var rollback in Directory.EnumerateDirectories(paths.Staging, "software-rollback-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivationTransaction? activation = null;
            Guid? serverId = TryServerIdFromRollback(rollback);
            try
            {
                activation = ActivationTransaction.Open(paths, rollback);
                serverId = activation.ServerId;
                var server = await state.Servers.SingleOrDefaultAsync(x => x.Id == activation.ServerId, cancellationToken);
                if (activation.IsCommitRecorded)
                {
                    activation.Commit();
                    logger.LogInformation("Finalized interrupted server core activation for {ServerId}", activation.ServerId);
                    continue;
                }

                if (server is not null)
                {
                    activation.OriginalMetadata.Restore(server);
                    await state.SaveChangesAsync(cancellationToken);
                }
                activation.Rollback();
                logger.LogWarning("Rolled back interrupted server core activation for {ServerId}", activation.ServerId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (activation?.IsCommitRecorded != true && serverId is { } id)
                {
                    unrecovered.Add(id);
                    try
                    {
                        var server = await state.Servers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                        if (server is not null)
                        {
                            server.State = ServerState.Error;
                            server.ProcessId = null;
                            server.UpdatedAt = DateTimeOffset.UtcNow;
                            await state.SaveChangesAsync(cancellationToken);
                        }
                    }
                    catch (Exception stateException) when (stateException is not OperationCanceledException)
                    {
                        logger.LogError(stateException, "Could not block server {ServerId} after activation recovery failed", id);
                    }
                }
                logger.LogError(exception, "Could not recover server core activation journal {RollbackDirectory}; preserving it for recovery", rollback);
            }
        }
        return unrecovered;
    }

    private static Guid? TryServerIdFromRollback(string rollback)
    {
        const string prefix = "software-rollback-";
        var name = Path.GetFileName(rollback);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 32)
            return null;
        return Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out var serverId) ? serverId : null;
    }

    public void CleanupOrphanedStaging()
    {
        if (!Directory.Exists(paths.Staging)) return;
        foreach (var directory in Directory.EnumerateDirectories(paths.Staging))
        {
            if (Path.GetFileName(directory).StartsWith("software-rollback-", StringComparison.Ordinal)) continue;
            try { Directory.Delete(directory, true); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not remove stale staging directory {StagingDirectory}", directory); }
        }
    }

    internal sealed record SoftwareMetadataSnapshot(
        ServerKind Kind, string Version, string? Build, string? Loader, string? Installer,
        LaunchMode LaunchMode, string LaunchTarget, string JavaRuntimeId, int RequiredJava,
        bool Experimental, string? ModpackName, string? ModpackVersion, string? ProjectId,
        string? VersionId, string? ModpackSource, bool RestartRequired)
    {
        public static SoftwareMetadataSnapshot Capture(ServerEntity server) => new(
            server.Kind, server.Version, server.DistributionBuild, server.LoaderVersion, server.InstallerVersion,
            server.LaunchMode, server.LaunchTarget, server.JavaRuntimeId, server.RequiredJavaMajor,
            server.IsExperimental, server.ModpackName, server.ModpackVersion, server.ModrinthProjectId,
            server.ModrinthVersionId, server.ModpackSource, server.RestartRequired);

        public void Restore(ServerEntity server)
        {
            server.Kind = Kind; server.Version = Version; server.DistributionBuild = Build;
            server.LoaderVersion = Loader; server.InstallerVersion = Installer; server.LaunchMode = LaunchMode;
            server.LaunchTarget = LaunchTarget; server.JavaRuntimeId = JavaRuntimeId;
            server.RequiredJavaMajor = RequiredJava; server.IsExperimental = Experimental;
            server.ModpackName = ModpackName; server.ModpackVersion = ModpackVersion;
            server.ModrinthProjectId = ProjectId; server.ModrinthVersionId = VersionId;
            server.ModpackSource = ModpackSource; server.RestartRequired = RestartRequired;
            server.State = ServerState.Stopped; server.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    internal sealed class ActivationTransaction
    {
        private const int ManifestVersion = 2;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string? _source;
        private readonly string _destination;
        private readonly string _rollback;
        private readonly SoftwareMetadataSnapshot _originalMetadata;
        private readonly List<FileMutation> _files;
        private readonly List<string> _createdDirectories;
        private bool _commitRecorded;
        private bool _finished;

        private ActivationTransaction(
            Guid serverId,
            string? source,
            string destination,
            string rollback,
            SoftwareMetadataSnapshot originalMetadata,
            bool commitRecorded = false,
            List<FileMutation>? files = null,
            List<string>? createdDirectories = null)
        {
            ServerId = serverId;
            _source = source;
            _destination = Path.GetFullPath(destination);
            _rollback = Path.GetFullPath(rollback);
            _originalMetadata = originalMetadata;
            _commitRecorded = commitRecorded;
            _files = files ?? [];
            _createdDirectories = createdDirectories ?? [];
        }

        public ActivationTransaction(
            Guid serverId,
            string source,
            string destination,
            string rollback,
            SoftwareMetadataSnapshot originalMetadata)
            : this(serverId, Path.GetFullPath(source), destination, rollback, originalMetadata, false, null, null) { }

        public Guid ServerId { get; }
        public SoftwareMetadataSnapshot OriginalMetadata => _originalMetadata;
        public bool IsCommitRecorded => _commitRecorded;
        public bool IsFinished => _finished;
        public IReadOnlyList<string> ActivatedPaths => _files.Select(file => ResolveDestination(file.RelativePath)).ToArray();
        private string ManifestPath => Path.Combine(_rollback, "activation-manifest.json");

        public void Activate()
        {
            if (_source is null) throw new InvalidOperationException("A recovered activation cannot be started again.");
            Directory.CreateDirectory(_destination);
            WriteManifest();
            foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = NormalizeRelative(Path.GetRelativePath(_source, file));
                var target = ResolveDestination(relative);
                if (Directory.Exists(target))
                    throw PanelProblems.Conflict("SOFTWARE_ACTIVATION_CONFLICT", $"A directory blocks {relative}.");
                CreateParents(Path.GetDirectoryName(target)!);

                var replaced = File.Exists(target);
                var backup = ResolveRelative(_rollback, relative);
                if (replaced) Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                _files.Add(new FileMutation(relative, replaced));
                WriteManifest();

                if (replaced) File.Move(target, backup);
                File.Move(file, target);
            }
        }

        public void Commit()
        {
            if (_finished) return;
            if (Directory.Exists(_rollback)) Directory.Delete(_rollback, true);
            _finished = true;
        }

        public void MarkCommitted()
        {
            if (_finished) throw new InvalidOperationException("The activation is already finished.");
            _commitRecorded = true;
            try { WriteManifest(); }
            catch
            {
                _commitRecorded = false;
                throw;
            }
        }

        public void PrepareRollback()
        {
            if (_finished || !_commitRecorded) return;
            _commitRecorded = false;
            WriteManifest();
        }

        public void Rollback()
        {
            if (_finished) return;
            foreach (var mutation in _files.AsEnumerable().Reverse())
            {
                var target = ResolveDestination(mutation.RelativePath);
                if (mutation.Replaced)
                {
                    var backup = ResolveRelative(_rollback, mutation.RelativePath);
                    if (!File.Exists(backup)) continue;
                    if (File.Exists(target)) File.Delete(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
            foreach (var relative in _createdDirectories.AsEnumerable().Reverse())
            {
                var directory = ResolveDestination(relative);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            if (Directory.Exists(_rollback)) Directory.Delete(_rollback, true);
            _finished = true;
        }

        public static ActivationTransaction Open(PanelPaths paths, string rollback)
        {
            var manifestPath = Path.Combine(rollback, "activation-manifest.json");
            var manifest = JsonSerializer.Deserialize<ActivationManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("The activation manifest is empty.");
            if (manifest.Version != ManifestVersion || manifest.ServerId == Guid.Empty ||
                manifest.OriginalMetadata is null || manifest.Files is null || manifest.CreatedDirectories is null)
                throw new InvalidDataException("The activation manifest is invalid.");
            foreach (var file in manifest.Files) ResolveRelative(paths.Instance(manifest.ServerId), file.RelativePath);
            foreach (var directory in manifest.CreatedDirectories) ResolveRelative(paths.Instance(manifest.ServerId), directory);
            return new ActivationTransaction(manifest.ServerId, null, paths.Instance(manifest.ServerId), rollback,
                manifest.OriginalMetadata, manifest.CommitRecorded, manifest.Files, manifest.CreatedDirectories);
        }

        private void CreateParents(string directory)
        {
            EnsureNoReparsePoints(_destination, directory);
            var missing = new Stack<string>();
            var current = directory;
            while (!Directory.Exists(current))
            {
                if (File.Exists(current))
                    throw PanelProblems.Conflict("SOFTWARE_ACTIVATION_CONFLICT", "A file blocks a required server core directory.");
                missing.Push(current);
                current = Path.GetDirectoryName(current)
                    ?? throw PanelProblems.Conflict("SOFTWARE_ACTIVATION_CONFLICT", "A server core path escaped the server instance.");
            }
            while (missing.TryPop(out var item))
            {
                var relative = NormalizeRelative(Path.GetRelativePath(_destination, item));
                ResolveRelative(_destination, relative);
                _createdDirectories.Add(relative);
                WriteManifest();
                Directory.CreateDirectory(item);
            }
        }

        private void WriteManifest()
        {
            Directory.CreateDirectory(_rollback);
            var temporary = ManifestPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream,
                    new ActivationManifest(ManifestVersion, ServerId, _commitRecorded, _originalMetadata,
                        _files, _createdDirectories), JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, ManifestPath, true);
        }

        private static string NormalizeRelative(string relative) =>
            relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

        private string ResolveDestination(string relative)
        {
            var path = ResolveRelative(_destination, relative);
            EnsureNoReparsePoints(_destination, path);
            return path;
        }

        private static void EnsureNoReparsePoints(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root);
            var relative = Path.GetRelativePath(fullRoot, path);
            var current = fullRoot;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Prepend(""))
            {
                if (segment.Length > 0) current = Path.Combine(current, segment);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw PanelProblems.Conflict("SOFTWARE_ACTIVATION_CONFLICT",
                        "A symbolic link blocks a server core activation path.");
            }
        }

        private static string ResolveRelative(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new InvalidDataException("The activation manifest contains an invalid path.");
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("The activation manifest path escapes the server instance.");
            return fullPath;
        }

        private sealed record ActivationManifest(
            int Version,
            Guid ServerId,
            bool CommitRecorded,
            SoftwareMetadataSnapshot OriginalMetadata,
            List<FileMutation> Files,
            List<string> CreatedDirectories);

        private sealed record FileMutation(string RelativePath, bool Replaced);
    }
}
