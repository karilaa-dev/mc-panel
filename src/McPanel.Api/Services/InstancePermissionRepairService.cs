using McPanel.Api.Configuration;
using McPanel.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Services;

internal static class InstancePermissionRepairCommand
{
    public const string Argument = "--mcpanel-repair-instance-permissions";

    public static bool IsInvocation(string[] args) => args.Length > 0 && args[0] == Argument;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != Argument)
        {
            Console.Error.WriteLine("Invalid MC Panel instance permission repair invocation.");
            return 2;
        }

        try
        {
            var options = new PanelOptions
            {
                DataDirectory = Path.GetFullPath(args[1]),
                ConfigDirectory = Path.Combine(Path.GetFullPath(args[1]), ".permission-repair-config")
            };
            var paths = new PanelPaths(options);
            if (!File.Exists(paths.StateDatabase)) return 0;
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = paths.StateDatabase,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            };
            var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
                .UseSqlite(connection.ToString()).Options;
            var service = new InstancePermissionService(paths, new RepairDbContextFactory(dbOptions),
                NullLogger<InstancePermissionService>.Instance);
            await service.NormalizeAllAsync(CancellationToken.None, tolerateFailures: false);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not repair MC Panel instance permissions: {exception.Message}");
            return 1;
        }
    }

    private sealed class RepairDbContextFactory(DbContextOptions<StateDbContext> options)
        : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }
}

public sealed class InstancePermissionRepairService(
    InstancePermissionService permissions,
    IHostApplicationLifetime lifetime,
    ILogger<InstancePermissionRepairService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stoppingRegistration = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
            await permissions.NormalizeAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not repair existing instance permissions");
        }
    }
}
