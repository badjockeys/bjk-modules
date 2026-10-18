using BJK.Modules.Posts.Domain;
using FluentValidation;

namespace BJK.Modules.Posts.UseCases;

/// <summary>The fields a post's author writes, shared by creating and editing a post.</summary>
public interface IPostDetails
{
    string Title { get; }

    string? Description { get; }

    IncidentCategory Category { get; }

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

    protected PostDetailsValidator(TimeProvider clock)
    {
        RuleFor(x => x.Title)
            .NotEmpty()
            .Must(t => t is null || t.Trim().Length <= Post.TitleMaxLength)
            .WithMessage($"Titles are {Post.TitleMaxLength} characters or fewer.");
        RuleFor(x => x.Description)
            .Must(d => d is null || d.Trim().Length <= Post.DescriptionMaxLength)
            .WithMessage($"Descriptions are {Post.DescriptionMaxLength} characters or fewer.");
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.IncidentAt)
            .Must(at => at <= clock.GetUtcNow() + ClockSkew)
            .WithMessage("The incident can't be in the future.")
            .GreaterThanOrEqualTo(Earliest)
            .WithMessage("Check the incident date.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(x => x.PlaceName)
            .Must(p => p is null || p.Trim().Length <= Post.PlaceNameMaxLength)
            .WithMessage($"Place names are {Post.PlaceNameMaxLength} characters or fewer.");
    }
}

internal static class PostDetailsExtensions
{
    public static PostDetails ToDetails(this IPostDetails d) =>
        new(d.Title, d.Description, d.Category, d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName);
}
