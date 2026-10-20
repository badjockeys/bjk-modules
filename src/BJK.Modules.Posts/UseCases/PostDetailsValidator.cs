using BJK.Modules.Posts.Domain;
using BJK.Modules.Shared.Safety;
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
    }
}

internal static class PostDetailsExtensions
{
    public static PostDetails ToDetails(this IPostDetails d)
    {
        var types = IncidentTypeFlags.Normalise(d.Types, d.Category);
        return new(d.Title, d.Description, types[0], d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName, types);
    }
}
