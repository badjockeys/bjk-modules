using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Create;
using BJK.Modules.Posts.UseCases.Get;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class ValidatorTests
{
    private readonly CreatePostCommandValidator _create = new(new FixedTimeProvider(Now));

    private static CreatePostCommand Command(
        DateTimeOffset? incidentAt = null,
        decimal latitude = 53.59m,
        int mediaCount = 1,
        string title = "Close pass") =>
        new(
            title,
            null,
            IncidentCategory.ClosePass,
            incidentAt ?? Now.AddHours(-2),
            latitude,
            -2.29m,
            null,
            Enumerable.Range(0, mediaCount).Select(_ => Guid.NewGuid()).ToList());

    [Fact]
    public void A_good_post_passes()
    {
        Assert.True(_create.Validate(Command()).IsValid);
    }

    [Fact]
    public void Incidents_cant_be_in_the_future()
    {
        var result = _create.Validate(Command(incidentAt: Now.AddHours(1)));

        Assert.Contains(result.Errors, e => e.PropertyName == "IncidentAt");
    }

    [Fact]
    public void A_little_clock_skew_is_allowed()
    {
        Assert.True(_create.Validate(Command(incidentAt: Now.AddMinutes(2))).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Posts_need_one_to_ten_media(int count)
    {
        var result = _create.Validate(Command(mediaCount: count));

        Assert.Contains(result.Errors, e => e.PropertyName == "MediaIds");
    }

    [Fact]
    public void Latitude_must_be_on_earth()
    {
        Assert.Contains(_create.Validate(Command(latitude: 91m)).Errors, e => e.PropertyName == "Latitude");
    }

    [Fact]
    public void Title_is_required()
    {
        Assert.Contains(_create.Validate(Command(title: " ")).Errors, e => e.PropertyName == "Title");
    }

    [Fact]
    public void Area_search_needs_point_and_radius_together()
    {
        var validator = new ListPostsQueryValidator();

        Assert.False(validator.Validate(new ListPostsQuery(Latitude: 53.5m, Longitude: -2.2m)).IsValid);
        Assert.True(validator.Validate(new ListPostsQuery(Latitude: 53.5m, Longitude: -2.2m, RadiusKm: 10)).IsValid);
        Assert.False(validator.Validate(new ListPostsQuery(Latitude: 53.5m, Longitude: -2.2m, RadiusKm: 500)).IsValid);
    }
}
