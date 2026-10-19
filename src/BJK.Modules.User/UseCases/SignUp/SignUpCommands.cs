using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.AgeChecks;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.UseCases.SignUp;

/// <summary>
/// Where the signed-in user is in finishing sign-up: date of birth, then the liveness check. Used by the identity
/// server's sign-up pages, with the sign-in cookie (an unfinished account can't get tokens).
/// </summary>
public sealed record GetMySignUpQuery : IRequest<SignUpStatusDto>;

public sealed class GetMySignUpQueryHandler(
    IUserRepository users,
    ICurrentUser caller,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock)
    : IRequestHandler<GetMySignUpQuery, SignUpStatusDto>
{
    public async Task<SignUpStatusDto> Handle(GetMySignUpQuery request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        return SignUpStatusDto.From(account, clock.GetUtcNow(), options.Value.MaxAttemptsPerDay);
    }
}

/// <summary>
/// Records the date of birth of an account created before dates of birth were asked for (asked at its next
/// sign-in). Under 13 is refused and not stored. Once recorded, only support can change it.
/// </summary>
/// <param name="DateOfBirth">The declared date of birth: 13 or over.</param>
public sealed record SetMyDateOfBirthCommand(DateOnly? DateOfBirth) : IRequest<SignUpStatusDto>
{
    /// <summary>Never prints the date of birth.</summary>
    public override string ToString() => nameof(SetMyDateOfBirthCommand);
}

public sealed class SetMyDateOfBirthCommandValidator : AbstractValidator<SetMyDateOfBirthCommand>
{
    public SetMyDateOfBirthCommandValidator(TimeProvider clock)
    {
        RuleFor(c => c.DateOfBirth).ValidDateOfBirth(clock);
    }
}

public sealed class SetMyDateOfBirthCommandHandler(
    IUserRepository users,
    ICurrentUser caller,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock)
    : IRequestHandler<SetMyDateOfBirthCommand, SignUpStatusDto>
{
    public async Task<SignUpStatusDto> Handle(SetMyDateOfBirthCommand request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        if (account.DateOfBirth is not null)
        {
            throw SignUpErrors.Invalid(
                nameof(SetMyDateOfBirthCommand.DateOfBirth),
                "We already have your date of birth. If it's wrong, please contact support.");
        }

        var now = clock.GetUtcNow();
        account.SetDateOfBirth(
            request.DateOfBirth ?? throw new InvalidOperationException("The validator requires a date of birth."),
            now);
        await users.SaveChangesAsync(cancellationToken);
        return SignUpStatusDto.From(account, now, options.Value.MaxAttemptsPerDay);
    }
}

/// <summary>
/// The liveness check at sign-up: a 3–5 second head-turn selfie video, checked on our own servers (YuNet) to show
/// one real person, to stop bots and fake accounts. No age estimate. 3 tries a day. The video is only held in
/// memory for this request and wiped afterwards; only the result and the date are saved.
/// </summary>
/// <param name="Video">The recorded video. Wiped (zeroed) once the check is done.</param>
/// <param name="ContentType">video/webm, video/mp4 or video/quicktime.</param>
public sealed record SubmitSignUpLivenessCommand(byte[] Video, string ContentType) : IRequest<SignUpStatusDto>
{
    /// <summary>Never prints the video.</summary>
    public override string ToString() => $"{nameof(SubmitSignUpLivenessCommand)}({ContentType}, {Video.Length} bytes)";
}

