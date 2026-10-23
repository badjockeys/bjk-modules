using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Media.UseCases.Vault;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

/// <summary>The vault (owner, 23 Oct 2026): originals kept by plan, 30 days / 12 months / 3 years in 2 / 25 / 100 GB.</summary>
public sealed class VaultTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly IVaultStore _store = Substitute.For<IVaultStore>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IPlanResolver _resolver = Substitute.For<IPlanResolver>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly Microsoft.Extensions.Options.IOptions<VaultOptions> _options = Microsoft.Extensions.Options.Options.Create(new VaultOptions());
    private readonly Microsoft.Extensions.Options.IOptions<PlansOptions> _plansOptions = Microsoft.Extensions.Options.Options.Create(new PlansOptions());

    public VaultTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _resolver.CurrentPlan().Returns(SubscriptionPlan.Free);
        _store.AllOfUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _storage.CreateUploadAsync(Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => Upload(ci.ArgAt<string>(1)));
        _storage.CreateDownloadUrlAsync(Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri($"https://vault.example.com/{ci.ArgAt<string>(1)}?name={ci.ArgAt<string>(3)}"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Expiry follows the plan ---

    [Fact]
    public void Upgrading_keeps_clips_longer_at_once()
    {
        var clip = VaultClip.StartUpload(UserId, "clip.mp4", "video/mp4", 10, Now, TimeSpan.FromDays(30));

        Assert.True(clip.FollowPlan(TimeSpan.FromDays(365), Now.AddDays(5), TimeSpan.FromDays(7)));

        Assert.Equal(Now.AddDays(365), clip.ExpiresAt);
    }

    [Fact]
    public void Downgrading_gives_seven_days_notice_before_out_of_date_clips_go()
    {
        var old = VaultClip.StartUpload(UserId, "old.mp4", "video/mp4", 10, Now.AddDays(-200), TimeSpan.FromDays(365));
        var recent = VaultClip.StartUpload(UserId, "new.mp4", "video/mp4", 10, Now.AddDays(-2), TimeSpan.FromDays(365));

        old.FollowPlan(TimeSpan.FromDays(30), Now, TimeSpan.FromDays(7));
        recent.FollowPlan(TimeSpan.FromDays(30), Now, TimeSpan.FromDays(7));

        Assert.Equal(Now.AddDays(7), old.ExpiresAt);
        Assert.Equal(Now.AddDays(28), recent.ExpiresAt);
    }

    [Fact]
    public void File_names_are_only_ever_names()
    {
        var clip = VaultClip.StartUpload(UserId, "../../etc/2026\u0007_dashcam.MP4", "video/mp4", 10, Now, TimeSpan.FromDays(30));

        Assert.Equal("2026_dashcam.MP4", clip.FileName);
        Assert.Equal($"{UserId:D}/vault/{clip.Id:D}/original.mp4", clip.Key);
    }

    [Fact]
    public async Task A_downgrade_moves_every_clips_expiry_with_notice()
    {
        var clip = VaultClip.StartUpload(UserId, "old.mp4", "video/mp4", 10, Now.AddDays(-100), TimeSpan.FromDays(365));
        _store.GetAccountAsync(UserId, Arg.Any<CancellationToken>()).Returns(VaultAccount.Create(UserId, SubscriptionPlan.Premium, Now.AddDays(-1)));
        _store.AllOfUserAsync(UserId, Arg.Any<CancellationToken>()).Returns([clip]);

        await Plans().SeeAsync(UserId, SubscriptionPlan.Free, Ct);

        Assert.Equal(Now.AddDays(7), clip.ExpiresAt);
        await _store.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_request_the_plan_is_an_override_then_the_last_seen_then_free()
    {
        Assert.Equal(SubscriptionPlan.Free, await Plans().PlanOfAsync(UserId, Ct));

        _store.GetAccountAsync(UserId, Arg.Any<CancellationToken>()).Returns(VaultAccount.Create(UserId, SubscriptionPlan.Premium, Now));
        Assert.Equal(SubscriptionPlan.Premium, await Plans().PlanOfAsync(UserId, Ct));

        _plansOptions.Value.Overrides[UserId.ToString()] = "premium_plus";
        Assert.Equal(SubscriptionPlan.PremiumPlus, await Plans().PlanOfAsync(UserId, Ct));
    }

    // --- Uploading straight to the vault ---

    [Fact]
    public async Task Uploads_go_to_the_vault_bucket_and_are_kept_for_the_plans_time()
    {
        _resolver.CurrentPlan().Returns(SubscriptionPlan.Premium);

        var result = await RequestUpload().Handle(new RequestVaultUploadCommand("Nextbase 0042.MP4", "video/mp4", 300_000_000), Ct);

        _store.Received(1).Add(Arg.Is<VaultClip>(c =>
            c.Id == result.ClipId && c.Status == VaultClipStatus.Uploading && c.Source == VaultClipSource.Upload && c.ExpiresAt == Now.AddDays(365)));
        await _storage.Received(1).CreateUploadAsync(MediaBucket.Vault, Arg.Is<string>(k => k.Contains("/vault/", StringComparison.Ordinal)), "video/mp4", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_full_vault_refuses_more()
    {
        _store.UsedBytesAsync(UserId, Arg.Any<CancellationToken>()).Returns(2 * Gb - 100);

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => RequestUpload().Handle(new RequestVaultUploadCommand("clip.mp4", "video/mp4", 1_000), Ct));

        Assert.Contains("Your vault is full (2 GB on your plan)", error.Message, StringComparison.Ordinal);
        _store.DidNotReceive().Add(Arg.Any<VaultClip>());
    }

    [Fact]
    public void Files_over_the_largest_size_are_refused()
    {
        var result = new RequestVaultUploadCommandValidator(_options).Validate(new RequestVaultUploadCommand("big.mp4", "video/mp4", 3 * Gb));

        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("too big for the vault", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completing_keeps_the_real_size_or_refuses_a_file_that_does_not_fit()
    {
        var clip = Uploading(declared: 1_000);
        _storage.FindAsync(MediaBucket.Vault, clip.Key, Arg.Any<CancellationToken>()).Returns(new StoredFile(1_200, "video/mp4"));
        _store.UsedBytesAsync(UserId, Arg.Any<CancellationToken>()).Returns(1_000);

        var dto = await Complete().Handle(new CompleteVaultUploadCommand(clip.Id), Ct);
        Assert.Equal(1_200, dto.SizeBytes);
        Assert.Equal(VaultClipStatus.Stored, clip.Status);

        var sneaky = Uploading(declared: 1_000);
        _storage.FindAsync(MediaBucket.Vault, sneaky.Key, Arg.Any<CancellationToken>()).Returns(new StoredFile(3 * Gb, "video/mp4"));
        await Assert.ThrowsAsync<ValidationException>(() => Complete().Handle(new CompleteVaultUploadCommand(sneaky.Id), Ct));
        await _storage.Received(1).DeleteAsync(MediaBucket.Vault, Arg.Is<IEnumerable<string>>(k => k.Single() == sneaky.Key), Arg.Any<CancellationToken>());
        _store.Received(1).Remove(sneaky);
    }

    [Fact]
    public async Task Completing_before_the_file_arrives_says_so()
    {
        var clip = Uploading(declared: 1_000);

        await Assert.ThrowsAsync<ValidationException>(() => Complete().Handle(new CompleteVaultUploadCommand(clip.Id), Ct));
    }

    // --- Downloading and deleting ---

    [Fact]
    public async Task Downloads_use_our_own_file_name_and_other_peoples_clips_are_not_found()
    {
        var clip = Stored(UserId);
        var link = await new GetVaultDownloadQueryHandler(_store, _storage, _caller, _clock).Handle(new GetVaultDownloadQuery(clip.Id), Ct);
        Assert.Contains($"name=bad-jockeys-original-{Now:yyyy-MM-dd-HHmm}.mp4", link.Url.ToString(), StringComparison.Ordinal);

        var theirs = Stored(OtherUserId);
        await Assert.ThrowsAsync<NotFoundException>(
            () => new GetVaultDownloadQueryHandler(_store, _storage, _caller, _clock).Handle(new GetVaultDownloadQuery(theirs.Id), Ct));
        await Assert.ThrowsAsync<NotFoundException>(
            () => new DeleteVaultClipCommandHandler(_store, _storage, _caller).Handle(new DeleteVaultClipCommand(theirs.Id), Ct));
    }

    [Fact]
    public async Task Deleting_removes_the_file_then_the_clip()
    {
        var clip = Stored(UserId);

        await new DeleteVaultClipCommandHandler(_store, _storage, _caller).Handle(new DeleteVaultClipCommand(clip.Id), Ct);

        await _storage.Received(1).DeleteAsync(MediaBucket.Vault, Arg.Is<IEnumerable<string>>(k => k.Single() == clip.Key), Arg.Any<CancellationToken>());
        _store.Received(1).Remove(clip);
    }

    // --- Originals of published posts ---

    [Fact]
    public async Task Published_originals_are_copied_into_the_vault()
    {
        var media = ReadyMedia(size: 50_000_000);
        var postId = Guid.NewGuid();
        _store.MediaInVaultAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());

        var failed = await Keeper().KeepAsync([media], postId, Ct);

        Assert.Empty(failed);
        _store.Received(1).Add(Arg.Is<VaultClip>(c =>
            c.MediaId == media.Id && c.PostId == postId && c.Source == VaultClipSource.Post && c.Status == VaultClipStatus.Stored
            && c.SizeBytes == 50_000_000 && c.ExpiresAt == Now.AddDays(30)));
        await _storage.Received(1).CopyInsideAsync(MediaBucket.Originals, media.OriginalKey, MediaBucket.Vault, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Originals_are_not_kept_when_the_vault_is_full_already_has_them_or_came_from_it()
    {
        var full = ReadyMedia(size: 500);
        _store.UsedBytesAsync(UserId, Arg.Any<CancellationToken>()).Returns(2 * Gb);
        var already = ReadyMedia(size: 1);
        var fromVault = ReadyMedia(size: 1);
        fromVault.MarkFromVault(Guid.NewGuid());
        _store.MediaInVaultAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid> { already.Id });

        await Keeper().KeepAsync([full, already, fromVault], null, Ct);

        _store.DidNotReceive().Add(Arg.Any<VaultClip>());
        await _storage.DidNotReceiveWithAnyArgs().CopyInsideAsync(default, default!, default, default!, Ct);
    }

    [Fact]
    public async Task A_failed_copy_keeps_the_original_for_now()
    {
        var media = ReadyMedia(size: 10);
        _store.MediaInVaultAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        _storage.CopyInsideAsync(Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("storage down")));

        var failed = await Keeper().KeepAsync([media], null, Ct);

        Assert.Equal(media.Id, Assert.Single(failed));
        _store.DidNotReceive().Add(Arg.Any<VaultClip>());
    }

    [Fact]
    public async Task With_automatic_saving_off_originals_are_not_kept()
    {
        var media = ReadyMedia(size: 10);
        var account = VaultAccount.Create(UserId, SubscriptionPlan.Premium, Now);
        account.SetAutoSave(false);
        _store.GetAccountAsync(UserId, Arg.Any<CancellationToken>()).Returns(account);
        _store.MediaInVaultAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());

        var failed = await Keeper().KeepAsync([media], Guid.NewGuid(), Ct);

        Assert.Empty(failed);
        _store.DidNotReceive().Add(Arg.Any<VaultClip>());
        await _storage.DidNotReceiveWithAnyArgs().CopyInsideAsync(default, default!, default, default!, Ct);
    }

    // --- Settings and previews ---

    [Fact]
    public void Automatic_saving_is_on_unless_turned_off()
    {
        var account = VaultAccount.Create(UserId, SubscriptionPlan.Free, Now);
        Assert.True(account.AutoSave);

        account.SetAutoSave(false);
        Assert.False(account.AutoSave);

        account.SetAutoSave(true);
        Assert.True(account.AutoSave);
    }

    [Fact]
    public async Task Turning_automatic_saving_off_is_saved_and_reported_with_the_vault()
    {
        var account = VaultAccount.Create(UserId, SubscriptionPlan.Free, Now);
        _store.GetAccountAsync(UserId, Arg.Any<CancellationToken>()).Returns(account);
        _store.ListAsync(UserId, 1, 20, Arg.Any<CancellationToken>()).Returns(((IReadOnlyList<VaultClip>)[], 0));

        var settings = await new SetVaultSettingsCommandHandler(_store, Plans(), _resolver, _caller)
            .Handle(new SetVaultSettingsCommand(false), Ct);
        var vault = await new GetVaultQueryHandler(_store, Plans(), _resolver, _caller, _options).Handle(new GetVaultQuery(), Ct);

        Assert.False(settings.AutoSave);
        Assert.False(vault.AutoSave);
        await _store.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Previews_show_only_your_stored_clips_in_the_page()
    {
        _storage.CreateDownloadUrlAsync(Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri($"https://vault.example.com/{ci.ArgAt<string>(1)}"));
        var mine = Stored(UserId);
        var uploading = Uploading(declared: 10);
        var theirs = Stored(OtherUserId);

        var previews = await new GetVaultPreviewsQueryHandler(_store, _storage, _caller)
            .Handle(new GetVaultPreviewsQuery([mine.Id, uploading.Id, theirs.Id, Guid.NewGuid()]), Ct);

        var preview = Assert.Single(previews);
        Assert.Equal(mine.Id, preview.ClipId);
        Assert.DoesNotContain("name=", preview.Url.ToString(), StringComparison.Ordinal);
        await _storage.DidNotReceive().CreateDownloadUrlAsync(
            Arg.Any<MediaBucket>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Previews_are_asked_for_up_to_fifty_at_a_time()
    {
        var validator = new GetVaultPreviewsQueryValidator();

        Assert.False(validator.Validate(new GetVaultPreviewsQuery([])).IsValid);
        Assert.False(validator.Validate(new GetVaultPreviewsQuery([.. Enumerable.Range(0, 51).Select(_ => Guid.NewGuid())])).IsValid);
        Assert.True(validator.Validate(new GetVaultPreviewsQuery([Guid.NewGuid()])).IsValid);
    }

    // --- The hourly cleanup ---

    [Fact]
    public async Task The_sweep_deletes_clips_past_their_time()
    {
        var expired = Stored(UserId);
        _store.ListDueForDeletionAsync(Now, Now.AddHours(-24), VaultCleanup.SweepBatchSize, Arg.Any<CancellationToken>()).Returns([expired]);

        var deleted = await new VaultCleanup(_store, _storage, Options(), _clock, NullLogger<VaultCleanup>.Instance).SweepAsync(Ct);

        Assert.Equal(1, deleted);
        await _storage.Received(1).DeleteAsync(MediaBucket.Vault, Arg.Is<IEnumerable<string>>(k => k.Single() == expired.Key), Arg.Any<CancellationToken>());
        _store.Received(1).Remove(expired);
    }

    private VaultPlans Plans() => new(_store, _options, _plansOptions, _clock);

    private VaultKeeper Keeper() => new(_store, _storage, Plans(), _options, _clock, NullLogger<VaultKeeper>.Instance);

    private RequestVaultUploadCommandHandler RequestUpload() =>
        new(_store, _storage, Plans(), _resolver, _caller, _options, Options(), _clock);

    private CompleteVaultUploadCommandHandler Complete() => new(_store, _storage, Plans(), _resolver, _caller, _options, _clock);

    private VaultClip Uploading(long declared)
    {
        var clip = VaultClip.StartUpload(UserId, "clip.mp4", "video/mp4", declared, Now, TimeSpan.FromDays(30));
        _store.GetAsync(clip.Id, Arg.Any<CancellationToken>()).Returns(clip);
        return clip;
    }

    private VaultClip Stored(Guid ownerId)
    {
        var clip = VaultClip.StartUpload(ownerId, "clip.mp4", "video/mp4", 10, Now, TimeSpan.FromDays(30));
        clip.MarkStored(10, Now);
        _store.GetAsync(clip.Id, Arg.Any<CancellationToken>()).Returns(clip);
        return clip;
    }

    private static MediaItem ReadyMedia(long size)
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(size, Now, TimeSpan.FromDays(1));
        item.MarkReady(10, 30m, 1280, 720, null, Now);
        return item;
    }
}
