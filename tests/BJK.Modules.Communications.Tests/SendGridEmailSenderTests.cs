using System.Net;
using System.Text.Json;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace BJK.Modules.Communications.Tests;

public sealed class SendGridEmailSenderTests
{
    private static readonly EmailMessage Message = new("rider.one@example.com", "Confirm your email", "Plain text", "<p>Html</p>");

    private static (SendGridEmailSender Sender, FakeHandler Handler) Create(Action<EmailOptions>? configure = null)
    {
        var options = new EmailOptions
        {
            Provider = EmailOptions.SendGridProvider,
            FromAddress = "no-reply@badjockeys.com",
            FromName = "Bad Jockeys",
            SendGrid = { ApiKey = "SG.test-key" },
        };
        configure?.Invoke(options);
        var handler = new FakeHandler();
        var sender = new SendGridEmailSender(
            new HttpClient(handler),
            Options.Create(options),
            TestData.NoDelayRetry(),
            NullLogger<SendGridEmailSender>.Instance);
        return (sender, handler);
    }

    [Fact]
    public async Task Posts_the_email_to_the_mail_send_api_with_the_api_key()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.Accepted, configure: r => r.Headers.Add("X-Message-Id", "abc123"));

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", request.Uri.ToString());
        Assert.Equal("Bearer SG.test-key", request.Authorization);
        Assert.Equal("application/json", request.ContentType);

        using var json = JsonDocument.Parse(request.Body!);
        var root = json.RootElement;
        Assert.Equal("rider.one@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("no-reply@badjockeys.com", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("Bad Jockeys", root.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("Confirm your email", root.GetProperty("subject").GetString());

        var content = root.GetProperty("content");
        Assert.Equal("text/plain", content[0].GetProperty("type").GetString());
        Assert.Equal("Plain text", content[0].GetProperty("value").GetString());
        Assert.Equal("text/html", content[1].GetProperty("type").GetString());

        // Links carry codes, so they must not be rewritten through SendGrid's click tracker.
        Assert.False(root.GetProperty("tracking_settings").GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.False(root.TryGetProperty("mail_settings", out _));
        Assert.False(root.TryGetProperty("reply_to", out _));
    }

    [Fact]
    public async Task Sends_text_only_when_there_is_no_html()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.Accepted);

        await sender.SendAsync(Message with { HtmlBody = null }, TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(1, json.RootElement.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public async Task Uses_sandbox_mode_reply_to_and_the_eu_endpoint_when_configured()
    {
        var (sender, handler) = Create(o =>
        {
            o.SendGrid.SandboxMode = true;
            o.SendGrid.BaseUrl = new Uri("https://api.eu.sendgrid.com");
            o.ReplyToAddress = "support@badjockeys.com";
        });
        handler.Reply(HttpStatusCode.OK);

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal("https://api.eu.sendgrid.com/v3/mail/send", request.Uri.ToString());
        using var json = JsonDocument.Parse(request.Body!);
        Assert.True(json.RootElement.GetProperty("mail_settings").GetProperty("sandbox_mode").GetProperty("enable").GetBoolean());
        Assert.Equal("support@badjockeys.com", json.RootElement.GetProperty("reply_to").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Retries_server_errors_and_rate_limits_then_succeeds()
    {
        var (sender, handler) = Create();
        handler
            .Reply(HttpStatusCode.ServiceUnavailable)
            .Reply(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero))
            .Reply(HttpStatusCode.Accepted);

        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_three_attempts_with_a_transient_failure()
    {
        var (sender, handler) = Create();
        handler
            .Throw(new HttpRequestException("connection refused"))
            .Reply(HttpStatusCode.BadGateway)
            .Reply(HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.True(error.IsTransient);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_rejected_email_and_reports_why()
    {
        var (sender, handler) = Create();
        handler.Reply(
            HttpStatusCode.BadRequest,
            """{"errors":[{"message":"The from address does not match a verified Sender Identity.","field":"from"}]}""");

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.False(error.IsTransient);
        Assert.Contains("from: The from address does not match a verified Sender Identity.", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Explains_a_bad_api_key()
    {
        var (sender, handler) = Create();
        handler.Reply(HttpStatusCode.Unauthorized, """{"errors":[{"message":"The provided authorization grant is invalid."}]}""");

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(
            () => sender.SendAsync(Message, TestContext.Current.CancellationToken));

        Assert.Contains("Email:SendGrid:ApiKey", error.Message);
    }
}
