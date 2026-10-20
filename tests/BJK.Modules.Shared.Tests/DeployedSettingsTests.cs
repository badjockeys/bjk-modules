using BJK.Modules.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BJK.Modules.Shared.Tests;

public sealed class DeployedSettingsTests
{
    private static ServiceProvider Build(string environmentName, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment);
        services.RequireWhenDeployed("Email:Provider", "SendGrid", v => v == "SendGrid");
        services.RequireWhenDeployed("Reels:ViewerKeySecret", "a long random secret");
        services.RequireWhenDeployed("Captcha:Provider", "Turnstile", v => v == "Turnstile", allowMissing: true);
        services.RequireWhenDeployed("Notifications:AppUrl", "an https address", DeployedSettingChecks.IsHttps);
        return services.BuildServiceProvider();
    }

    private static string[] Failures(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DeployedSettings>>().Value)
            .Failures.ToArray();

    [Fact]
    public void Outside_development_every_problem_is_listed_together()
    {
        using var provider = Build("Production", new()
        {
            ["Email:Provider"] = "Log",
            ["Reels:ViewerKeySecret"] = "<a long random string>",
            ["Notifications:AppUrl"] = "http://badjockeys.com",
            ["Media:Storage:AccessKeyId"] = "<GO-LIVE: R2 key>",
        });

        var failures = Failures(provider);

        Assert.Contains("4 setting(s)", failures[0]);
        Assert.Contains(failures, f => f.StartsWith("Email:Provider must be SendGrid", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Reels:ViewerKeySecret is still a placeholder", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Notifications:AppUrl must be an https address", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Media:Storage:AccessKeyId is still a placeholder", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.StartsWith("Captcha:Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_settings_are_named()
    {
        using var provider = Build("Staging", []);

        var failures = Failures(provider);

        Assert.Contains(failures, f => f.StartsWith("Email:Provider is missing", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Reels:ViewerKeySecret is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_set_value_with_a_default_is_still_checked()
    {
        using var provider = Build("Production", new()
        {
            ["Email:Provider"] = "SendGrid",
            ["Reels:ViewerKeySecret"] = "secret",
            ["Notifications:AppUrl"] = "https://badjockeys.com",
            ["Captcha:Provider"] = "AlwaysPass",
        });

        Assert.Equal("Captcha:Provider must be Turnstile.", Assert.Single(Failures(provider)[1..]));
    }

    [Fact]
    public void Ready_settings_pass_and_development_is_never_checked()
    {
        using var ready = Build("Production", new()
        {
            ["Email:Provider"] = "SendGrid",
            ["Reels:ViewerKeySecret"] = "secret",
            ["Notifications:AppUrl"] = "https://badjockeys.com",
        });
        using var local = Build(Environments.Development, new() { ["Email:Provider"] = "Log", ["X"] = "<placeholder>" });

        Assert.NotNull(ready.GetRequiredService<IOptions<DeployedSettings>>().Value);
        Assert.NotNull(local.GetRequiredService<IOptions<DeployedSettings>>().Value);
        Assert.Equal(0, SettingsCheck.Run(ready));
    }

    [Fact]
    public void The_settings_check_fails_with_problems()
    {
        using var provider = Build("Production", []);

        Assert.Equal(1, SettingsCheck.Run(provider));
        Assert.True(SettingsCheck.Requested(["--check-settings"]));
        Assert.False(SettingsCheck.Requested([]));
    }

    [Theory]
    [InlineData("1x0000000000000000000000000000000AA", true)]
    [InlineData("2x00000000000000000000AB", true)]
    [InlineData("0x4AAAAAAAreal-secret", false)]
    public void Recognises_cloudflare_test_keys(string key, bool isTest) =>
        Assert.Equal(isTest, TurnstileTestKeys.IsTestKey(key));
}
