using BJK.Modules.Profiles.Domain;
using FluentValidation;

namespace BJK.Modules.Profiles.UseCases;

/// <summary>The editable details shared by creating and updating a profile.</summary>
public interface IProfileDetails
{
    string DisplayName { get; }

    string? Bio { get; }

    string? Location { get; }
}

/// <summary>Validation rules for <see cref="IProfileDetails"/>, shared by the create and update validators.</summary>
public abstract class ProfileDetailsValidator<T> : AbstractValidator<T>
    where T : IProfileDetails
{
    protected ProfileDetailsValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .Must(n => n is null || n.Trim().Length <= Profile.DisplayNameMaxLength)
            .WithMessage($"Display names are {Profile.DisplayNameMaxLength} characters or fewer.");
        RuleFor(x => x.Bio)
            .Must(b => b is null || b.Trim().Length <= Profile.BioMaxLength)
            .WithMessage($"Bios are {Profile.BioMaxLength} characters or fewer.");
        RuleFor(x => x.Location)
            .Must(l => l is null || l.Trim().Length <= Profile.LocationMaxLength)
            .WithMessage($"Locations are {Profile.LocationMaxLength} characters or fewer.");
    }
}

internal static class ProfileRules
{
    public static IRuleBuilderOptions<T, string> ValidHandle<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .Must(h => Handle.IsValid(Handle.Normalise(h)))
            .WithMessage((_, h) => Handle.Problem(Handle.Normalise(h)) ?? "That handle isn't allowed.");

    public static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
