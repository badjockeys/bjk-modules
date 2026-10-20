using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
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
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationSender Sender(Uri? appUrl = null) => new(
        _repository,
        _profiles,
        _email,
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
}
