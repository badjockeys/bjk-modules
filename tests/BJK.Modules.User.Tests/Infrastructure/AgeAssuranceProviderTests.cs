using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Tests.UseCases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BJK.Modules.User.Tests.Infrastructure;

public sealed class AgeAssuranceProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_stand_ins_are_used_in_Development_when_nothing_is_set_up()
    {
        var result = Validator("Development").Validate(null, new AgeAssuranceOptions());

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Dev")]
    public void Without_real_providers_the_app_refuses_to_start_anywhere_else(string environment)
    {
        var result = Validator(environment).Validate(null, new AgeAssuranceOptions());

        Assert.True(result.Failed);
        Assert.Contains("Liveness step has no real provider", result.FailureMessage);
        Assert.Contains("AgeAssurance:Liveness:ModelPath", result.FailureMessage);
        Assert.Contains("Estimator step has no real provider", result.FailureMessage);
        Assert.Contains("AgeAssurance:Yoti:SdkId and PrivateKey", result.FailureMessage);
        Assert.Contains("Document step has no real provider", result.FailureMessage);
    }

    [Fact]
    public void Configured_real_providers_pass_outside_Development()
    {
        var result = Validator("Production", Real<ILivenessChecker>("YuNet"), Real<IAgeEstimator>("Yoti"), Real<IDocumentAgeCheck>("Vendor"))
            .Validate(null, new AgeAssuranceOptions());

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void A_real_provider_without_its_settings_isnt_used()
    {
        var unconfigured = Substitute.For<ILivenessChecker>();
        unconfigured.Name.Returns("YuNet");
        unconfigured.IsConfigured.Returns(false);

        var result = Validator("Production", unconfigured).Validate(null, new AgeAssuranceOptions());

        Assert.Contains("Liveness step has no real provider", result.FailureMessage);
    }

    [Fact]
    public void Yoti_settings_are_checked_when_set()
    {
        var options = new AgeAssuranceOptions();
        options.Yoti.SdkId = "not-a-uuid";
        options.Yoti.PrivateKey = "not a key";
        options.Yoti.BaseUrl = new Uri("http://api.yoti.com/ai/v1");

        var result = Validator("Production").Validate(null, options);

        Assert.Contains("AgeAssurance:Yoti:SdkId must be", result.FailureMessage);
        Assert.Contains("AgeAssurance:Yoti:PrivateKey must be", result.FailureMessage);
        Assert.Contains("AgeAssurance:Yoti:BaseUrl must be an https address", result.FailureMessage);
    }

    [Fact]
    public void Outside_Development_the_whole_app_validates_with_yoti_set_up_without_reading_options_twice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment("Production"));
        services.AddSingleton(Real<ILivenessChecker>("YuNet"));
        services.AddSingleton(Real<IDocumentAgeCheck>("Vendor"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgeAssurance:Yoti:SdkId"] = Guid.NewGuid().ToString(),
                ["AgeAssurance:Yoti:PrivateKey"] = Pem(),
            })
            .Build();
        UserModule.AddAgeAssurance(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(25, provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgeAssuranceOptions>>().Value.ChallengeAge);
        Assert.Equal("Yoti", provider.GetRequiredService<AgeAssuranceProviders>().Estimator.Name);
    }

    [Fact]
    public void Yoti_is_configured_only_with_both_keys_and_not_with_placeholders()
    {
        Assert.False(new YotiOptions().IsConfigured);
        Assert.False(new YotiOptions { SdkId = Guid.NewGuid().ToString() }.IsConfigured);
        Assert.False(new YotiOptions { SdkId = "<from the Yoti Hub>", PrivateKey = "<secret>" }.IsConfigured);
        Assert.True(new YotiOptions { SdkId = Guid.NewGuid().ToString(), PrivateKey = Pem() }.IsConfigured);
    }

    [Fact]
    public void Bad_limits_are_refused()
    {
        var options = new AgeAssuranceOptions { ChallengeAge = 16, MaxAttemptsPerDay = 0 };

        var result = Validator("Development").Validate(null, options);

        Assert.Contains("ChallengeAge must be at least 18", result.FailureMessage);
        Assert.Contains("MaxAttemptsPerDay", result.FailureMessage);
    }

    [Theory]
    [InlineData("""{"age": {"age": 27.5, "st_dev": 2.1}, "antispoofing": {"prediction": "real"}}""", 27.5, 2.1)]
    [InlineData("""{"age": {"age": 31, "st_dev": 1.0}}""", 31.0, 1.0)]
    [InlineData("""{"age": 22.4, "st_dev": 3}""", 22.4, 3.0)]
    public void Yoti_answers_are_read(string json, double age, double uncertainty)
    {
        using var document = JsonDocument.Parse(json);

        var estimate = YotiAgeEstimator.Read(document.RootElement, maxUncertainty: 6);

        Assert.Equal(age, estimate.Age);
        Assert.Equal(uncertainty, estimate.Uncertainty);
        Assert.Equal("Yoti", estimate.Provider);
    }

    [Theory]
    [InlineData("""{"age": {"age": 27.5, "st_dev": 2.1}, "antispoofing": {"prediction": "fake"}}""")]
    [InlineData("""{"age": {"age": 27.5, "st_dev": 2.1}, "antispoofing": {"prediction": "undetermined"}}""")]
    [InlineData("""{"age": {"age": 27.5, "st_dev": 7.5}}""")]
    [InlineData("""{"error": "no face"}""")]
    public void A_spoof_a_wide_margin_or_no_age_counts_as_couldnt_tell(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(YotiAgeEstimator.Read(document.RootElement, maxUncertainty: 6).Age);
    }

    [Fact]
    public void Yoti_requests_are_signed_over_the_method_path_and_body()
    {
        using var key = RSA.Create(2048);
        var body = Encoding.UTF8.GetBytes("""{"img":"abc"}""");
        var query = YotiRequestSigner.Query(Guid.Parse("8f2a6b44-1c3d-4e5f-9a0b-1c2d3e4f5a6b"), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        var digest = YotiRequestSigner.Sign(key, "POST", "/age-antispoofing" + query, body);

        Assert.Equal("?nonce=8f2a6b44-1c3d-4e5f-9a0b-1c2d3e4f5a6b&timestamp=1800000000", query);
        var message = Encoding.UTF8.GetBytes("POST&/age-antispoofing" + query + "&" + Convert.ToBase64String(body));
        Assert.True(key.VerifyData(message, Convert.FromBase64String(digest), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void The_private_key_loads_from_pem_including_escaped_new_lines()
    {
        var pem = Pem();

        Assert.True(YotiRequestSigner.TryLoad(pem, out var key));
        key.Dispose();
        Assert.True(YotiRequestSigner.TryLoad(pem.Replace("\n", "\\n", StringComparison.Ordinal), out key));
        key.Dispose();
        Assert.False(YotiRequestSigner.TryLoad("nope", out key));
        key.Dispose();
    }

    [Fact]
    public async Task Development_estimator_returns_the_configured_age()
    {
        var options = new AgeAssuranceOptions();
        options.Development.EstimatedAge = 15;
        var estimator = new DevelopmentAgeEstimator(new StaticOptionsMonitor<AgeAssuranceOptions>(options), NullLogger<DevelopmentAgeEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(new SelfieVideo(new byte[] { 1 }, "video/webm"), Ct);

        Assert.Equal(15, estimate.Age);
        Assert.True(estimator.IsDevelopmentOnly);
    }

    [Fact]
    public void A_configured_real_provider_wins_and_the_stand_in_is_only_used_in_Development()
    {
        var monitor = new StaticOptionsMonitor<AgeAssuranceOptions>(new AgeAssuranceOptions());
        var standIn = new DevelopmentLivenessChecker(monitor, NullLogger<DevelopmentLivenessChecker>.Instance);
        var yunet = Real<ILivenessChecker>("YuNet");

        Assert.Same(yunet, new AgeAssuranceProviders([standIn, yunet], [], [], Environment("Production")).Liveness);
        Assert.Same(yunet, new AgeAssuranceProviders([standIn, yunet], [], [], Environment("Development")).Liveness);
        Assert.Same(standIn, new AgeAssuranceProviders([standIn], [], [], Environment("Development")).Liveness);
        Assert.Throws<InvalidOperationException>(() => new AgeAssuranceProviders([standIn], [], [], Environment("Production")).Liveness);
    }

    [Fact]
    public void The_registered_services_resolve_and_validate_without_a_cycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment("Development"));
        UserModule.AddAgeAssurance(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgeAssuranceOptions>>().Value;
        var providers = provider.GetRequiredService<AgeAssuranceProviders>();

        Assert.Equal(25, options.ChallengeAge);
        Assert.Equal("Development", providers.Liveness.Name);
        Assert.Equal("Development", providers.Estimator.Name);
        Assert.Equal("Development", providers.Document.Name);
    }

    [Fact]
    public void With_yoti_keys_the_estimator_is_yoti()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Environment("Development"));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgeAssurance:Yoti:SdkId"] = Guid.NewGuid().ToString(),
                ["AgeAssurance:Yoti:PrivateKey"] = Pem(),
            })
            .Build();
        UserModule.AddAgeAssurance(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("Yoti", provider.GetRequiredService<AgeAssuranceProviders>().Estimator.Name);
    }

    private static string Pem()
    {
        using var key = RSA.Create(2048);
        return key.ExportPkcs8PrivateKeyPem();
    }

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private static T Real<T>(string name)
        where T : class, IAgeAssuranceProvider
    {
        var provider = Substitute.For<T>();
        provider.Name.Returns(name);
        provider.IsConfigured.Returns(true);
        return provider;
    }

    private static AgeAssuranceOptionsValidator Validator(
        string environmentName,
        ILivenessChecker? liveness = null,
        IAgeEstimator? estimator = null,
        IDocumentAgeCheck? document = null)
    {
        var monitor = new StaticOptionsMonitor<AgeAssuranceOptions>(new AgeAssuranceOptions());
        var services = new ServiceCollection();
        services.AddSingleton<ILivenessChecker>(new DevelopmentLivenessChecker(monitor, NullLogger<DevelopmentLivenessChecker>.Instance));
        services.AddSingleton<IAgeEstimator>(new DevelopmentAgeEstimator(monitor, NullLogger<DevelopmentAgeEstimator>.Instance));
        services.AddSingleton<IDocumentAgeCheck>(new DevelopmentDocumentAgeCheck(monitor, NullLogger<DevelopmentDocumentAgeCheck>.Instance));
        if (liveness is not null)
        {
            services.AddSingleton(liveness);
        }

        if (estimator is not null)
        {
            services.AddSingleton(estimator);
        }

        if (document is not null)
        {
            services.AddSingleton(document);
        }

        return new AgeAssuranceOptionsValidator(Environment(environmentName), services.BuildServiceProvider());
    }
}
