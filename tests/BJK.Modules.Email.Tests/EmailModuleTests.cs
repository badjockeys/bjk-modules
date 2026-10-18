using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BJK.Modules.Email.Tests;

public sealed class EmailModuleTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment(Environments.Development));
        new EmailModule().AddServices(services, configuration);
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

        Assert.IsType<LoggingEmailSender>(provider.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void Uses_sendgrid_when_configured()
    {
        using var provider = Build(new()
        {
            ["Email:Provider"] = "SendGrid",
            ["Email:FromAddress"] = "no-reply@badjockeys.com",
            ["Email:SendGrid:ApiKey"] = "SG.key",
        });

        Assert.IsType<SendGridEmailSender>(provider.GetRequiredService<IEmailSender>());
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
    public void Rejects_an_unknown_provider_name()
    {
        using var provider = Build(new() { ["Email:Provider"] = "Mailgun" });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
    }

    [Fact]
    public async Task Logging_provider_writes_the_email_in_development_only()
    {
        var logger = new ListLogger<LoggingEmailSender>();
        var message = new EmailMessage("rider@example.com", "Your code", "Your code is 123456");

        await new LoggingEmailSender(Environment(Environments.Development), logger).SendAsync(message, TestContext.Current.CancellationToken);
        await new LoggingEmailSender(Environment(Environments.Production), logger).SendAsync(message, TestContext.Current.CancellationToken);

        Assert.Contains("Your code is 123456", logger.Messages[0]);
        Assert.Contains("To: rider@example.com", logger.Messages[0]);
        Assert.DoesNotContain("123456", logger.Messages[1]);
        Assert.DoesNotContain("rider@example.com", logger.Messages[1]);
    }

    [Fact]
    public void Masks_addresses_for_logs()
    {
        Assert.Equal("r***@example.com", Redact.Email("rider.one@example.com"));
    }
}
