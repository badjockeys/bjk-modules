using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Cars;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Household cars (owner, 22 Oct 2026): "Filmed from: Dave's Golf", no registrations.</summary>
public sealed class HouseholdCarTests
{
    private readonly IHouseholdCarRepository _cars = Substitute.For<IHouseholdCarRepository>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public HouseholdCarTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _cars.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<HouseholdCar>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_car_without_a_nickname_is_called_by_its_make_and_model()
    {
        var car = await Save().Handle(new SaveCarCommand(null, "volkswagen", "golf", VehicleColour.Blue, null), Ct);

        Assert.Equal("Volkswagen", car.Make);
        Assert.Equal("Golf", car.Model);
        Assert.Equal("Volkswagen Golf", car.Nickname);
        _cars.Received(1).Add(Arg.Is<HouseholdCar>(c => c.UserId == UserId && c.Nickname == "Volkswagen Golf"));
    }

    [Fact]
    public void Makes_and_models_come_from_the_lists_and_nicknames_hold_no_personal_details()
    {
        var validator = new SaveCarCommandValidator();

        Assert.True(validator.Validate(new SaveCarCommand(null, "Volkswagen", "Golf", null, "Dave's Golf")).IsValid);
        Assert.False(validator.Validate(new SaveCarCommand(null, "Batmobile", null, null, null)).IsValid);
        Assert.False(validator.Validate(new SaveCarCommand(null, "Volkswagen", "Cortina", null, null)).IsValid);
        Assert.False(validator.Validate(new SaveCarCommand(null, "Volkswagen", "Golf", null, "AB12 CDE")).IsValid);
    }

    [Fact]
    public async Task Renaming_a_car_renames_it_on_its_posts()
    {
        var car = HouseholdCar.Create(UserId, "Volkswagen", "Golf", null, null, Now);
        _cars.GetAsync(car.Id, Arg.Any<CancellationToken>()).Returns(car);

        await Save().Handle(new SaveCarCommand(car.Id, "Volkswagen", "Golf", null, "Dave's Golf"), Ct);

        await _cars.Received(1).RenameOnPostsAsync(car.Id, "Dave's Golf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_elses_car_cant_be_changed_or_used()
    {
        var theirs = HouseholdCar.Create(OtherUserId, "Ford", "Focus", null, null, Now);
        _cars.GetAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);

        await Assert.ThrowsAsync<NotFoundException>(() => Save().Handle(new SaveCarCommand(theirs.Id, "Ford", "Focus", null, "Mine now"), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => CarViews.FilmedFromAsync(_cars, UserId, theirs.Id, Ct));
        Assert.Throws<ArgumentException>(() => NewPost(UserId).SetFilmedFrom(theirs, Now));
    }

    [Fact]
    public async Task There_are_at_most_five_cars()
    {
        _cars.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            Enumerable.Range(0, HouseholdCar.MaxPerUser).Select(_ => HouseholdCar.Create(UserId, "Ford", null, null, null, Now)).ToList());

        await Assert.ThrowsAsync<ValidationException>(() => Save().Handle(new SaveCarCommand(null, "Ford", "Focus", null, null), Ct));
    }

    [Fact]
    public void A_post_shows_the_car_it_was_filmed_from()
    {
        var car = HouseholdCar.Create(UserId, "Volkswagen", "Golf", null, "Dave's Golf", Now);
        var post = NewPost(UserId);

        post.SetFilmedFrom(car, Now);
        Assert.Equal("Dave's Golf", post.FilmedFrom);
        Assert.Equal(car.Id, post.FilmedFromCarId);

        post.SetFilmedFrom(null, Now);
        Assert.Null(post.FilmedFrom);
    }

    private SaveCarCommandHandler Save() => new(_cars, _caller, _clock);
}
