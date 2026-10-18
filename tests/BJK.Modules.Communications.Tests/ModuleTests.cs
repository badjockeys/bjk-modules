using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure;
using BJK.Modules.Communications.Infrastructure.Email;
using BJK.Modules.Communications.Infrastructure.Sms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

using Xunit;

namespace BJK.Modules.Communications.Tests;

public sealed class ModuleTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        new CommunicationsModule().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_to_the_logging_providers()
    {
        using var provider = Build([]);

        Assert.IsType<LoggingEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.IsType<LoggingSmsSender>(provider.GetRequiredService<ISmsSender>());
    }

    [Fact]
    public void Uses_sendgrid_and_twilio_when_configured()
    {
        using var provider = Build(new()
        {
            ["Email:Provider"] = "SendGrid",
            ["Email:FromAddress"] = "no-reply@badjockeys.com",
            ["Email:SendGrid:ApiKey"] = "SG.key",
            ["Sms:Provider"] = "Twilio",
            ["Sms:Twilio:AccountSid"] = "AC123",
            ["Sms:Twilio:AuthToken"] = "token",
            ["Sms:Twilio:MessagingServiceSid"] = "MG123",
        });

        Assert.IsType<SendGridEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.IsType<TwilioSmsSender>(provider.GetRequiredService<ISmsSender>());
    }

    [Fact]
    public void Fails_fast_when_sendgrid_is_missing_its_key_and_sender()
    {
        using var provider = Build(new() { ["Email:Provider"] = "SendGrid" });

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);

        Assert.Contains(error.Failures, f => f.Contains("Email:SendGrid:ApiKey"));
        Assert.Contains(error.Failures, f => f.Contains("Email:FromAddress"));
    }

    [Fact]
    public void Fails_fast_when_twilio_has_no_credentials_or_sender()
    {
        using var provider = Build(new() { ["Sms:Provider"] = "Twilio", ["Sms:Twilio:AccountSid"] = "AC123" });

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmsOptions>>().Value);

        Assert.Contains(error.Failures, f => f.Contains("AuthToken"));
        Assert.Contains(error.Failures, f => f.Contains("MessagingServiceSid"));
    }

    [Fact]
    public void Rejects_an_unknown_provider_name()
    {
        using var provider = Build(new() { ["Email:Provider"] = "sendgrid", ["Sms:Provider"] = "Vonage" });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmsOptions>>().Value);
    }

    [Fact]
    public async Task Logging_providers_write_the_message_in_development_only()
    {
        var logger = new ListLogger<LoggingEmailSender>();
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns(Environments.Development);
        var production = Substitute.For<IHostEnvironment>();
        production.EnvironmentName.Returns(Environments.Production);
        var message = new EmailMessage("rider@example.com", "Your code", "Your code is 123456");

        await new LoggingEmailSender(development, logger).SendAsync(message, TestContext.Current.CancellationToken);
        await new LoggingEmailSender(production, logger).SendAsync(message, TestContext.Current.CancellationToken);

        Assert.Contains("Your code is 123456", logger.Messages[0]);
        Assert.Contains("To: rider@example.com", logger.Messages[0]);
        Assert.DoesNotContain("123456", logger.Messages[1]);
        Assert.DoesNotContain("rider@example.com", logger.Messages[1]);
    }

    [Theory]
    [InlineData("+447700900123", true)]
    [InlineData("+15005550006", true)]
    [InlineData("447700900123", false)]
    [InlineData("+44 7700 900123", false)]
    [InlineData(null, false)]
    public void Recognises_e164_numbers(string? number, bool expected) =>
        Assert.Equal(expected, PhoneNumbers.IsE164(number));

    [Fact]
    public void Masks_addresses_for_logs()
    {
        Assert.Equal("r***@example.com", Redact.Email("rider.one@example.com"));
        Assert.Equal("+44********23", Redact.Phone("+447700900123"));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
