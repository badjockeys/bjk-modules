using BJK.Modules.Chat.Guard;
using BJK.Modules.Chat.UseCases;
using BJK.Modules.Shared.Notifications;
using Xunit;

namespace BJK.Modules.Chat.Tests;

public sealed class ChatExamplesTests
{
    private static HomeArea Home(string name) => new(name, 53.43m, -2.80m, 5);

    [Theory]
    [InlineData("Prescot")]
    [InlineData("St Helens")]
    [InlineData("Newton-le-Willows")]
    [InlineData("Bishop's Stortford")]
    public void A_town_is_named_in_the_local_questions(string town)
    {
        var examples = ChatExamples.For(Home(town));

        Assert.Equal($"How many incidents have there been in {town} this month?", examples[0]);
        Assert.Equal($"Worst roads for tailgating in {town}", examples[1]);
        Assert.Equal($"Is pavement parking getting worse in {town}?", examples[2]);
    }

    [Theory]
    [InlineData("WA10")]
    [InlineData("L34")]
    [InlineData("ec1a")]
    [InlineData(" M1 ")]
    public void A_postcode_district_gets_near_me_questions(string district)
    {
        var examples = ChatExamples.For(Home(district));

        Assert.All(examples.Take(3), q => Assert.Contains("near me", q));
        Assert.All(examples, q => Assert.DoesNotContain(district.Trim(), q, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_home_area_means_untailored_questions_with_no_place_in_them()
    {
        Assert.Same(ChatExamples.Untailored, ChatExamples.For(null));
        Assert.Same(ChatExamples.Untailored, ChatExamples.For(Home("  ")));
        Assert.All(ChatExamples.Untailored, q => Assert.DoesNotContain("near me", q));
        Assert.All(ChatExamples.Untailored, q => Assert.DoesNotContain("Warrington", q));
    }

    [Fact]
    public void Near_me_is_the_home_area_named_only_when_its_a_town()
    {
        var town = ChatExamples.NearOf(Home("Prescot"))!;
        var district = ChatExamples.NearOf(Home("WA10"))!;

        Assert.Equal((53.43, -2.80, "Prescot"), (town.Latitude, town.Longitude, town.Name));
        Assert.Equal((53.43, -2.80, (string?)null), (district.Latitude, district.Longitude, district.Name));
        Assert.Null(ChatExamples.NearOf(null));
    }

    [Theory]
    [InlineData("Prescot")]
    [InlineData("Newton-le-Willows")]
    [InlineData("WA10")]
    [InlineData(null)]
    public void Every_example_gets_past_the_guards_fixed_checks(string? place)
    {
        var examples = ChatExamples.For(place is null ? null : Home(place));

        Assert.All(examples, q => Assert.Null(ChatGuard.Fixed(q)));
    }

    [Fact]
    public void The_guard_offers_the_members_own_examples()
    {
        var examples = ChatExamples.For(Home("Prescot"));

        var greeting = ChatGuard.Fixed("hi", examples)!;
        var refused = ChatGuard.Fixed("Who owns the red Audi?", examples)!;

        Assert.Equal(examples.Take(3).ToArray(), greeting.Reply!.Suggestions.ToArray());
        Assert.Equal(examples.Take(2).ToArray(), refused.Reply!.Suggestions.ToArray());
    }
}