public sealed class SubmitSignUpLivenessCommandValidator : AbstractValidator<SubmitSignUpLivenessCommand>
{
    public SubmitSignUpLivenessCommandValidator(IOptions<AgeAssuranceOptions> options)
    {
        RuleFor(c => c.Video)
            .NotEmpty().WithMessage("Please record a short video.")
            .Must(v => v.LongLength <= options.Value.MaxVideoBytes)
            .WithMessage("That video is too long. Record about 5 seconds.");
        RuleFor(c => c.ContentType)
            .Must(t => SubmitFaceAgeCheckCommandValidator.ContentTypes.Any(
                allowed => SubmitFaceAgeCheckCommandValidator.BaseType(t).Equals(allowed, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("That video format isn't supported. Please use a recent version of Chrome, Safari, Edge or Firefox.");
    }
}

public sealed class SubmitSignUpLivenessCommandHandler(
    IUserRepository users,
    ICurrentUser caller,
    AgeAssuranceProviders providers,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock,
    ILogger<SubmitSignUpLivenessCommandHandler> logger)
    : IRequestHandler<SubmitSignUpLivenessCommand, SignUpStatusDto>
{
    public async Task<SignUpStatusDto> Handle(SubmitSignUpLivenessCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = options.Value;
            var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
            if (account.HasPassedLiveness)
            {
                throw SignUpErrors.Invalid("Liveness", "You've already done this check. You're all set.");
            }

            var now = clock.GetUtcNow();
            if (!account.TryStartLivenessAttempt(now, settings.MaxAttemptsPerDay))
            {
                throw SignUpErrors.Invalid(
                    "Liveness",
                    $"You've used all {settings.MaxAttemptsPerDay} tries for today. Please come back tomorrow, or contact support.");
            }

            var liveness = providers.Liveness;
            var result = await liveness.CheckAsync(
                new SelfieVideo(request.Video, SubmitFaceAgeCheckCommandValidator.BaseType(request.ContentType)),
                cancellationToken);
            account.RecordLiveness(result.IsLive, now);
            await users.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Sign-up liveness for account {UserId}: {Result}{Reason}.",
                account.Id,
                result.IsLive ? "passed" : "failed",
                result.IsLive ? string.Empty : $" ({result.Failure})");
            return SignUpStatusDto.From(account, now, settings.MaxAttemptsPerDay, result.Failure);
        }
        finally
        {
            // Done with the video: don't leave it lying around in memory.
            Array.Clear(request.Video);
        }
    }
}

/// <summary>The signed-in user's progress through sign-up.</summary>
/// <param name="HasDateOfBirth">True once a date of birth is recorded.</param>
/// <param name="LivenessStatus">NotChecked, Passed or Failed.</param>
/// <param name="LivenessCheckedAt">When the latest liveness result was recorded.</param>
/// <param name="AttemptsLeft">Liveness tries left in the current 24 hours.</param>
/// <param name="IsComplete">True once sign-up is finished: the account can get tokens.</param>
/// <param name="Message">A plain-English explanation of the latest liveness result, for the user.</param>
public sealed record SignUpStatusDto(
    bool HasDateOfBirth,
    LivenessStatus LivenessStatus,
    DateTimeOffset? LivenessCheckedAt,
    int AttemptsLeft,
    bool IsComplete,
    string? Message)
{
    /// <summary>True if the user can record another liveness video now.</summary>
    public bool CanTryLiveness => LivenessStatus != LivenessStatus.Passed && AttemptsLeft > 0;

    internal static SignUpStatusDto From(UserAccount account, DateTimeOffset now, int maxPerDay, LivenessFailure? failure = null)
    {
        var attemptsLeft = account.LivenessAttemptsLeft(now, maxPerDay);
        return new SignUpStatusDto(
            account.DateOfBirth is not null,
            account.LivenessStatus,
            account.LivenessCheckedAt,
            attemptsLeft,
            account.IsSignUpComplete,
            Describe(account, attemptsLeft, failure));
    }

    private static string? Describe(UserAccount account, int attemptsLeft, LivenessFailure? failure) => account.LivenessStatus switch
    {
        LivenessStatus.Passed => "Thanks, that's you checked. Welcome to Bad Jockeys!",
        LivenessStatus.Failed => LivenessMessages.Hint(failure)
            + (attemptsLeft == 0
                ? " You've used all your tries for today; please come back tomorrow."
                : $" You have {attemptsLeft} {(attemptsLeft == 1 ? "try" : "tries")} left today."),
        _ => null,
    };
}

/// <summary>The date of birth rules every sign-up route shares.</summary>
public static class DateOfBirthValidation
{
    /// <summary>
    /// Required, believable (not in the future, not over 120 years ago) and 13 or over. Under 13 fails with
    /// <see cref="AgeRules.TooYoungErrorCode"/> and a kind message.
    /// </summary>
    /// <typeparam name="T">The command.</typeparam>
    /// <param name="rule">The date of birth rule.</param>
    /// <param name="clock">Today's date comes from here.</param>
    public static IRuleBuilderOptions<T, DateOnly?> ValidDateOfBirth<T>(this IRuleBuilder<T, DateOnly?> rule, TimeProvider clock) =>
        rule.NotNull().WithMessage("Please enter your date of birth.")
            .Must(d => d is not { } date || AgeRules.IsPlausible(date, AgeRules.Today(clock)))
            .WithMessage("Please check your date of birth.")
            .Must(d => d is not { } date || !AgeRules.IsPlausible(date, AgeRules.Today(clock)) || AgeRules.IsOldEnoughToJoin(date, AgeRules.Today(clock)))
            .WithMessage(AgeRules.TooYoungMessage)
            .WithErrorCode(AgeRules.TooYoungErrorCode);
}

internal static class SignUpErrors
{
    public static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);
}
