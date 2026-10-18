using BJK.Modules.Profiles.Domain;
using Xunit;

namespace BJK.Modules.Profiles.Tests.Domain;

public sealed class HandleTests
{
    [Theory]
    [InlineData("@RoadHog", "roadhog")]
    [InlineData("  lane_hogger_99 ", "lane_hogger_99")]
    [InlineData(null, "")]
    public void Normalise_trims_drops_the_at_sign_and_lower_cases(string? input, string expected)
    {
        Assert.Equal(expected, Handle.Normalise(input));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("road_hog_2026")]
    [InlineData("a23456789012345678901234567890")]
    public void Valid_handles_are_accepted(string handle)
    {
        Assert.True(Handle.IsValid(handle));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901")]
    [InlineData("9lives")]
    [InlineData("_hidden")]
    [InlineData("road-hog")]
    [InlineData("road hog")]
    [InlineData("admin")]
    [InlineData("me")]
    public void Invalid_or_reserved_handles_are_rejected(string handle)
    {
        Assert.NotNull(Handle.Problem(handle));
    }
}
