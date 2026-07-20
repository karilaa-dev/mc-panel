using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed partial class JavaDiscoveryService(IDbContextFactory<StateDbContext> stateFactory, ILogger<JavaDiscoveryService> logger)
{
    public async Task<IReadOnlyList<JavaRuntimeDto>> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.JavaRuntimes.OrderByDescending(x => x.Major).ThenBy(x => x.Path)
            .Select(x => new JavaRuntimeDto(x.Id, x.Path, x.Version, x.Major, x.Vendor, x.Architecture, x.IsCustom))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JavaRuntimeDto>> ScanAsync(CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, JavaRuntimeDto>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(FindCandidates(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (path, token) =>
        {
            try
            {
                var runtime = await ProbeAsync(path, false, token);
                results[runtime.Id] = runtime;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Java candidate {Path} could not be probed", path);
            }
        });

        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var custom = await db.JavaRuntimes.Where(x => x.IsCustom).ToListAsync(cancellationToken);
        foreach (var item in custom)
        {
            try { results[item.Id] = await ProbeAsync(item.Path, true, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning(exception, "Custom Java runtime {Path} is currently unavailable", item.Path); }
        }

        var stale = await db.JavaRuntimes.Where(x => !x.IsCustom).ToListAsync(cancellationToken);
        db.JavaRuntimes.RemoveRange(stale.Where(x => !results.ContainsKey(x.Id)));
        foreach (var dto in results.Values)
        {
            var entity = await db.JavaRuntimes.FindAsync([dto.Id], cancellationToken);
            if (entity is null)
            {
                db.JavaRuntimes.Add(new JavaRuntimeEntity
                {
                    Id = dto.Id, Path = dto.Path, Version = dto.Version, Major = dto.Major,
                    Vendor = dto.Vendor, Architecture = dto.Architecture, IsCustom = dto.IsCustom
                });
            }
            else
            {
                entity.Path = dto.Path; entity.Version = dto.Version; entity.Major = dto.Major;
                entity.Vendor = dto.Vendor; entity.Architecture = dto.Architecture;
                entity.IsCustom |= dto.IsCustom; entity.LastSeenAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return results.Values.OrderByDescending(x => x.Major).ThenBy(x => x.Path).ToList();
    }

    public async Task<JavaRuntimeDto> AddCustomAsync(string path, CancellationToken cancellationToken)
    {
        var runtime = await ProbeAsync(path, true, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.JavaRuntimes.FindAsync([runtime.Id], cancellationToken);
        if (entity is null)
        {
            db.JavaRuntimes.Add(new JavaRuntimeEntity
            {
                Id = runtime.Id, Path = runtime.Path, Version = runtime.Version, Major = runtime.Major,
                Vendor = runtime.Vendor, Architecture = runtime.Architecture, IsCustom = true
            });
        }
        else
        {
            entity.Path = runtime.Path; entity.Version = runtime.Version; entity.Major = runtime.Major;
            entity.Vendor = runtime.Vendor; entity.Architecture = runtime.Architecture; entity.IsCustom = true;
            entity.LastSeenAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return runtime;
    }

    public async Task<JavaRuntimeDto> ProbeAsync(string path, bool custom, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The Java executable was not found.");
        var canonical = Canonicalize(path);
        var start = new ProcessStartInfo
        {
            FileName = canonical,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        start.Environment.Remove("JAVA_TOOL_OPTIONS");
        start.Environment.Remove("_JAVA_OPTIONS");
        start.Environment.Remove("JDK_JAVA_OPTIONS");
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MCPANEL_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "Java could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            try { await Task.WhenAll(outputTask, errorTask); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "Java did not answer the version probe in time.");
        }
        var output = await outputTask + "\n" + await errorTask;
        var version = Property(output, "java.version") ?? VersionRegex().Match(output).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(version))
            throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The executable did not report a Java version.");
        var major = ParseMajor(version);
        if (major < 8) throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", "Java 8 or newer is required.");
        var vendor = Property(output, "java.vendor") ?? "Unknown";
        var architecture = Property(output, "os.arch") ?? RuntimeInformation.ProcessArchitecture.ToString();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
        return new JavaRuntimeDto(id, canonical, version.Trim(), major, vendor.Trim(), architecture.Trim(), custom);
    }

    public static int ParseMajor(string version)
    {
        var normalized = version.Trim().Trim('"');
        if (normalized.StartsWith("1.", StringComparison.Ordinal)) normalized = normalized[2..];
        var digits = new string(normalized.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : 0;
    }

    private static string? Property(string output, string name)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(name + " =", StringComparison.Ordinal)) return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
        }
        return null;
    }

    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        try { return File.ResolveLinkTarget(full, true)?.FullName ?? full; }
        catch { return full; }
    }

    internal static IEnumerable<string> FindCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "bin", ExecutableName));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory, ExecutableName));
        candidates.Add(OperatingSystem.IsWindows() ? "java.exe" : "/usr/bin/java");
        if (!OperatingSystem.IsWindows())
        {
            candidates.Add("/etc/alternatives/java");
            candidates.Add("/usr/local/bin/java");
        }
        var sdkman = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { "/usr/lib/jvm", "/usr/local/lib/jvm", "/usr/java", "/opt", "/opt/java", "/opt/jdk" };
        if (!string.IsNullOrWhiteSpace(sdkman)) roots.Add(Path.Combine(sdkman, ".sdkman", "candidates", "java"));
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(root).Take(200).ToArray(); }
            catch { continue; }
            foreach (var directory in directories)
            {
                candidates.Add(Path.Combine(directory, "bin", ExecutableName));
                candidates.Add(Path.Combine(directory, "jre", "bin", ExecutableName));
                if (root is "/opt" or "/usr/local")
                {
                    try
                    {
                        foreach (var nested in Directory.EnumerateDirectories(directory).Take(50))
                        {
                            candidates.Add(Path.Combine(nested, "bin", ExecutableName));
                            candidates.Add(Path.Combine(nested, "jre", "bin", ExecutableName));
                        }
                    }
                    catch { }
                }
            }
        }
        return candidates.Where(File.Exists).Take(500);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    [GeneratedRegex("(?:java|openjdk) version \\\"([^\\\"]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
