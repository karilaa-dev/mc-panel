using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class SessionAudienceTests
{
    [Fact]
    public async Task Rotation_switches_broadcasts_to_a_new_group_only_after_persistence()
    {
        var audience = new SessionAudience();
        audience.Initialize("old-stamp");
        var groups = new List<string>();
        await audience.PublishAsync(group => { groups.Add(group); return Task.CompletedTask; }, CancellationToken.None);
        var oldGroup = Assert.Single(groups);
        var persisted = false;

        var revokedGroup = await audience.RotateAfterPersistAsync("new-stamp", () =>
        {
            Assert.True(audience.TryGetCurrentGroup("old-stamp", out var group));
            Assert.Equal(oldGroup, group);
            Assert.False(audience.TryGetCurrentGroup("new-stamp", out _));
            persisted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(persisted);
        Assert.Equal(oldGroup, revokedGroup);
        Assert.False(audience.TryGetCurrentGroup("old-stamp", out _));
        Assert.True(audience.TryGetCurrentGroup("new-stamp", out var newGroup));
        await audience.PublishAsync(group => { groups.Add(group); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal([oldGroup, newGroup], groups);
        Assert.NotEqual(oldGroup, newGroup);
    }

    [Fact]
    public async Task Rotation_waits_for_an_inflight_old_broadcast_and_prevents_later_stale_broadcasts()
    {
        var audience = new SessionAudience();
        audience.Initialize("old-stamp");
        var oldPublishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var groups = new List<string>();

        var oldPublish = audience.PublishAsync(async group =>
        {
            groups.Add(group);
            oldPublishEntered.SetResult();
            await releaseOldPublish.Task;
        }, CancellationToken.None);
        await oldPublishEntered.Task;

        var rotation = audience.RotateAfterPersistAsync("new-stamp", () =>
        {
            persistEntered.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(persistEntered.Task.IsCompleted);

        releaseOldPublish.SetResult();
        await oldPublish;
        var revokedGroup = await rotation;
        await persistEntered.Task;
        await audience.PublishAsync(group => { groups.Add(group); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(revokedGroup, groups[0]);
        Assert.NotEqual(groups[0], groups[1]);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task No_admin_means_no_live_broadcast_audience()
    {
        var audience = new SessionAudience();
        audience.Initialize(null);
        var called = false;

        await audience.PublishAsync(_ => { called = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(called);
        Assert.False(audience.TryGetCurrentGroup("any-stamp", out _));
    }
}
