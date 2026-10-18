using BJK.Modules.SMS.Contracts;
using BJK.Modules.SMS.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BJK.Modules.SMS.Tests;

public sealed class SMSModuleTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment(Environments.Development));
        new SMSModule().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    [Fact]
    public void Defaults_to_the_logging_provider()
    {
        using var provider = Build([]);

        Assert.IsType<LoggingSMSSender>(provider.GetRequiredService<ISMSSender>());
    }

    [Fact]
    public void Uses_twilio_when_configured()
    {
        using var provider = Build(new()
        {
            ["SMS:Provider"] = "Twilio",
            ["SMS:Twilio:AccountSid"] = "AC123",
            ["SMS:Twilio:AuthToken"] = "token",
            ["SMS:Twilio:MessagingServiceSid"] = "MG123",
        });

        Assert.IsType<TwilioSMSSender>(provider.GetRequiredService<ISMSSender>());
    }

    [Fact]
    public void Fails_fast_when_twilio_has_no_credentials_or_sender()
    {
        using var provider = Build(new() { ["SMS:Provider"] = "Twilio", ["SMS:Twilio:AccountSid"] = "AC123" });

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SMSOptions>>().Value);

        Assert.Contains(error.Failures, f => f.Contains("AuthToken"));
        Assert.Contains(error.Failures, f => f.Contains("MessagingServiceSid"));
    }

    [Fact]
    public void Rejects_an_unknown_provider_name()
    {
        using var provider = Build(new() { ["SMS:Provider"] = "Vonage" });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SMSOptions>>().Value);
    }

    [Fact]
    public async Task Logging_provider_writes_the_message_in_development_only()
    {
        var logger = new ListLogger<LoggingSMSSender>();
        var message = new SMSMessage("+447700900123", "Your code is 123456");

        await new LoggingSMSSender(Environment(Environments.Development), logger).SendAsync(message, TestContext.Current.CancellationToken);
        await new LoggingSMSSender(Environment(Environments.Production), logger).SendAsync(message, TestContext.Current.CancellationToken);

        Assert.Contains("Your code is 123456", logger.Messages[0]);
        Assert.Contains("+447700900123", logger.Messages[0]);
        Assert.DoesNotContain("123456", logger.Messages[1]);
        Assert.DoesNotContain("+447700900123", logger.Messages[1]);
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
    public void Masks_numbers_for_logs()
    {
        Assert.Equal("+44********23", Redact.Phone("+447700900123"));
    }
}
