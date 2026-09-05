using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class OperationQueueTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"mcpanel-operation-queue-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Four_stalled_downloads_time_out_and_release_workers_for_later_work()
    {
        var factory = new TestStateDbContextFactory(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_database};Cache=Shared").Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSignalR();
        await using var provider = services.BuildServiceProvider();
        var queue = new OperationQueue(provider, factory, provider.GetRequiredService<IHubContext<PanelHub>>(), new SessionAudience(), new TestApplicationLifetime(), NullLogger<OperationQueue>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int readers = 0;
        var download = new ValidatedDownloadClient(new StalledHttpFactory(() => { if (Interlocked.Increment(ref readers) == 4) entered.TrySetResult(); }))
            { IdleTimeout = TimeSpan.FromMilliseconds(500), TotalTimeout = TimeSpan.FromSeconds(5) };
        var files = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var file = _database + $"-{index}.download"; files.Add(file);
            await queue.EnqueueAsync("Download", null, (_, _, token) => download.DownloadAsync(new(new Uri("https://piston-data.mojang.com/server.jar"), "sha256", new string('0', 64), null, "server.jar"), file, token), default);
        }
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.EnqueueAsync("Later", null, (_, _, _) => { later.TrySetResult(); return Task.CompletedTask; }, default);
        await queue.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await later.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((await queue.ListAsync(null, 10, default)).Count(x => x.State == JobState.Failed) != 4 && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(4, (await queue.ListAsync(null, 10, default)).Count(x => x.State == JobState.Failed));
            Assert.All(files, file => Assert.False(File.Exists(file)));
        }
        finally { await queue.StopAsync(default); }
    }

    private sealed class StalledHttpFactory(Action entered) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StalledHandler(entered));
    }
    private sealed class StalledHandler(Action entered) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(entered)) });
    }
    private sealed class StalledStream(Action entered) : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    [Fact]
    public async Task Request_cancellation_after_job_commit_does_not_strand_the_job_outside_the_queue()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={_database};Cache=Shared").Options;
        var factory = new TestStateDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        await using var provider = services.BuildServiceProvider();
        var lifetime = new TestApplicationLifetime();
        var queue = new OperationQueue(
            provider,
            factory,
            provider.GetRequiredService<IHubContext<PanelHub>>(),
            new SessionAudience(),
            lifetime,
            NullLogger<OperationQueue>.Instance);

        var serverId = Guid.NewGuid();
        var related = await queue.EnqueueAsync("Test", serverId, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(serverId, related.ServerId);

        for (var index = 1; index < 256; index++)
            await queue.EnqueueAsync("Test", null, (_, _, _) => Task.CompletedTask, CancellationToken.None);

        using var request = new CancellationTokenSource();
        var overflow = queue.EnqueueAsync("Test", null, (_, _, _) => Task.CompletedTask, request.Token);
        await WaitForJobCountAsync(factory, 257);
        request.Cancel();
        await Task.Delay(100);
        Assert.False(overflow.IsCompleted);

        await queue.StartAsync(CancellationToken.None);
        try
        {
            var accepted = await overflow.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(JobState.Queued, accepted.State);
            await WaitForAllCompletedAsync(factory, 257);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForJobCountAsync(IDbContextFactory<StateDbContext> factory, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Jobs.CountAsync() == expected) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The queue did not persist {expected} jobs.");
    }

    private static async Task WaitForAllCompletedAsync(IDbContextFactory<StateDbContext> factory, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Jobs.CountAsync(x => x.State == JobState.Completed) == expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The committed jobs did not all execute.");
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    public void Dispose()
    {
        if (File.Exists(_database)) File.Delete(_database);
        if (File.Exists(_database + "-shm")) File.Delete(_database + "-shm");
        if (File.Exists(_database + "-wal")) File.Delete(_database + "-wal");
    }
}
