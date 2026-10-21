using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.Infrastructure.Push;
using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Notifications.Tests.TestData;

namespace BJK.Modules.Notifications.Tests.UseCases;

public sealed class PushTests
{
    // RFC 8291 section 5 and appendix A.
    private const string RfcPlaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string RfcSenderPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string RfcSenderPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string RfcBrowserPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string RfcAuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string RfcSalt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string RfcMessage =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT"
        + "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private const string FcmEndpoint = "https://fcm.googleapis.com/fcm/send/abc123";

    private readonly INotificationRepository _repository = Substitute.For<INotificationRepository>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly FixedTimeProvider _clock = new(Now);

    public PushTests() => _caller.IdentityId.Returns(UserId.ToString());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] B(string base64Url) => Base64Url.DecodeFromChars(base64Url);

    [Fact]
    public void Messages_are_encrypted_exactly_as_the_RFC_8291_example()
    {
        var senderPublic = B(RfcSenderPublic);
        using var senderKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B(RfcSenderPrivate),
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });

        var body = WebPushEncryption.Encrypt(B(RfcPlaintext), B(RfcBrowserPublic), B(RfcAuthSecret), senderKey, B(RfcSalt));

        Assert.Equal(RfcMessage, Base64Url.EncodeToString(body));
    }

    [Fact]
    public void Each_message_gets_a_fresh_key_and_salt()
    {
        var plaintext = "hello"u8.ToArray();

        var one = WebPushEncryption.Encrypt(plaintext, B(RfcBrowserPublic), B(RfcAuthSecret));
        var two = WebPushEncryption.Encrypt(plaintext, B(RfcBrowserPublic), B(RfcAuthSecret));

        Assert.Equal(86 + plaintext.Length + 1 + 16, one.Length);
        Assert.NotEqual(one[..86], two[..86]);
    }

    [Fact]
    public void Messages_that_wont_fit_are_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintext + 1], B(RfcBrowserPublic), B(RfcAuthSecret)));
    }

    [Fact]
    public void The_VAPID_token_is_signed_for_the_push_services_origin()
    {
        var (publicKey, privateKey, verifier) = NewKeys();
        using var _ = verifier;
        using var signer = new VapidSigner(new PushOptions { PublicKey = publicKey, PrivateKey = privateKey, Subject = "mailto:support@badjockeys.com" });

        var header = signer.Authorization(new Uri(FcmEndpoint), Now);

        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        Assert.EndsWith($", k={publicKey}", header, StringComparison.Ordinal);
        var token = header["vapid t=".Length..header.IndexOf(',', StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B(parts[2]), HashAlgorithmName.SHA256));

        using var claims = JsonDocument.Parse(B(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:support@badjockeys.com", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(Now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
    }

    [Theory]
    [InlineData(FcmEndpoint, true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA", true)]
    [InlineData("https://web.push.apple.com/QGZ1", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://evil.example.com/fcm.googleapis.com", false)]
    [InlineData("https://push.apple.com.evil.example/x", false)]
    [InlineData("https://user@fcm.googleapis.com/x", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    public void Only_known_push_services_are_ever_called(string endpoint, bool allowed) =>
        Assert.Equal(allowed, PushServices.IsAllowed(endpoint));

    [Fact]
    public void Keys_are_checked()
    {
        Assert.True(PushServices.IsPublicKey(RfcBrowserPublic));
        Assert.True(PushServices.IsAuthSecret(RfcAuthSecret));
        Assert.False(PushServices.IsPublicKey(RfcAuthSecret));
        Assert.False(PushServices.IsAuthSecret("not base64url!"));
        Assert.True(PushKeyChecks.IsPrivateKey(RfcSenderPrivate));
        Assert.False(PushKeyChecks.IsPrivateKey(RfcBrowserPublic));
    }

    [Fact]
    public async Task Devices_the_push_service_says_have_gone_are_forgotten()
    {
        var kept = PushSubscription.Create(UserId, FcmEndpoint, RfcBrowserPublic, RfcAuthSecret, Now);
        var gone = PushSubscription.Create(UserId, FcmEndpoint + "/old", RfcBrowserPublic, RfcAuthSecret, Now);
        _repository.ListPushSubscriptionsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<PushSubscription> { kept, gone });
        var client = Substitute.For<IWebPushClient>();
        client.IsConfigured.Returns(true);
        client.SendAsync(Arg.Is(kept), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(PushOutcome.Sent);
        client.SendAsync(Arg.Is(gone), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(PushOutcome.Gone);

        await new PushSender(_repository, client, NullLogger<PushSender>.Instance)
            .SendAsync(UserId, new PushMessage("Bad Jockeys", "Hello", "/notifications", "x"), Ct);

        _repository.Received(1).RemovePushSubscription(gone);
        _repository.DidNotReceive().RemovePushSubscription(kept);
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_client_sends_an_encrypted_signed_message_and_reports_gone_devices()
    {
        var (publicKey, privateKey, verifier) = NewKeys();
        verifier.Dispose();
        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var options = Options.Create(new NotificationsOptions { Push = new PushOptions { PublicKey = publicKey, PrivateKey = privateKey } });
        using var client = new WebPushClient(http, options, _clock, NullLogger<WebPushClient>.Instance);
        var device = PushSubscription.Create(UserId, FcmEndpoint, RfcBrowserPublic, RfcAuthSecret, Now);

        Assert.Equal(PushOutcome.Sent, await client.SendAsync(device, "{}"u8.ToArray(), Ct));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(FcmEndpoint, sent.Uri);
        Assert.Equal("aes128gcm", sent.Encoding);
        Assert.Equal("43200", sent.Ttl);
        Assert.StartsWith("vapid t=", sent.Authorization, StringComparison.Ordinal);
        Assert.Equal(86 + 2 + 1 + 16, sent.BodyLength);

        handler.Status = HttpStatusCode.Gone;
        Assert.Equal(PushOutcome.Gone, await client.SendAsync(device, "{}"u8.ToArray(), Ct));

        // Never anywhere but a push service.
        var elsewhere = PushSubscription.Create(UserId, "https://example.com/hook", RfcBrowserPublic, RfcAuthSecret, Now);
        Assert.Equal(PushOutcome.Failed, await client.SendAsync(elsewhere, "{}"u8.ToArray(), Ct));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Without_keys_push_is_off()
    {
        var setup = await new GetPushSetupQueryHandler(Options.Create(new NotificationsOptions())).Handle(new GetPushSetupQuery(), Ct);

        Assert.False(setup.Available);
        Assert.Null(setup.PublicKey);
    }

    [Fact]
    public async Task Placeholder_or_broken_keys_turn_push_off_instead_of_failing()
    {
        var options = Options.Create(new NotificationsOptions
        {
            Push = new PushOptions { PublicKey = "<set by the setup>", PrivateKey = "<set by the setup>" },
        });
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.Created));
        using var client = new WebPushClient(http, options, _clock, NullLogger<WebPushClient>.Instance);

        Assert.False(client.IsConfigured);
        Assert.False((await new GetPushSetupQueryHandler(options).Handle(new GetPushSetupQuery(), Ct)).Available);
    }

    [Fact]
    public async Task Subscribing_adds_the_device_and_replaces_the_oldest_beyond_ten()
    {
        var mine = Enumerable.Range(0, PushSubscription.MaxPerUser)
            .Select(i => PushSubscription.Create(UserId, $"{FcmEndpoint}{i}", RfcBrowserPublic, RfcAuthSecret, Now.AddDays(i - 20)))
            .ToList();
        _repository.ListPushSubscriptionsAsync(UserId, Arg.Any<CancellationToken>()).Returns(mine);

        await new SubscribePushCommandHandler(_repository, _caller, _clock)
            .Handle(new SubscribePushCommand(FcmEndpoint, RfcBrowserPublic, RfcAuthSecret), Ct);

        _repository.Received(1).RemovePushSubscription(mine[0]);
        _repository.Received(1).AddPushSubscription(Arg.Is<PushSubscription>(s => s.Endpoint == FcmEndpoint && s.UserId == UserId));
    }

    [Fact]
    public async Task Subscribing_the_same_browser_again_makes_it_yours()
    {
        var existing = PushSubscription.Create(SamId, FcmEndpoint, RfcBrowserPublic, RfcAuthSecret, Now.AddDays(-1));
        _repository.GetPushSubscriptionAsync(FcmEndpoint, Arg.Any<CancellationToken>()).Returns(existing);

        await new SubscribePushCommandHandler(_repository, _caller, _clock)
            .Handle(new SubscribePushCommand(FcmEndpoint, RfcBrowserPublic, RfcAuthSecret), Ct);

        Assert.Equal(UserId, existing.UserId);
        _repository.DidNotReceiveWithAnyArgs().AddPushSubscription(null!);
    }

    [Fact]
    public async Task You_can_only_unsubscribe_your_own_devices()
    {
        var sams = PushSubscription.Create(SamId, FcmEndpoint, RfcBrowserPublic, RfcAuthSecret, Now);
        _repository.GetPushSubscriptionAsync(FcmEndpoint, Arg.Any<CancellationToken>()).Returns(sams);

        await new UnsubscribePushCommandHandler(_repository, _caller).Handle(new UnsubscribePushCommand(FcmEndpoint), Ct);

        _repository.DidNotReceiveWithAnyArgs().RemovePushSubscription(null!);
    }

    [Fact]
    public void Subscriptions_to_unknown_services_or_with_bad_keys_are_refused()
    {
        var validator = new SubscribePushCommandValidator();

        Assert.True(validator.Validate(new SubscribePushCommand(FcmEndpoint, RfcBrowserPublic, RfcAuthSecret)).IsValid);
        Assert.False(validator.Validate(new SubscribePushCommand("https://example.com/x", RfcBrowserPublic, RfcAuthSecret)).IsValid);
        Assert.False(validator.Validate(new SubscribePushCommand(FcmEndpoint, RfcAuthSecret, RfcAuthSecret)).IsValid);
    }

    [Theory]
    [InlineData(2026, 10, 20, 20, 59, false)] // 21:59 UK (BST)
    [InlineData(2026, 10, 20, 21, 0, true)] // 22:00 UK
    [InlineData(2026, 10, 21, 5, 59, true)] // 06:59 UK
    [InlineData(2026, 10, 21, 6, 0, false)] // 07:00 UK
    [InlineData(2026, 12, 1, 6, 30, true)] // 06:30 UK (GMT)
    [InlineData(2026, 12, 1, 7, 0, false)] // 07:00 UK (GMT)
    public void Quiet_hours_follow_UK_time(int year, int month, int day, int hour, int minute, bool quiet) =>
        Assert.Equal(quiet, PushQuietHours.IsQuiet(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero)));

    private static (string PublicKey, string PrivateKey, ECDsa Verifier) NewKeys()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return (
            Base64Url.EncodeToString(WebPushEncryption.PublicKeyBytes(parameters)),
            Base64Url.EncodeToString(parameters.D!),
            key);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        public List<(string Uri, string Encoding, string Ttl, string Authorization, int BodyLength)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((
                request.RequestUri!.ToString(),
                string.Join(",", request.Content.Headers.ContentEncoding),
                request.Headers.GetValues("TTL").Single(),
                request.Headers.GetValues("Authorization").Single(),
                body.Length));
            return new HttpResponseMessage(Status);
        }
    }
}
