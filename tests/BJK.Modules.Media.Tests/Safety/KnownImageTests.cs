using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
using Xunit;

namespace BJK.Modules.Media.Tests.Safety;

public sealed class KnownImageTests
{
    [Theory]
    [InlineData(KnownHashKind.Sha256, "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData(KnownHashKind.Pdq, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData(KnownHashKind.Md5, "0123456789abcdef0123456789abcdef", true)]
    [InlineData(KnownHashKind.Md5, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", false)]
    [InlineData(KnownHashKind.Sha256, "not a hash", false)]
    [InlineData(KnownHashKind.Pdq, null, false)]
    public void Hashes_are_checked_and_lower_cased(KnownHashKind kind, string? value, bool valid)
    {
        var normalised = KnownImageHash.Normalise(kind, value);

        Assert.Equal(valid, normalised is not null);
        if (valid)
        {
            Assert.Equal(value!.ToLowerInvariant(), normalised);
        }
    }

    [Fact]
    public void A_match_survives_being_stored_as_a_review_reason()
    {
        var hit = new KnownImageHit(Guid.NewGuid(), KnownHashKind.Pdq, new string('a', 64), "Test", null, 7);

        var parsed = KnownImageHit.FromReason(hit.ToReason());

        Assert.Equal((hit.HashId, 7), parsed);
        Assert.Null(KnownImageHit.FromReason("1 detection(s) had low confidence."));
    }

    [Fact]
    public void The_built_in_clip_prompts_load()
    {
        var prompts = ClipPrompts.Builtin.Value;

        Assert.Equal(100f, prompts.LogitScale);
        Assert.Contains(1, prompts.Groups);
        Assert.Contains(2, prompts.Groups);
        Assert.All(prompts.Embeddings, e => Assert.Equal(512, e.Length));
    }

    [Fact]
    public void An_image_closest_to_a_nudity_description_scores_high_for_nudity()
    {
        var prompts = ClipPrompts.Builtin.Value;
        var nudity = Array.IndexOf(prompts.Groups, 2);
        var safe = Array.IndexOf(prompts.Groups, 0);

        var nude = prompts.Score(prompts.Embeddings[nudity]);
        var road = prompts.Score(prompts.Embeddings[safe]);

        Assert.True(nude.Nudity > 0.5, $"Nudity {nude.Nudity}");
        Assert.True(road.Nudity < 0.1 && road.Graphic < 0.1, $"Nudity {road.Nudity}, graphic {road.Graphic}");
    }
}
