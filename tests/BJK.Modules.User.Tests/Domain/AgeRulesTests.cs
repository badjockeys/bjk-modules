using BJK.Modules.User.Contracts;
using BJK.Modules.User.Domain;
using Xunit;

namespace BJK.Modules.User.Tests.Domain;

public sealed class AgeRulesTests
{
    [Theory]
    [InlineData("2013-10-19", "2026-10-19", 13)]
    [InlineData("2013-10-20", "2026-10-19", 12)]
    [InlineData("2008-10-19", "2026-10-19", 18)]
    [InlineData("2008-10-20", "2026-10-19", 17)]
    [InlineData("2012-02-29", "2025-02-28", 12)] // a leap-day birthday comes on 1 March in other years
    [InlineData("2012-02-29", "2025-03-01", 13)]
    public void Age_is_whole_years(string dateOfBirth, string today, int expected)
    {
        Assert.Equal(expected, AgeRules.AgeOn(DateOnly.Parse(dateOfBirth), DateOnly.Parse(today)));
    }

    [Fact]
    public void Thirteen_can_join_and_is_a_minor()
    {
        var today = new DateOnly(2026, 10, 19);

        Assert.True(AgeRules.IsOldEnoughToJoin(new DateOnly(2013, 10, 19), today));
        Assert.False(AgeRules.IsOldEnoughToJoin(new DateOnly(2013, 10, 20), today));
        Assert.True(AgeRules.IsMinor(new DateOnly(2013, 10, 19), today));
        Assert.False(AgeRules.IsMinor(new DateOnly(2008, 10, 19), today));
    }

    [Fact]
    public void Dates_in_the_future_or_over_120_years_ago_are_not_believable()
    {
        var today = new DateOnly(2026, 10, 19);

        Assert.False(AgeRules.IsPlausible(today.AddDays(1), today));
        Assert.False(AgeRules.IsPlausible(new DateOnly(1905, 1, 1), today));
        Assert.True(AgeRules.IsPlausible(new DateOnly(1950, 1, 1), today));
    }

    [Fact]
    public void Account_age_facts_say_who_has_a_date_of_birth_and_who_is_a_minor()
    {
        var today = new DateOnly(2026, 10, 19);

        Assert.False(new AccountAgeFacts(null, AgeVerified: false).IsSignUpComplete);
        Assert.True(new AccountAgeFacts(TestDates.Fifteen, AgeVerified: false).IsSignUpComplete);
        Assert.True(new AccountAgeFacts(TestDates.Fifteen, false).IsMinorOn(today));
        Assert.False(new AccountAgeFacts(TestDates.Adult, false).IsMinorOn(today));
        Assert.False(new AccountAgeFacts(null, false).IsMinorOn(today));
        Assert.DoesNotContain("2011", new AccountAgeFacts(TestDates.Fifteen, false).ToString(), StringComparison.Ordinal);
    }
}
