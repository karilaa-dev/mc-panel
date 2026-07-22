using System.Globalization;
using System.Diagnostics;
using System.Collections.Concurrent;
using McPanel.Api.Infrastructure;

namespace McPanel.Api.Services;

public sealed record CgroupMemorySnapshot(
    long CurrentBytes, long PeakBytes, long SwapBytes, long AnonymousBytes,
    long FileBytes, long KernelBytes, long SocketBytes, long OomKillCount);

public sealed record CgroupWorkload(Guid ServerId, string Directory, string ProcsFile)
{
    public string File(string name) => Path.Combine(Directory, name);
}

public sealed class CgroupMemoryService
{
    public const string LauncherArgument = "--mcpanel-cgroup-exec";
    private const string CgroupMount = "/sys/fs/cgroup";
    private readonly ILogger<CgroupMemoryService> _logger;
    private readonly bool _required;
    private readonly string? _serversDirectory;
    private readonly ConcurrentDictionary<Guid, long> _observedPeaks = new();

    public CgroupMemoryService(IHostEnvironment environment, ILogger<CgroupMemoryService> logger)
    {
        _logger = logger;
        _required = environment.IsProduction() && OperatingSystem.IsLinux() &&
            Environment.GetCommandLineArgs().Contains(PersistentRuntimeHost.Argument, StringComparer.Ordinal);
        if (!_required) return;
        try
        {
            _serversDirectory = Initialize();
            logger.LogInformation("Per-server cgroup v2 memory enforcement is enabled at {CgroupDirectory}", _serversDirectory);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Per-server cgroup v2 memory enforcement is unavailable");
        }
    }

    public bool Available => _serversDirectory is not null;

    public CgroupWorkload? Create(Guid serverId, int totalMemoryMb)
    {
        if (_serversDirectory is null)
        {
            if (_required)
                throw new PanelException(500, "MEMORY_ENFORCEMENT_UNAVAILABLE",
                    "The server cannot start because cgroup memory enforcement is unavailable.",
                    "Install the delegated systemd unit and restart MC Panel.");
            return null;
        }

        var directory = Path.Combine(_serversDirectory, serverId.ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var totalBytes = checked((long)totalMemoryMb * 1024 * 1024);
            Write(directory, "memory.high", (totalBytes * 95 / 100).ToString(CultureInfo.InvariantCulture));
            Write(directory, "memory.max", totalBytes.ToString(CultureInfo.InvariantCulture));
            Write(directory, "memory.swap.max", "0");
            Write(directory, "memory.oom.group", "1");
            _observedPeaks[serverId] = 0;
            return new(serverId, directory, Path.Combine(directory, "cgroup.procs"));
        }
        catch
        {
            try { Directory.Delete(directory); } catch { }
            throw;
        }
    }

    public ProcessStartInfo Wrap(ProcessStartInfo java, CgroupWorkload? workload)
    {
        if (workload is null) return java;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new PanelException(500, "MEMORY_ENFORCEMENT_UNAVAILABLE", "The cgroup workload launcher could not be resolved.");

        var wrapped = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = java.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = java.RedirectStandardInput,
            RedirectStandardOutput = java.RedirectStandardOutput,
            RedirectStandardError = java.RedirectStandardError,
            CreateNoWindow = java.CreateNoWindow
        };
        wrapped.ArgumentList.Add(LauncherArgument);
        wrapped.ArgumentList.Add(workload.ProcsFile);
        wrapped.ArgumentList.Add(java.FileName);
        foreach (var argument in java.ArgumentList) wrapped.ArgumentList.Add(argument);
        wrapped.Environment.Clear();
        foreach (var item in java.Environment) wrapped.Environment[item.Key] = item.Value;
        return wrapped;
    }

    public CgroupMemorySnapshot? Read(CgroupWorkload? workload)
    {
        if (workload is null) return null;
        try
        {
            var current = ReadLong(workload.File("memory.current"));
            var observedPeak = _observedPeaks.AddOrUpdate(workload.ServerId, current, (_, prior) => Math.Max(prior, current));
            var stat = workload.File("memory.stat");
            return new(
                current,
                ReadOptionalLong(workload.File("memory.peak"), observedPeak),
                ReadOptionalLong(workload.File("memory.swap.current"), 0),
                ReadKey(stat, "anon"),
                ReadKey(stat, "file"),
                ReadKey(stat, "kernel"),
                ReadKey(stat, "sock"),
                ReadKey(workload.File("memory.events"), "oom_kill"));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read cgroup memory metrics for {ServerId}", workload.ServerId);
            return null;
        }
    }

    public void Remove(CgroupWorkload? workload)
    {
        if (workload is null) return;
        try
        {
            var kill = workload.File("cgroup.kill");
            if (File.Exists(kill)) File.WriteAllText(kill, "1");
            Directory.Delete(workload.Directory);
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception exception) { _logger.LogDebug(exception, "Could not remove cgroup for {ServerId}", workload.ServerId); }
        finally { _observedPeaks.TryRemove(workload.ServerId, out _); }
    }

    private static string Initialize()
    {
        if (!File.Exists(Path.Combine(CgroupMount, "cgroup.controllers")))
            throw new InvalidOperationException("cgroup v2 is not mounted.");
        var membership = File.ReadLines("/proc/self/cgroup").SingleOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The process has no cgroup v2 membership.");
        var relative = membership[3..].TrimStart('/');
        var root = Path.GetFullPath(Path.Combine(CgroupMount, relative));
        if (!root.StartsWith(CgroupMount + Path.DirectorySeparatorChar, StringComparison.Ordinal) && root != CgroupMount)
            throw new InvalidOperationException("The delegated cgroup path is invalid.");
        var controllers = File.ReadAllText(Path.Combine(root, "cgroup.controllers")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!controllers.Contains("memory", StringComparer.Ordinal))
            throw new InvalidOperationException("The memory controller was not delegated to MC Panel.");

        var panel = Path.Combine(root, "panel");
        Directory.CreateDirectory(panel);
        File.WriteAllText(Path.Combine(panel, "cgroup.procs"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        EnableMemoryController(root);

        var servers = Path.Combine(root, "servers");
        Directory.CreateDirectory(servers);
        EnableMemoryController(servers);
        foreach (var stale in Directory.EnumerateDirectories(servers))
        {
            try { Directory.Delete(stale); } catch { }
        }
        return servers;
    }

    private static void EnableMemoryController(string directory)
    {
        var subtree = Path.Combine(directory, "cgroup.subtree_control");
        if (!File.ReadAllText(subtree).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("memory", StringComparer.Ordinal))
            File.WriteAllText(subtree, "+memory");
    }

    private static void Write(string directory, string file, string value) => File.WriteAllText(Path.Combine(directory, file), value);
    private static long ReadLong(string file) => long.Parse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture);
    private static long ReadOptionalLong(string file, long fallback) => File.Exists(file) ? ReadLong(file) : fallback;

    private static long ReadKey(string file, string key)
    {
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == key) return long.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return 0;
    }
}
