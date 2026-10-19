using System.Net;
using System.Text.Json;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Tests.UseCases;
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
    public void Development_providers_are_allowed_in_Development()
    {
        var result = Validator("Development").Validate(null, new AgeAssuranceOptions());

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Dev")]
    public void Development_providers_are_refused_anywhere_else(string environment)
    {
        var result = Validator(environment).Validate(null, new AgeAssuranceOptions());

        Assert.True(result.Failed);
        Assert.Contains("AgeAssurance:Liveness:Provider 'Development' is for local development only", result.FailureMessage);
        Assert.Contains("AgeAssurance:Estimator:Provider 'Development'", result.FailureMessage);
        Assert.Contains("AgeAssurance:Document:Provider 'Development'", result.FailureMessage);
    }

    [Fact]
    public void Real_providers_pass_outside_Development()
    {
        var liveness = Substitute.For<ILivenessChecker>();
        liveness.Name.Returns("YuNet");
        var document = Substitute.For<IDocumentAgeCheck>();
        document.Name.Returns("Vendor");
        var options = new AgeAssuranceOptions();
        options.Liveness.Provider = "YuNet";
        options.Document.Provider = "Vendor";
        options.Estimator.Provider = "Http";
        options.Estimator.Http.Endpoint = new Uri("https://age.example.com/estimate");
        options.Estimator.Http.ApiKey = "secret";

        var result = Validator("Production", liveness, document).Validate(null, options);

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Http_estimator_needs_an_https_endpoint_and_a_key()
    {
        var options = new AgeAssuranceOptions();
        options.Estimator.Provider = "Http";
        options.Estimator.Http.Endpoint = new Uri("http://age.example.com/estimate");

        var result = Validator("Production").Validate(null, options);

        Assert.Contains("must use HTTPS", result.FailureMessage);
        Assert.Contains("ApiKey is required", result.FailureMessage);
    }

    [Fact]
    public void Unknown_providers_and_bad_limits_are_refused()
    {
        var options = new AgeAssuranceOptions { ChallengeAge = 16, MaxAttemptsPerDay = 0 };
        options.Liveness.Provider = "Nope";

        var result = Validator("Development").Validate(null, options);

        Assert.Contains("'Nope' isn't registered", result.FailureMessage);
        Assert.Contains("ChallengeAge must be at least 18", result.FailureMessage);
        Assert.Contains("MaxAttemptsPerDay", result.FailureMessage);
    }

    [Theory]
    [InlineData("""{"age": 27.5}""", "age", null, 27.5, 0.0)]
    [InlineData("""{"result": {"age": {"estimate": "31", "sd": 2.5}}}""", "result.age.estimate", "result.age.sd", 31.0, 2.5)]
    public void Http_estimator_reads_the_age_from_the_configured_path(string json, string agePath, string? uncertaintyPath, double age, double uncertainty)
    {
        var settings = new AgeAssuranceOptions().Estimator.Http;
        settings.Name = "Vendor";
        settings.AgePath = agePath;
        settings.UncertaintyPath = uncertaintyPath;

        using var document = JsonDocument.Parse(json);
        var estimate = HttpAgeEstimator.Read(document.RootElement, settings);

        Assert.Equal(age, estimate.Age);
        Assert.Equal(uncertainty, estimate.Uncertainty);
        Assert.Equal("Vendor", estimate.Provider);
    }

    [Fact]
    public void Http_estimator_treats_a_missing_age_as_uncertain()
    {
        using var document = JsonDocument.Parse("""{"error": "no face"}""");

        Assert.Null(HttpAgeEstimator.Read(document.RootElement, new AgeAssuranceOptions().Estimator.Http).Age);
    }

    [Fact]
    public async Task Http_estimator_posts_the_video_with_the_key()
    {
        var handler = new RecordingHandler("""{"age": 33}""");
        var options = new AgeAssuranceOptions();
        options.Estimator.Http.Endpoint = new Uri("https://age.example.com/estimate");
        options.Estimator.Http.ApiKey = "secret";
        options.Estimator.Http.ApiKeyHeader = "x-api-key";
        options.Estimator.Http.ApiKeyPrefix = string.Empty;
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(HttpAgeEstimator.ClientName).Returns(_ => new HttpClient(handler));
        var estimator = new HttpAgeEstimator(clients, new StaticOptionsMonitor<AgeAssuranceOptions>(options), NullLogger<HttpAgeEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(new SelfieVideo(new byte[] { 1, 2, 3 }, "video/webm"), Ct);

        Assert.Equal(33, estimate.Age);
        Assert.Equal("secret", handler.ApiKey);
        Assert.Equal("multipart/form-data", handler.MediaType);
        Assert.Matches("name=\"?video\"?", handler.Body);
    }

    [Fact]
    public async Task Development_estimator_returns_the_configured_age()
    {
        var options = new AgeAssuranceOptions();
        options.Estimator.Development.Age = 15;
        var estimator = new DevelopmentAgeEstimator(new StaticOptionsMonitor<AgeAssuranceOptions>(options), NullLogger<DevelopmentAgeEstimator>.Instance);

        var estimate = await estimator.EstimateAsync(new SelfieVideo(new byte[] { 1 }, "video/webm"), Ct);

        Assert.Equal(15, estimate.Age);
        Assert.True(estimator.IsDevelopmentOnly);
    }

    [Fact]
    public void Providers_are_picked_by_name()
    {
        var yunet = Substitute.For<ILivenessChecker>();
        yunet.Name.Returns("YuNet");
        var options = new AgeAssuranceOptions();
        options.Liveness.Provider = "yunet";
        var monitor = new StaticOptionsMonitor<AgeAssuranceOptions>(options);
        var providers = new AgeAssuranceProviders(
            [new DevelopmentLivenessChecker(monitor, NullLogger<DevelopmentLivenessChecker>.Instance), yunet],
            [],
            [],
            monitor);

        Assert.Same(yunet, providers.Liveness);
        Assert.Throws<InvalidOperationException>(() => providers.Estimator);
    }

    [Fact]
    public void The_registered_services_resolve_and_validate_without_a_cycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Development");
        services.AddSingleton(environment);
        UserModule.AddAgeAssurance(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgeAssuranceOptions>>().Value;
        var providers = provider.GetRequiredService<AgeAssuranceProviders>();

        Assert.Equal(25, options.ChallengeAge);
        Assert.Equal("Development", providers.Liveness.Name);
        Assert.Equal("Development", providers.Estimator.Name);
        Assert.Equal("Development", providers.Document.Name);
    }

    private static AgeAssuranceOptionsValidator Validator(string environmentName, ILivenessChecker? liveness = null, IDocumentAgeCheck? document = null)
    {
        var monitor = new StaticOptionsMonitor<AgeAssuranceOptions>(new AgeAssuranceOptions());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        List<ILivenessChecker> livenessCheckers = [new DevelopmentLivenessChecker(monitor, NullLogger<DevelopmentLivenessChecker>.Instance)];
        if (liveness is not null)
        {
            livenessCheckers.Add(liveness);
        }

        List<IDocumentAgeCheck> documents = [new DevelopmentDocumentAgeCheck(monitor, NullLogger<DevelopmentDocumentAgeCheck>.Instance)];
        if (document is not null)
        {
            documents.Add(document);
        }

        IAgeEstimator[] estimators =
        [
            new DevelopmentAgeEstimator(monitor, NullLogger<DevelopmentAgeEstimator>.Instance),
            new HttpAgeEstimator(Substitute.For<IHttpClientFactory>(), monitor, NullLogger<HttpAgeEstimator>.Instance),
        ];

        var services = new ServiceCollection();
        livenessCheckers.ForEach(c => services.AddSingleton(c));
        documents.ForEach(d => services.AddSingleton(d));
        foreach (var estimator in estimators)
        {
            services.AddSingleton(estimator);
        }

        return new AgeAssuranceOptionsValidator(environment, services.BuildServiceProvider());
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }

        public string? MediaType { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            MediaType = request.Content!.Headers.ContentType?.MediaType;
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
