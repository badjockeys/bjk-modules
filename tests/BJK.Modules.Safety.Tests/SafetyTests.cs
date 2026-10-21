using System.Net;
using System.Security.Claims;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BJK.Modules.Safety.Tests;

public sealed class SafetyTests
{
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SafetyTests()
    {
        _caller.IdentityId.Returns(Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task Writes_need_the_age_check_when_it_is_required()
    {
        var behaviour = Behaviour<Write>(required: true);

        await Assert.ThrowsAsync<AgeVerificationRequiredException>(() => behaviour.Handle(new Write(), Next, Ct));

        _caller.AgeVerified.Returns(true);
        Assert.Equal("done", await behaviour.Handle(new Write(), Next, Ct));
    }

    [Fact]
    public async Task The_age_check_can_be_switched_off_and_doesnt_apply_to_other_requests()
    {
        Assert.Equal("done", await Behaviour<Write>(required: false).Handle(new Write(), Next, Ct));
        Assert.Equal("done", await Behaviour<Read>(required: true).Handle(new Read(), Next, Ct));
    }

    [Fact]
    public async Task Anonymous_callers_are_left_to_the_handler()
    {
        _caller.IdentityId.Returns((string?)null);

        Assert.Equal("done", await Behaviour<Write>(required: true).Handle(new Write(), Next, Ct));
    }

    [Fact]
    public void The_age_verified_claim_must_be_true()
    {
        static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

        Assert.True(User(new Claim("age_verified", "true")).IsAgeVerified());
        Assert.False(User(new Claim("age_verified", "false")).IsAgeVerified());
        Assert.False(User().IsAgeVerified());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("age_verified", "true") })).IsAgeVerified());
    }

    [Fact]
    public void Minor_claim_is_read_from_the_token()
    {
        static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

        Assert.True(User(new Claim("minor", "true")).IsMinor());
        Assert.False(User(new Claim("minor", "false")).IsMinor());
        Assert.False(User().IsMinor());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("minor", "true") })).IsMinor());
    }

    [Fact]
    public void Safety_defaults_no_longer_require_the_age_check_to_write_and_limit_new_accounts()
    {
        var options = new SafetyOptions();

        // Since 19 Oct 2026 the age check is only for viewing sensitive posts (13+ can post, comment and react).
        Assert.False(options.RequireAgeVerification);
        Assert.Equal(TimeSpan.FromHours(24), options.NewAccounts.Period);
        Assert.Equal(3, options.NewAccounts.MaxPosts);
        Assert.Equal(20, options.NewAccounts.MaxComments);
    }

    [Fact]
    public async Task Turnstile_tokens_are_checked_with_cloudflare()
    {
        var handler = new FakeHandler().Reply(HttpStatusCode.OK, """{"success":true}""").Reply(HttpStatusCode.OK, """{"success":false,"error-codes":["invalid-input-response"]}""");
        var verifier = Turnstile(handler);

        Assert.Equal(CaptchaResult.Passed, await verifier.VerifyAsync("token-1", "203.0.113.9", Ct));
        Assert.Equal(CaptchaResult.Failed, await verifier.VerifyAsync("token-2", null, Ct));
        Assert.Contains("remoteip=203.0.113.9", handler.Requests[0].Body);
        Assert.Contains("secret=test-secret", handler.Requests[0].Body);
    }

    [Fact]
    public async Task An_unreachable_turnstile_is_reported_as_unavailable()
    {
        var verifier = Turnstile(new FakeHandler().Reply(HttpStatusCode.BadGateway));

        Assert.Equal(CaptchaResult.Unavailable, await verifier.VerifyAsync("token", null, Ct));
    }

    [Fact]
    public void Turnstile_needs_a_secret()
    {
        var validator = new CaptchaOptionsValidator();

        Assert.True(validator.Validate(null, new CaptchaOptions()).Failed);
        Assert.True(validator.Validate(null, new CaptchaOptions { TurnstileSecretKey = "x" }).Succeeded);
    }

    [Fact]
    public void The_audit_role_comes_from_the_caller()
    {
        _caller.HasRole(StaffRoles.Admin).Returns(true);
        Assert.Equal("Admin", ModerationActor.RoleOf(_caller));

        _caller.HasRole(StaffRoles.Admin).Returns(false);
        Assert.Equal("Moderator", ModerationActor.RoleOf(_caller));

        _caller.IdentityId.Returns((string?)null);
        Assert.Equal("Service", ModerationActor.RoleOf(_caller));
    }

    private static Task<string> Next(CancellationToken cancellationToken) => Task.FromResult("done");

    private AgeVerificationBehaviour<T, string> Behaviour<T>(bool required)
        where T : notnull =>
        new(_caller, Options.Create(new SafetyOptions { RequireAgeVerification = required }));

    private static TurnstileCaptchaVerifier Turnstile(FakeHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new CaptchaOptions { TurnstileSecretKey = "test-secret" }),
            NullLogger<TurnstileCaptchaVerifier>.Instance);

    public sealed record Write : IRequest<string>, IRequiresAgeVerification;

    public sealed record Read : IRequest<string>;
}
