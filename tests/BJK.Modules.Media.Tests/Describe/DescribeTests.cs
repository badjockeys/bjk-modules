using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Describe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BJK.Modules.Media.Tests.Describe;

public sealed class DescribeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("""{"title": "Tailgating on the A58", "description": "A grey car follows very closely. It stays close for a mile."}""")]
    [InlineData("Here you go:\n```json\n{\"title\": \"Tailgating on the A58\", \"description\": \"A grey car follows very closely. It stays close for a mile.\"}\n```")]
    public void The_model_answer_is_read_even_when_wrapped(string answer)
    {
        var draft = DescriptionPrompt.Read(answer);

        Assert.NotNull(draft);
        Assert.Equal("Tailgating on the A58", draft.Value.Title);
        Assert.StartsWith("A grey car", draft.Value.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I can't help with that.")]
    [InlineData("""{"title": "", "description": "Something."}""")]
    [InlineData("""{"title": "A title"}""")]
    public void No_usable_draft_is_null(string? answer) => Assert.Null(DescriptionPrompt.Read(answer));

    [Fact]
    public void Number_plates_are_taken_out_of_drafts()
    {
        var draft = DescriptionPrompt.Read("""{"title": "AB12 CDE ran the red", "description": "The van YX68ABC went through the lights."}""");

        Assert.Equal("a vehicle ran the red", draft!.Value.Title);
        Assert.Equal("The van a vehicle went through the lights.", draft.Value.Description);
    }

    [Fact]
    public void Long_drafts_are_cut_at_a_sentence()
    {
        var text = string.Join(' ', Enumerable.Repeat("The car drifts across the lane.", 40));
        var draft = DescriptionPrompt.Read($$"""{"title": "Drifting", "description": "{{text}}"}""");

        Assert.True(draft!.Value.Description.Length <= 600);
        Assert.EndsWith(".", draft.Value.Description);
    }

    [Fact]
    public void The_details_are_given_in_plain_words()
    {
        var details = DescriptionPrompt.Details(new DescriptionContext(
            ["RedLightRunning", "MobilePhoneUse"], "High Street, Prescot", null, true, "Van", "Ford", "Transit", "White"));

        Assert.Contains("Incident type: red light running, mobile phone use", details);
        Assert.Contains("Where: High Street, Prescot", details);
        Assert.Contains("Light: dark", details);
        Assert.Contains("The other vehicle: White Ford Transit Van", details);
    }

    [Fact]
    public async Task The_stand_in_writes_a_draft_in_Development_only()
    {
        var context = new DescriptionContext(["Tailgating"], "A58 Prescot Bypass, Prescot", null, false, "Car", "BMW", "3 Series", "Black");

        var draft = await Model("Development").DescribeAsync([], context, Ct);

        Assert.Equal("Tailgating on A58 Prescot Bypass", draft.Title);
        Assert.Contains("black bmw 3 series", draft.Description);
        Assert.Equal("Development", draft.Model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Model("Production").DescribeAsync([], context, Ct));
    }

    [Fact]
    public void Frames_are_chosen_unusual_first_then_least_alike_in_time_order()
    {
        // Ten frames of an ordinary road (similar), with something different at frames 4 and 8.
        float[] road = ClipEmbedding.Normalise([1, 0.1f, 0]);
        float[] incident = ClipEmbedding.Normalise([0, 1, 0]);
        float[] other = ClipEmbedding.Normalise([0, 0, 1]);
        var frames = Enumerable.Range(0, 10).Select(i => (float[]?)(i == 4 ? incident : i == 8 ? other : road)).ToList();

        var chosen = DescriptionFrames.Choose(frames, 3);

        Assert.Equal(3, chosen.Count);
        Assert.Contains(4, chosen);
        Assert.Contains(8, chosen);
        Assert.Equal(chosen.OrderBy(i => i), chosen);
    }

    [Fact]
    public void Without_embeddings_frames_are_spread_evenly()
    {
        Assert.Equal([0, 3, 6, 9], DescriptionFrames.Choose(Enumerable.Repeat<float[]?>(null, 10).ToList(), 4));
        Assert.Equal([0, 1, 2], DescriptionFrames.Choose(Enumerable.Repeat<float[]?>(null, 3).ToList(), 6));
    }

    [Fact]
    public void Near_duplicate_photos_are_left_out()
    {
        float[] a = ClipEmbedding.Normalise([1, 0, 0]);
        float[] almostA = ClipEmbedding.Normalise([1, 0.05f, 0]);
        float[] b = ClipEmbedding.Normalise([0, 1, 0]);
        byte[] one = [1], two = [2], three = [3];

        var chosen = DescriptionFrames.WithoutDuplicates([(one, a), (two, almostA), (three, b)], 6);

        Assert.Equal([one, three], chosen);
    }

    [Fact]
    public void A_description_keeps_its_media_in_order_and_fails_only_while_queued()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var description = MediaDescription.Create(Guid.NewGuid(), ids, "{}", DateTimeOffset.UnixEpoch);

        Assert.Equal(ids, description.MediaIdList);
        description.MarkReady(new string('t', 200), "Text", "qwen3-vl:8b", DateTimeOffset.UnixEpoch);
        description.MarkFailed("too late", DateTimeOffset.UnixEpoch);

        Assert.Equal(DescriptionStatus.Ready, description.Status);
        Assert.Equal(MediaDescription.TitleMaxLength, description.Title!.Length);
    }

    [Fact]
    public void Clip_summaries_round_trip_and_average_by_weight()
    {
        float[] a = [1, 0];
        float[] b = [0, 1];

        Assert.Equal(a, ClipEmbedding.FromBytes(ClipEmbedding.ToBytes(a)));
        var average = ClipEmbedding.Average([(a, 3), (b, 1)])!;
        Assert.Equal(0.949, average[0], 3);
        Assert.Equal(0.316, average[1], 3);
        Assert.Null(ClipEmbedding.Average([]));
    }

    private static DevelopmentDescriptionModel Model(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new DevelopmentDescriptionModel(environment, NullLogger<DevelopmentDescriptionModel>.Instance);
    }
}
