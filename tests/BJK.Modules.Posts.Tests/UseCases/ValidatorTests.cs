using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Create;
using BJK.Modules.Posts.UseCases.Get;
using Microsoft.Extensions.Options;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class ValidatorTests
{
    private readonly CreatePostCommandValidator _create = new(new FixedTimeProvider(Now), Options.Create(new PostsOptions()));

    private static CreatePostCommand Command(
        DateTimeOffset? incidentAt = null,
        decimal latitude = 53.59m,
        decimal longitude = -2.29m,
        int mediaCount = 1,
        string title = "Close pass") =>
        new(
            title,
            null,
            IncidentCategory.ClosePass,
            incidentAt ?? Now.AddHours(-2),
            latitude,
            longitude,
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

    [Theory]
    [InlineData(51.5074, -0.1278)] // London
    [InlineData(53.5933, -2.2966)] // Bury
    [InlineData(51.1279, 1.3134)] // Dover
    [InlineData(50.066, -5.714)] // Land's End
    [InlineData(55.9533, -3.1883)] // Edinburgh
    [InlineData(58.6400, -3.0700)] // John o' Groats
    [InlineData(54.5973, -5.9301)] // Belfast
    [InlineData(54.9966, -7.3086)] // Derry
    [InlineData(54.8273, -7.4633)] // Strabane
    [InlineData(54.1751, -6.3402)] // Newry
    [InlineData(54.3438, -7.6315)] // Enniskillen
    [InlineData(50.7008, -1.2933)] // Isle of Wight
    [InlineData(53.3094, -4.6330)] // Holyhead, Anglesey
    [InlineData(58.9810, -2.9605)] // Kirkwall, Orkney
    [InlineData(60.1545, -1.1494)] // Lerwick, Shetland
    [InlineData(58.2093, -6.3865)] // Stornoway, Lewis
    [InlineData(56.9800, -7.4600)] // Barra
    [InlineData(57.4125, -6.1946)] // Portree, Skye
    [InlineData(55.7570, -6.2880)] // Islay
    [InlineData(49.9145, -6.3150)] // St Mary's, Scilly
    public void Incidents_anywhere_in_the_UK_are_accepted(double latitude, double longitude)
    {
        Assert.True(_create.Validate(Command(latitude: (decimal)latitude, longitude: (decimal)longitude)).IsValid);
    }

    [Theory]
    [InlineData(53.3498, -6.2603)] // Dublin
    [InlineData(54.0090, -6.4049)] // Dundalk
    [InlineData(54.9558, -7.7342)] // Letterkenny
    [InlineData(54.8320, -7.4860)] // Lifford, across the river from Strabane
    [InlineData(54.2492, -6.9683)] // Monaghan
    [InlineData(55.3800, -7.3700)] // Malin Head
    [InlineData(54.0880, -6.2600)] // Omeath, across Carlingford Lough from Warrenpoint
    [InlineData(50.9513, 1.8587)] // Calais
    [InlineData(50.7264, 1.6147)] // Boulogne
    [InlineData(48.8566, 2.3522)] // Paris
    [InlineData(52.3676, 4.9041)] // Amsterdam
    [InlineData(54.1523, -4.4861)] // Douglas, Isle of Man
    [InlineData(49.1868, -2.1060)] // St Helier, Jersey
    [InlineData(49.4552, -2.5363)] // St Peter Port, Guernsey
    [InlineData(40.7128, -74.0060)] // New York
    public void Incidents_outside_the_UK_are_refused(double latitude, double longitude)
    {
        var result = _create.Validate(Command(latitude: (decimal)latitude, longitude: (decimal)longitude));

        var error = Assert.Single(result.Errors);
        Assert.Equal("Location", error.PropertyName);
        Assert.Equal("Bad Jockeys only accepts incidents in the UK for now.", error.ErrorMessage);
    }

    [Fact]
    public void Crown_dependencies_can_be_allowed_in_settings()
    {
        var validator = new CreatePostCommandValidator(
            new FixedTimeProvider(Now),
            Options.Create(new PostsOptions { AllowedRegions = ["GB", "IM", "JE", "GG"] }));

        Assert.True(validator.Validate(Command(latitude: 54.1523m, longitude: -4.4861m)).IsValid);
        Assert.True(validator.Validate(Command(latitude: 49.1868m, longitude: -2.1060m)).IsValid);
        Assert.True(validator.Validate(Command(latitude: 49.4552m, longitude: -2.5363m)).IsValid);
        Assert.False(validator.Validate(Command(latitude: 53.3498m, longitude: -6.2603m)).IsValid);
    }

    [Fact]
    public void Regions_are_checked_at_startup()
    {
        var validator = new PostsOptionsValidator();

        Assert.True(validator.Validate(null, new PostsOptions()).Succeeded);
        Assert.True(validator.Validate(null, new PostsOptions { AllowedRegions = ["gb", "IM"] }).Succeeded);
        Assert.True(validator.Validate(null, new PostsOptions { AllowedRegions = ["FR"] }).Failed);
        Assert.Equal(new[] { "GB" }, new PostsOptions().EffectiveRegions);
    }

    [Fact]
    public void The_region_check_uses_the_pin_before_rounding()
    {
        Assert.Equal(ServiceRegions.UnitedKingdom, ServiceRegions.RegionOf(54.8273m, -7.4633m));
        Assert.Null(ServiceRegions.RegionOf(54.8320m, -7.4860m));
    }
}
