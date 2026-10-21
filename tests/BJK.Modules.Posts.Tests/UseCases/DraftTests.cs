using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Drafts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class DraftTests
{
    private readonly IDraftRepository _drafts = Substitute.For<IDraftRepository>();
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly List<PostDraft> _saved = [];

    public DraftTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _drafts.When(d => d.Add(Arg.Any<PostDraft>())).Do(c => _saved.Add(c.Arg<PostDraft>()));
        _drafts.When(d => d.Remove(Arg.Any<PostDraft>())).Do(c => _saved.Remove(c.Arg<PostDraft>()));
        _drafts.CountForAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => _saved.Count);
        _drafts.MediaInOtherDraftsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<IReadOnlyCollection<Guid>>().Select(id => Photo(id, UserId)).ToList());
        _media.GetWithSensitiveLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<IReadOnlyCollection<Guid>>().Select(id => Photo(id, UserId)).ToList());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_half_finished_post_is_saved_with_its_photos_and_expires_after_30_days()
    {
        var photos = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var draft = await Save(Draft() with
        {
            Title = "  Parked across the bus stop ",
            Types = [IncidentCategory.BadParking, IncidentCategory.Other],
            Passengers = ["@Sam_J", "sam_j", " "],
            MediaIds = photos,
        });

        Assert.Equal("Parked across the bus stop", draft.Title);
        Assert.Equal(new[] { IncidentCategory.BadParking, IncidentCategory.Other }, draft.Types);
        Assert.Equal(new[] { "Sam_J" }, draft.Passengers);
        Assert.Equal(photos, draft.Media.Select(m => m.MediaId));
        Assert.Equal(Now.AddDays(30), draft.ExpiresAt);
        Assert.Single(_saved);
        await _drafts.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Up_to_five_drafts_each()
    {
        for (var i = 0; i < PostDraft.MaxPerAuthor; i++)
        {
            await Save(Draft());
        }

        var error = await Assert.ThrowsAsync<ValidationException>(() => Save(Draft()));

        Assert.Equal(SaveDraftCommandHandler.LimitMessage, Assert.Single(error.Errors).ErrorMessage);
        Assert.Equal(5, _saved.Count);
    }

    [Fact]
    public async Task Saving_over_a_draft_replaces_it_keeps_photo_rows_and_restarts_its_30_days()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var existing = PostDraft.Create(UserId, Content([a, b]), Now.AddDays(-10));
        _drafts.GetAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        var keptRow = existing.Media.Single(m => m.MediaId == b);

        var draft = await Save(Draft() with { Title = "Changed", MediaIds = [b, c], DraftId = existing.Id });

        Assert.Equal("Changed", draft.Title);
        Assert.Equal(new[] { b, c }, draft.Media.Select(m => m.MediaId));
        Assert.Same(keptRow, existing.Media.Single(m => m.MediaId == b));
        Assert.Equal(0, keptRow.Position);
        Assert.Equal(Now.AddDays(30), draft.ExpiresAt);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Someone_elses_draft_looks_missing()
    {
        var theirs = PostDraft.Create(OtherUserId, Content([]), Now);
        _drafts.GetAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);

        await Assert.ThrowsAsync<NotFoundException>(() => Save(Draft() with { DraftId = theirs.Id }));
        await Assert.ThrowsAsync<NotFoundException>(
            () => new GetDraftQueryHandler(_drafts, new DraftViews(_media), _caller).Handle(new GetDraftQuery(theirs.Id), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => DeleteHandler().Handle(new DeleteDraftCommand(theirs.Id), Ct));
    }

    [Fact]
    public async Task Photos_must_be_your_own_and_not_already_posted()
    {
        var theirs = Guid.NewGuid();
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary> { Photo(theirs, OtherUserId) });
        await Assert.ThrowsAsync<ValidationException>(() => Save(Draft() with { MediaIds = [theirs] }));

        var posted = Guid.NewGuid();
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary> { Photo(posted, UserId) });
        _posts.AnyUseMediaAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        var error = await Assert.ThrowsAsync<ValidationException>(() => Save(Draft() with { MediaIds = [posted] }));
        Assert.Equal("A photo or video is already in a post.", Assert.Single(error.Errors).ErrorMessage);
    }

    [Fact]
    public void An_empty_draft_isnt_saved_and_lengths_are_capped()
    {
        var validator = new SaveDraftCommandValidator();

        var empty = validator.Validate(Draft() with { Title = "  " });
        Assert.Equal(SaveDraftCommandValidator.EmptyMessage, Assert.Single(empty.Errors).ErrorMessage);

        Assert.False(validator.Validate(Draft() with { Title = new string('a', Post.TitleMaxLength + 1) }).IsValid);
        Assert.False(validator.Validate(Draft() with { Description = new string('a', Post.DescriptionInputMaxLength + 1) }).IsValid);
        Assert.False(validator.Validate(Draft() with { Passengers = ["a", "b", "c", "d", "e"] }).IsValid);
        Assert.False(validator.Validate(Draft() with { MediaIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList() }).IsValid);
        Assert.True(validator.Validate(Draft() with { Title = "Just a title" }).IsValid);
    }

    [Fact]
    public async Task Deleting_a_draft_deletes_the_photos_nothing_else_uses()
    {
        var (only, inPost, inOtherDraft) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = PostDraft.Create(UserId, Content([only, inPost, inOtherDraft]), Now);
        _drafts.GetAsync(draft.Id, Arg.Any<CancellationToken>()).Returns(draft);
        _posts.AnyUseMediaAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(inPost)), Arg.Any<CancellationToken>()).Returns(true);
        _drafts.MediaInOtherDraftsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { inOtherDraft });

        await DeleteHandler().Handle(new DeleteDraftCommand(draft.Id), Ct);

        _drafts.Received(1).Remove(draft);
        await _media.Received(1).DeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { only })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drafts_list_last_saved_first_without_deleted_photos()
    {
        var (kept, gone) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = PostDraft.Create(UserId, Content([kept, gone]), Now);
        _drafts.ListForAuthorAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<PostDraft> { draft });
        _media.GetWithSensitiveLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaSummary> { Photo(kept, UserId), Photo(gone, UserId) with { Status = MediaStatus.Deleted } });

        var list = await new ListDraftsQueryHandler(_drafts, new DraftViews(_media), _caller).Handle(new ListDraftsQuery(), Ct);

        var media = Assert.Single(Assert.Single(list).Media);
        Assert.Equal(kept, media.MediaId);
        Assert.NotNull(media.ThumbnailUrl);
    }

    [Fact]
    public async Task Photos_taken_out_of_a_draft_are_deleted_unless_a_post_or_another_draft_uses_them()
    {
        Guid kept = Guid.NewGuid(), dropped = Guid.NewGuid(), inPost = Guid.NewGuid(), inOtherDraft = Guid.NewGuid();
        var draft = PostDraft.Create(UserId, Content([kept, dropped, inPost, inOtherDraft]), Now);
        _drafts.GetAsync(draft.Id, Arg.Any<CancellationToken>()).Returns(draft);
        _posts.AnyUseMediaAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(inPost)), Arg.Any<CancellationToken>())
            .Returns(true);
        _drafts.MediaInOtherDraftsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { inOtherDraft });

        var saved = await Save(Draft() with { DraftId = draft.Id, MediaIds = [kept] });

        Assert.Equal(new[] { kept }, saved.Media.Select(m => m.MediaId));
        await _media.Received(1).DeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(dropped)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_a_draft_without_taking_anything_out_deletes_nothing()
    {
        var photo = Guid.NewGuid();
        var draft = PostDraft.Create(UserId, Content([photo]), Now);
        _drafts.GetAsync(draft.Id, Arg.Any<CancellationToken>()).Returns(draft);

        await Save(Draft() with { DraftId = draft.Id, MediaIds = [photo, Guid.NewGuid()] });

        await _media.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
    }

    private async Task<DraftDto> Save(SaveDraftCommand command) =>
        await new SaveDraftCommandHandler(
            _drafts,
            _posts,
            _media,
            new DraftViews(_media),
            new DraftDeletion(_drafts, _posts, _media, NullLogger<DraftDeletion>.Instance),
            _caller,
            _clock).Handle(command, Ct);

    private DeleteDraftCommandHandler DeleteHandler() =>
        new(new DraftDeletion(_drafts, _posts, _media, NullLogger<DraftDeletion>.Instance), _drafts, _caller);

    private static SaveDraftCommand Draft() => new(
        "Overtook on a blind bend", null, null, null, null, null, null, false, null, null, null, null, null, null, null);

    private static DraftContent Content(IReadOnlyList<Guid> mediaIds) =>
        new("Title", null, [IncidentCategory.Speeding], null, null, null, null, false, [], null, null, null, null, null, mediaIds);

    private static MediaSummary Photo(Guid id, Guid owner) => new(
        id,
        owner,
        MediaType.Image,
        MediaStatus.Ready,
        new Uri($"https://media.example.com/{id:D}.webp"),
        new Uri($"https://media.example.com/{id:D}-t.webp"),
        null,
        2048,
        1536);
}
