using System.Net;
using BJK.Modules.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BJK.Modules.Safety.Tests;

public sealed class ContentScreeningTests
{
    private static readonly IOptions<ScreeningOptions> Defaults = Options.Create(new ScreeningOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly KeywordContentScreener _keywords = new(Defaults);

    [Theory]
    [InlineData("I know where you live")]
    [InlineData("someone should track him down")]
    [InlineData("I'm going to K1LL you")]
    [InlineData("hope he diiiies")]
    public void Threats_and_calls_to_confront_are_held(string text)
    {
        Assert.Equal(ScreeningVerdict.Hold, _keywords.Screen(text).Verdict);
    }

    [Fact]
    public void Slurs_are_held_with_the_identity_attack_label()
    {
        var result = _keywords.Screen("what a faggot");

        Assert.Equal(ScreeningVerdict.Hold, result.Verdict);
        Assert.Contains(ScreeningKeywords.IdentityAttack, result.Labels);
    }

    [Theory]
    [InlineData("absolute wanker")]
    [InlineData("anyone know who this is?")]
    public void Abuse_and_identification_requests_are_flagged(string text)
    {
        Assert.Equal(ScreeningVerdict.Flag, _keywords.Screen(text).Verdict);
    }

    [Theory]
    [InlineData("What an idiot, nearly took me out")]
    [InlineData("Shocking overtake on a blind bend")]
    [InlineData("Skills for days")]
    [InlineData("He killed the engine at the lights")]
    [InlineData("Scunthorpe bypass")]
    public void Ordinary_comments_are_allowed(string text)
    {
        Assert.Equal(ScreeningVerdict.Allow, _keywords.Screen(text).Verdict);
    }

    [Fact]
    public void Thresholds_decide_the_verdict()
    {
        var options = new ScreeningOptions { FlagThreshold = 0.4, HoldThreshold = 0.7 };

        Assert.Equal(ScreeningVerdict.Allow, options.VerdictFor(0.39));
        Assert.Equal(ScreeningVerdict.Flag, options.VerdictFor(0.4));
        Assert.Equal(ScreeningVerdict.Hold, options.VerdictFor(0.7));
    }

    [Fact]
    public async Task The_classifier_score_wins_when_higher()
    {
        var handler = new FakeHandler().Reply(HttpStatusCode.OK, """{"scores":{"toxicity":0.91,"insult":0.62,"threat":0.05}}""");
        var screener = Http(handler);

        var result = await screener.ScreenAsync("you absolute muppet", Ct);

        Assert.Equal(0.91, result.Score, 3);
        Assert.Equal(ScreeningVerdict.Hold, result.Verdict);
        Assert.Contains("toxicity", result.Labels);
        Assert.Contains("insult", result.Labels);
        Assert.DoesNotContain("threat", result.Labels);
        Assert.Equal("""{"text":"you absolute muppet"}""", handler.Requests.Single().Body);
    }

    [Fact]
    public async Task The_keyword_list_still_applies_when_the_classifier_scores_low()
    {
        var screener = Http(new FakeHandler().Reply(HttpStatusCode.OK, """{"scores":{"toxicity":0.1}}"""));

        var result = await screener.ScreenAsync("I know where you live", Ct);

        Assert.Equal(ScreeningVerdict.Hold, result.Verdict);
    }

    [Fact]
    public async Task If_the_classifier_is_down_the_keyword_result_is_used()
    {
        var screener = Http(new FakeHandler().Reply(HttpStatusCode.ServiceUnavailable));

        var result = await screener.ScreenAsync("lovely driving", Ct);

        Assert.Equal(ScreeningVerdict.Allow, result.Verdict);
        Assert.Contains(HttpContentScreener.ClassifierUnavailableLabel, result.Labels);
    }

    [Fact]
    public void The_classifier_endpoint_must_be_absolute_and_thresholds_ordered()
    {
        var validator = new ScreeningOptionsValidator();

        Assert.True(validator.Validate(null, new ScreeningOptions { Endpoint = new Uri("/score", UriKind.Relative) }).Failed);
        Assert.True(validator.Validate(null, new ScreeningOptions { Endpoint = new Uri("http://detoxify:8000/score") }).Succeeded);
        Assert.True(validator.Validate(null, new ScreeningOptions { FlagThreshold = 0.9, HoldThreshold = 0.5 }).Failed);
        Assert.True(validator.Validate(null, new ScreeningOptions()).Succeeded);
    }

    private HttpContentScreener Http(FakeHandler handler)
    {
        var options = Options.Create(new ScreeningOptions { Endpoint = new Uri("http://detoxify:8000/score") });
        return new HttpContentScreener(new HttpClient(handler), new KeywordContentScreener(options), options, NullLogger<HttpContentScreener>.Instance);
    }
}
