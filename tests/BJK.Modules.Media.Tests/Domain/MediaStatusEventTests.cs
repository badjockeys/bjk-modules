using BJK.Modules.Media.Domain;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Domain;

/// <summary>The status changes that become media.ready / media.failed events.</summary>
public sealed class MediaStatusEventTests
{
    private static MediaItem Processing()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(100, Now, TimeSpan.FromDays(90));
        return item;
    }

    [Fact]
    public void Becoming_ready_is_announced_once()
    {
        var item = Processing();
        Assert.Empty(item.TakeStatusChanges());

        item.MarkReady(10, 12m, 1280, 720, null, Now);

        var change = Assert.Single(item.TakeStatusChanges());
        Assert.Equal(MediaStatus.Ready, change.Status);
        Assert.Empty(item.TakeStatusChanges());
    }

    [Fact]
    public void Needing_review_is_not_announced_but_the_approval_is()
    {
        var item = Processing();
        item.MarkNeedsReview("unsure", 10, 12m, 1280, 720, null, Now);
        Assert.Empty(item.TakeStatusChanges());

        item.Approve(Guid.NewGuid(), Now);

        Assert.Equal(MediaStatus.Ready, Assert.Single(item.TakeStatusChanges()).Status);
    }

    [Fact]
    public void Failing_is_announced_with_the_reason_once()
    {
        var item = Processing();
        item.MarkFailed("Videos can be up to 30 seconds long.", Now);
        item.MarkFailed("again", Now);

        var change = Assert.Single(item.TakeStatusChanges());
        Assert.Equal((MediaStatus.Failed, "Videos can be up to 30 seconds long."), (change.Status, change.Reason));
    }

    [Fact]
    public void Deleting_before_it_is_ready_is_announced_but_deleting_ready_media_is_not()
    {
        var pending = Processing();
        pending.Delete(Now);
        var change = Assert.Single(pending.TakeStatusChanges());
        Assert.Equal((MediaStatus.Deleted, MediaItem.DeletedReason), (change.Status, change.Reason));

        var ready = Processing();
        ready.MarkReady(10, 12m, 1280, 720, null, Now);
        ready.TakeStatusChanges();
        ready.Delete(Now);
        Assert.Empty(ready.TakeStatusChanges());
    }
}
