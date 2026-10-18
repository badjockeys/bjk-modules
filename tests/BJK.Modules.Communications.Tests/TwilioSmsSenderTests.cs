using System.Net;
using System.Text;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure.Sms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Communications.Tests;

public sealed class TwilioSmsSenderTests
{
    private const string Created = """{"sid":"SM123","status":"queued","error_code":null}""";

    private static readonly SmsMessage Message = new("+447700900123", "Your Bad Jockeys code is 123456");

    private static (TwilioSmsSender Sender, FakeHandler Handler) Create(Action<TwilioOptions>? configure = null)
    {
        var options = new SmsOptions
        {
            Provider = SmsOptions.TwilioProvider,
            Twilio = { AccountSid = "AC123", AuthToken = "token", FromNumber = "+447700900000" },
        };
        configure?.Invoke(options.Twilio);
        var handler = new FakeHandler();
        var sender = new TwilioSmsSender(
            new HttpClient(handler),
            Options.Create(options),
            TestData.NoDelayRetry(),
            NullLogger<TwilioSmsSender>.Instance);
        return (sender, handler);
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Posts_the_message_to_the_account_messages_endpoint()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.Created, Created);

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC123/Messages.json", request.Uri.ToString());
        Assert.Equal(Basic("AC123", "token"), request.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("To=%2B447700900123&Body=Your+Bad+Jockeys+code+is+123456&From=%2B447700900000", request.Body);
    }

    [Fact]
    public async Task Prefers_an_api_key_and_a_messaging_service_when_configured()
    {
        var (sender, handler) = Create(o =>
        {
            o.ApiKeySid = "SK456";
            o.ApiKeySecret = "key-secret";
            o.MessagingServiceSid = "MG789";
        });
        handler.Reply(HttpStatusCode.Created, Created);

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal(Basic("SK456", "key-secret"), request.Authorization);
        Assert.Contains("MessagingServiceSid=MG789", request.Body);
        Assert.DoesNotContain("From=", request.Body);
    }

    [Theory]
    [InlineData("07700900123")]
    [InlineData("+44 7700 900123")]
    [InlineData("+0123456789")]
    [InlineData("")]
    public async Task Rejects_numbers_that_are_not_e164_without_calling_twilio(string number)
    {
        var (sender, handler) = Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => sender.SendAsync(Message with { To = number }, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Reports_twilio_errors_without_retrying()
    {
        var (sender, handler) = Create();
        handler.Reply(
            HttpStatusCode.BadRequest,
            """{"code":21211,"message":"The 'To' number +447700900123 is not a valid phone number.","more_info":"https://www.twilio.com/docs/errors/21211","status":400}""");

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.False(error.IsTransient);
        Assert.Contains("21211", error.Message);
        Assert.Contains("is not a valid phone number", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Explains_bad_credentials()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.Unauthorized, """{"code":20003,"message":"Authenticate","status":401}""");

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.Contains("Sms:Twilio:AccountSid", error.Message);
    }

    [Fact]
    public async Task Retries_when_twilio_is_unavailable()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.Created, Created);

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Treats_a_timeout_as_transient()
    {
        var (sender, handler) = Create();
        handler
            .Throw(new TaskCanceledException("timeout"))
            .Throw(new TaskCanceledException("timeout"))
            .Throw(new TaskCanceledException("timeout"));

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.True(error.IsTransient);
        Assert.Contains("timed out", error.Message);
    }
}
