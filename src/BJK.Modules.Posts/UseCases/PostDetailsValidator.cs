using BJK.Modules.Posts.Domain;
using BJK.Modules.Safety;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases;

/// <summary>The fields a post's author writes, shared by creating and editing a post.</summary>
public interface IPostDetails
{
    string Title { get; }

    string? Description { get; }

    /// <summary>The one type of bad driving, from clients that send a single category. Ignored when <see cref="Types"/> has any.</summary>
    IncidentCategory? Category { get; }

    /// <summary>Every type of bad driving that applies, the main one first. At least one (or a <see cref="Category"/>).</summary>
    IReadOnlyList<IncidentCategory>? Types { get; }

    /// <summary>The kind of vehicle (needed once a make, model or colour is given).</summary>
    VehicleType? VehicleType { get; }

    /// <summary>A make from <c>GET /api/vehicles</c>.</summary>
    string? VehicleMake { get; }

    /// <summary>One of the make's models from <c>GET /api/vehicles</c>.</summary>
    string? VehicleModel { get; }

    VehicleColour? VehicleColour { get; }

    /// <summary>Reported to the police: true, false, or null for not saying.</summary>
    bool? ReportedToPolice { get; }

    DateTimeOffset IncidentAt { get; }

    decimal Latitude { get; }

    decimal Longitude { get; }

    string? PlaceName { get; }
}

/// <summary>Validation for <see cref="IPostDetails"/>, shared by the create and update validators.</summary>
public abstract class PostDetailsValidator<T> : AbstractValidator<T>
    where T : IPostDetails
{
    /// <summary>Allows for phones whose clocks run a little fast.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Shown when the pin is outside the allowed regions.</summary>
    public const string OutsideServiceAreaMessage = "Bad Jockeys only accepts incidents in the UK for now.";

    /// <summary>Shown when no type of bad driving was chosen.</summary>
    public const string ChooseATypeMessage = "Choose at least one type of bad driving.";

    protected PostDetailsValidator(TimeProvider clock, IOptions<PostsOptions> options)
    {
        var regions = options.Value.EffectiveRegions;
        RuleFor(x => x.Title)
            .NotEmpty()
            .Must(t => t is null || t.Trim().Length <= Post.TitleMaxLength)
            .WithMessage($"Titles are {Post.TitleMaxLength} characters or fewer.");
        RuleFor(x => x.Description)
            .Must(d => d is null || d.Trim().Length <= Post.DescriptionInputMaxLength)
            .WithMessage($"Descriptions are {Post.DescriptionInputMaxLength} characters or fewer.");
        RuleFor(x => x.Category).IsInEnum().When(x => x.Category is not null);
        RuleFor(x => x)
            .Must(x => IncidentTypeFlags.Normalise(x.Types, x.Category).Count > 0)
            .OverridePropertyName(nameof(IPostDetails.Types))
            .WithMessage(ChooseATypeMessage);
        RuleForEach(x => x.Types).IsInEnum().OverridePropertyName(nameof(IPostDetails.Types));

        // Vehicle details: chosen from lists only (never typed, never the registration).
        RuleFor(x => x.VehicleType).IsInEnum().When(x => x.VehicleType is not null);
        RuleFor(x => x.VehicleColour).IsInEnum().When(x => x.VehicleColour is not null);
        RuleFor(x => x.VehicleMake)
            .Must(m => m is null || VehicleCatalogue.Make(m) is not null)
            .WithMessage("Choose a make from the list.");
        RuleFor(x => x.VehicleModel)
            .Must((x, model) => model is null || VehicleCatalogue.Model(x.VehicleMake, model) is not null)
            .WithMessage("Choose a model from the list for that make.");
        RuleFor(x => x.VehicleType)
            .NotNull()
            .When(x => x.VehicleMake is not null || x.VehicleModel is not null || x.VehicleColour is not null)
            .WithMessage("Choose the type of vehicle.");
        RuleFor(x => x.IncidentAt)
            .Must(at => at <= clock.GetUtcNow() + ClockSkew)
            .WithMessage("The incident can't be in the future.")
            .GreaterThanOrEqualTo(Earliest)
            .WithMessage("Check the incident date.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m);

        // The pin as given (before it's rounded to about 500 m) must be in an allowed region: the UK by default.
        RuleFor(x => x)
            .Must(x => ServiceRegions.IsAllowed(x.Latitude, x.Longitude, regions))
            .When(x => x.Latitude is >= -90m and <= 90m && x.Longitude is >= -180m and <= 180m)
            .OverridePropertyName("Location")
            .WithMessage(OutsideServiceAreaMessage);
        RuleFor(x => x.PlaceName)
            .Must(p => p is null || p.Trim().Length <= Post.PlaceNameMaxLength)
            .WithMessage($"Place names are {Post.PlaceNameMaxLength} characters or fewer.");

        // No number plates, phone numbers, emails, street addresses or social handles.
        RuleFor(x => x.Title).NoPersonalDetails();
        RuleFor(x => x.Description).NoPersonalDetails();
        RuleFor(x => x.PlaceName).NoPersonalDetails();

        // No links (owner, 23 Oct 2026, to keep spam out). Text already refused above isn't told twice.
        RuleFor(x => x.Title).NoWebAddresses().When(x => TextSafety.Check(x.Title) is null);
        RuleFor(x => x.Description).NoWebAddresses().When(x => TextSafety.Check(x.Description) is null);
        RuleFor(x => x.PlaceName).NoWebAddresses().When(x => TextSafety.Check(x.PlaceName) is null);
    }
}

internal static class PostDetailsExtensions
{
    public static PostDetails ToDetails(this IPostDetails d)
    {
        var types = IncidentTypeFlags.Normalise(d.Types, d.Category);
        var vehicle = d.VehicleType is null && d.VehicleMake is null && d.VehicleModel is null && d.VehicleColour is null
            ? null
            : new VehicleDetails(d.VehicleType, d.VehicleMake, d.VehicleModel, d.VehicleColour);
        return new(d.Title, d.Description, types[0], d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName, types, vehicle, d.ReportedToPolice);
    }
}
