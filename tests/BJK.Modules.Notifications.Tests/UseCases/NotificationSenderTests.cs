using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.Infrastructure.Push;
using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static BJK.Modules.Notifications.Tests.TestData;

namespace BJK.Modules.Notifications.Tests.UseCases;

public sealed class NotificationSenderTests
{
    private readonly INotificationRepository _repository = Substitute.For<INotificationRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IPushSender _push = Substitute.For<IPushSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly List<Notification> _added = [];
    private readonly List<NotificationSetting> _settings = [];

    public NotificationSenderTests()
    {
        _repository.When(r => r.Add(Arg.Any<Notification>())).Do(c => _added.Add(c.Arg<Notification>()));
        _repository.GetSettingsAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => _settings.ToList());
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [SamId] = new(SamId, "sam_j", "Sam J", null) });
        _caller.IdentityId.Returns(UserId.ToString());
        _push.IsEnabled.Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationSender Sender(Uri? appUrl = null) => new(
        _repository,
        _profiles,
        _email,
        _push,
        Options.Create(new EmailOptions()),
        Options.Create(new NotificationsOptions { AppUrl = appUrl }),
        _clock,
        NullLogger<NotificationSender>.Instance);

    [Fact]
    public async Task A_comment_goes_in_the_bell_and_isnt_emailed_by_default()
    {
        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId, PostTitle: "Tailgated"), Ct);

        var saved = Assert.Single(_added);
        Assert.Equal(NotificationType.CommentOnPost, saved.Type);
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nobody_is_notified_about_their_own_actions()
    {
        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.ReactionOnPost, UserId, PostId), Ct);

        Assert.Empty(_added);
    }

    [Fact]
    public async Task Passenger_tags_are_emailed_to_the_confirmed_address_with_a_link()
    {
        _repository.GetContactAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(NotificationContact.Create(UserId, "roadhog@example.com", Now));

        await Sender(new Uri("https://badjockeys.com")).NotifyAsync(
            new NotificationRequest(UserId, NotificationType.PassengerTagged, SamId, PostId, PostTitle: "Tailgated on the A57"),
            Ct);

        Assert.Single(_added);
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.To == "roadhog@example.com"
                && m.Subject == "Sam J tagged you as a passenger on Bad Jockeys"
                && m.TextBody.Contains($"https://badjockeys.com/p/{PostId:D}")
                && m.TextBody.Contains("Tailgated on the A57")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_email_without_a_confirmed_address()
    {
        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.PassengerTagged, SamId, PostId), Ct);

        Assert.Single(_added);
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Settings_turn_the_bell_and_email_on_or_off()
    {
        _settings.Add(NotificationSetting.Create(UserId, NotificationType.CommentOnPost, inApp: false, email: false));

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Ct);

        Assert.Empty(_added);
    }

    [Fact]
    public async Task Reactions_to_a_post_are_grouped_while_unread()
    {
        var first = Notification.Create(new NotificationRequest(UserId, NotificationType.ReactionOnPost, Guid.NewGuid(), PostId), Now);
        _repository.FindUnreadAsync(UserId, NotificationType.ReactionOnPost, PostId, Arg.Any<CancellationToken>()).Returns(first);

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.ReactionOnPost, SamId, PostId), Ct);

        Assert.Empty(_added);
        Assert.Equal(2, first.Count);
        Assert.Equal(SamId, first.ActorId);
        Assert.Equal("Sam J and 1 other reacted to your post “Title”.", NotificationTexts.Sentence(first.Type, "Sam J", first.Count, "Title"));
    }

    [Fact]
    public async Task A_failure_never_reaches_the_caller()
    {
        _repository.GetSettingsAsync(UserId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Ct);
    }

    [Fact]
    public async Task Checking_the_bell_keeps_the_email_address_up_to_date()
    {
        _caller.EmailVerified.Returns(true);
        _caller.Email.Returns("roadhog@example.com");
        var handler = new GetUnreadCountQueryHandler(_repository, _caller, _clock);

        await handler.Handle(new GetUnreadCountQuery(), Ct);
        _repository.Received(1).AddContact(Arg.Is<NotificationContact>(c => c.Email == "roadhog@example.com"));

        // No longer confirmed (changed in the identity server): forgotten.
        var saved = NotificationContact.Create(UserId, "roadhog@example.com", Now);
        _repository.GetContactAsync(UserId, Arg.Any<CancellationToken>()).Returns(saved);
        _caller.EmailVerified.Returns(false);
        await handler.Handle(new GetUnreadCountQuery(), Ct);
        _repository.Received(1).RemoveContact(saved);
    }

    [Fact]
    public async Task Settings_show_defaults_until_changed()
    {
        var settings = await new GetNotificationSettingsQueryHandler(_repository, _caller).Handle(new GetNotificationSettingsQuery(), Ct);

        var tagged = settings.Single(s => s.Type == NotificationType.PassengerTagged);
        Assert.True(tagged.InApp);
        Assert.True(tagged.Email);
        Assert.False(settings.Single(s => s.Type == NotificationType.CommentOnPost).Email);

        var updated = await new UpdateNotificationSettingsCommandHandler(_repository, _caller).Handle(
            new UpdateNotificationSettingsCommand([new NotificationSettingChange(NotificationType.CommentOnPost, true, true)]),
            Ct);
        Assert.True(updated.Single(s => s.Type == NotificationType.CommentOnPost).Email);
    }

    [Fact]
    public async Task Comments_are_pushed_to_the_persons_devices_by_default()
    {
        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId, PostTitle: "Tailgated"), Ct);

        await _push.Received(1).SendAsync(
            UserId,
            Arg.Is<PushMessage>(m => m.Body == "Sam J commented on your post “Tailgated”." && m.Url == $"/p/{PostId:D}"),
            Arg.Any<CancellationToken>());
        Assert.Equal(Now, Assert.Single(_added).LastPushedAt);
    }

    [Fact]
    public async Task Nothing_is_pushed_overnight_but_it_still_goes_in_the_bell()
    {
        _clock.Now = new DateTimeOffset(2026, 10, 20, 22, 30, 0, TimeSpan.Zero); // 23:30 UK

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Ct);

        Assert.Single(_added);
        await _push.DidNotReceiveWithAnyArgs().SendAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Push_can_be_turned_off_per_type()
    {
        _settings.Add(NotificationSetting.Create(UserId, NotificationType.CommentOnPost, inApp: true, email: false, push: false));

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Ct);

        Assert.Single(_added);
        await _push.DidNotReceiveWithAnyArgs().SendAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Grouped_reactions_are_pushed_at_most_hourly()
    {
        var first = Notification.Create(new NotificationRequest(UserId, NotificationType.ReactionOnPost, Guid.NewGuid(), PostId), Now);
        first.MarkPushed(Now.AddMinutes(-20));
        _repository.FindUnreadAsync(UserId, NotificationType.ReactionOnPost, PostId, Arg.Any<CancellationToken>()).Returns(first);

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.ReactionOnPost, SamId, PostId), Ct);
        await _push.DidNotReceiveWithAnyArgs().SendAsync(default, null!, Ct);

        _clock.Now = Now.AddMinutes(45);
        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.ReactionOnPost, SamId, PostId), Ct);
        await _push.Received(1).SendAsync(
            UserId,
            Arg.Is<PushMessage>(m => m.Body.StartsWith("Sam J and 2 others reacted", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_push_doesnt_stop_the_notification()
    {
        _push.SendAsync(default, null!, Ct).ReturnsForAnyArgs(Task.FromException(new HttpRequestException("down")));

        await Sender().NotifyAsync(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Ct);

        Assert.Single(_added);
    }

    [Fact]
    public void Vote_results_and_followed_posts_have_their_own_words()
    {
        Assert.Equal(
            "The vote on “Tailgated” has closed. Most said the other road user (62% of 41 votes).",
            NotificationTexts.Sentence(NotificationType.VoteClosed, null, 1, "Tailgated", "Most said the other road user (62% of 41 votes)."));
        Assert.Equal(
            "Sam J commented on “Tailgated”, which you follow.",
            NotificationTexts.Sentence(NotificationType.CommentOnFollowedPost, "Sam J", 1, "Tailgated"));
        Assert.Contains(NotificationType.VoteClosed, NotificationDefaults.Configurable);
        Assert.Contains(NotificationType.CommentOnFollowedPost, NotificationDefaults.Configurable);
    }

    [Fact]
    public async Task Settings_include_push_and_keep_it_when_a_change_leaves_it_out()
    {
        var updated = await new UpdateNotificationSettingsCommandHandler(_repository, _caller).Handle(
            new UpdateNotificationSettingsCommand([new NotificationSettingChange(NotificationType.CommentOnPost, true, false, Push: false)]),
            Ct);
        Assert.False(updated.Single(s => s.Type == NotificationType.CommentOnPost).Push);
        Assert.True(updated.Single(s => s.Type == NotificationType.ReplyToComment).Push);

        _settings.Add(NotificationSetting.Create(UserId, NotificationType.ReplyToComment, inApp: true, email: false, push: false));
        updated = await new UpdateNotificationSettingsCommandHandler(_repository, _caller).Handle(
            new UpdateNotificationSettingsCommand([new NotificationSettingChange(NotificationType.ReplyToComment, true, true)]),
            Ct);
        Assert.False(updated.Single(s => s.Type == NotificationType.ReplyToComment).Push);
    }
}
