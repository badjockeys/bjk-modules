using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.UseCases.AgeChecks;

/// <summary>The signed-in user's age check status and what they can do next.</summary>
public sealed record GetMyAgeCheckQuery : IRequest<AgeCheckDto>;

public sealed class GetMyAgeCheckQueryHandler(
    IUserRepository users,
    ICurrentUser caller,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock)
    : IRequestHandler<GetMyAgeCheckQuery, AgeCheckDto>
{
    public async Task<AgeCheckDto> Handle(GetMyAgeCheckQuery request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        return AgeCheckDto.From(account, clock.GetUtcNow(), options.Value.MaxAttemptsPerDay);
    }
}

/// <summary>
/// "Verify your age to view sensitive posts" with a head-turn selfie video: the highly effective age check, taken
/// on demand (not at sign-up). The liveness check runs first; if it passes, the age estimator decides: 25 or over
/// passes, 18 to 24 (or a pass the estimator can't call) leads to photo ID, clearly under 18 fails. Not offered at
/// all when the declared date of birth says the user is under 18. The video is only held in memory for this request
/// and is wiped afterwards; only the outcome is saved.
/// </summary>
/// <param name="Video">The recorded video. Wiped (zeroed) once the check is done.</param>
/// <param name="ContentType">video/webm, video/mp4 or video/quicktime.</param>
public sealed record SubmitFaceAgeCheckCommand(byte[] Video, string ContentType) : IRequest<AgeCheckDto>
{
    /// <summary>Never prints the video.</summary>
    public override string ToString() => $"{nameof(SubmitFaceAgeCheckCommand)}({ContentType}, {Video.Length} bytes)";
}

public sealed class SubmitFaceAgeCheckCommandValidator : AbstractValidator<SubmitFaceAgeCheckCommand>
{
    public static readonly string[] ContentTypes = ["video/webm", "video/mp4", "video/quicktime"];

    public SubmitFaceAgeCheckCommandValidator(IOptions<AgeAssuranceOptions> options)
    {
        RuleFor(c => c.Video)
            .NotEmpty().WithMessage("Please record a short video.")
            .Must(v => v.LongLength <= options.Value.MaxVideoBytes)
            .WithMessage("That video is too long. Record about 5 seconds.");
        RuleFor(c => c.ContentType)
            .Must(t => ContentTypes.Any(allowed => BaseType(t).Equals(allowed, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("That video format isn't supported. Please use a recent version of Chrome, Safari, Edge or Firefox.");
    }

    internal static string BaseType(string? contentType) => (contentType ?? string.Empty).Split(';')[0].Trim();
}

public sealed class SubmitFaceAgeCheckCommandHandler(
    IUserRepository users,
    ICurrentUser caller,
    AgeAssuranceProviders providers,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock,
    ILogger<SubmitFaceAgeCheckCommandHandler> logger)
    : IRequestHandler<SubmitFaceAgeCheckCommand, AgeCheckDto>
{
    public async Task<AgeCheckDto> Handle(SubmitFaceAgeCheckCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = options.Value;
            var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
            AgeCheckGuards.EnsureNotPassed(account);
            AgeCheckGuards.EnsureAdultByDateOfBirth(account, AgeRules.Today(clock));
            if (!account.CanUseFaceCheck)
            {
                throw AgeCheckGuards.Invalid("Please use photo ID to finish checking your age.");
            }

            var now = clock.GetUtcNow();
            AgeCheckGuards.StartAttempt(account, now, settings.MaxAttemptsPerDay);

            var video = new SelfieVideo(request.Video, SubmitFaceAgeCheckCommandValidator.BaseType(request.ContentType));
            var liveness = providers.Liveness;
            var live = await liveness.CheckAsync(video, cancellationToken);
            if (!live.IsLive)
            {
                account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.LivenessFailed, liveness.Name, now);
                await users.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Age check {Reference} for account {UserId}: liveness failed ({Reason}).", account.AgeCheckReference, account.Id, live.Failure);
                return AgeCheckDto.From(account, now, settings.MaxAttemptsPerDay, live.Failure);
            }

            var estimate = await providers.Estimator.EstimateAsync(video, cancellationToken);
            var result = AgeCheckRules.DecideFaceEstimate(estimate.Age, estimate.Uncertainty, settings.ChallengeAge);
            account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, result, estimate.Provider, now);
            await users.SaveChangesAsync(cancellationToken);

            // The outcome only; never the estimated age.
            logger.LogInformation(
                "Age check {Reference} for account {UserId}: face estimate {Result}.", account.AgeCheckReference, account.Id, result);
            return AgeCheckDto.From(account, now, settings.MaxAttemptsPerDay);
        }
        finally
        {
            // Done with the video: don't leave it lying around in memory.
            Array.Clear(request.Video);
        }
    }
}

/// <summary>
/// Photo-ID route of the age check: after the selfie said 18 to 24, couldn't tell, or the user chose it instead. 18 or over passes;
/// under 18 fails for good (until an admin resets it). The image is wiped afterwards; only the outcome is saved.
/// </summary>
/// <param name="Image">The ID photo. Wiped (zeroed) once the check is done.</param>
/// <param name="ContentType">image/jpeg, image/png, image/webp or image/heic.</param>
public sealed record SubmitDocumentAgeCheckCommand(byte[] Image, string ContentType) : IRequest<AgeCheckDto>
{
    /// <summary>Never prints the image.</summary>
    public override string ToString() => $"{nameof(SubmitDocumentAgeCheckCommand)}({ContentType}, {Image.Length} bytes)";
}

public sealed class SubmitDocumentAgeCheckCommandValidator : AbstractValidator<SubmitDocumentAgeCheckCommand>
{
    public static readonly string[] ContentTypes = ["image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"];

    public SubmitDocumentAgeCheckCommandValidator(IOptions<AgeAssuranceOptions> options)
    {
        RuleFor(c => c.Image)
            .NotEmpty().WithMessage("Please choose a photo of your ID.")
            .Must(i => i.LongLength <= options.Value.MaxImageBytes)
            .WithMessage("That photo is too large. Please use one under 10 MB.");
        RuleFor(c => c.ContentType)
            .Must(t => ContentTypes.Any(allowed => SubmitFaceAgeCheckCommandValidator.BaseType(t).Equals(allowed, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Please use a JPEG, PNG, WebP or HEIC photo.");
    }
}

public sealed class SubmitDocumentAgeCheckCommandHandler(
    IUserRepository users,
    ICurrentUser caller,
    AgeAssuranceProviders providers,
    IOptions<AgeAssuranceOptions> options,
    TimeProvider clock,
    ILogger<SubmitDocumentAgeCheckCommandHandler> logger)
    : IRequestHandler<SubmitDocumentAgeCheckCommand, AgeCheckDto>
{
    public async Task<AgeCheckDto> Handle(SubmitDocumentAgeCheckCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = options.Value;
            var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
            AgeCheckGuards.EnsureNotPassed(account);
            AgeCheckGuards.EnsureAdultByDateOfBirth(account, AgeRules.Today(clock));
            if (!account.CanUseDocumentCheck)
            {
                throw AgeCheckGuards.Invalid(
                    "We can't check your age again. If you think we've got this wrong, please contact support.");
            }

            var now = clock.GetUtcNow();
            AgeCheckGuards.StartAttempt(account, now, settings.MaxAttemptsPerDay);

            var document = providers.Document;
            var checkResult = await document.CheckAsync(
                new DocumentImage(request.Image, SubmitFaceAgeCheckCommandValidator.BaseType(request.ContentType)),
                cancellationToken);
            var result = AgeCheckRules.DecideDocument(checkResult.Age);
            account.RecordAgeCheck(AgeCheckMethod.Document, result, checkResult.Provider, now);
            await users.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Age check {Reference} for account {UserId}: photo ID {Result}.", account.AgeCheckReference, account.Id, result);
            return AgeCheckDto.From(account, now, settings.MaxAttemptsPerDay);
        }
        finally
        {
            Array.Clear(request.Image);
        }
    }
}

/// <summary>
/// Admins: clear an account's age check so the user takes it again (for example after a mistaken result). Their
/// next tokens no longer carry age_verified until they pass again.
/// </summary>
/// <param name="UserId">The account to reset.</param>
public sealed record ResetAgeCheckCommand(Guid UserId) : IRequest<UserDto>;

public sealed class ResetAgeCheckCommandHandler(
    IUserRepository users,
    IIdentityService identity,
    ICurrentUser caller,
    TimeProvider clock,
    ILogger<ResetAgeCheckCommandHandler> logger)
    : IRequestHandler<ResetAgeCheckCommand, UserDto>
{
    public async Task<UserDto> Handle(ResetAgeCheckCommand request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(UserModule.ManageScope);
        var account = await users.GetByIdAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("User", request.UserId);

        account.ResetAgeCheck(clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Age check for account {UserId} reset by {AdminId}.", account.Id, caller.IdentityId ?? "an admin client");

        return UserDto.From(account, await identity.GetAccountAsync(account.Id.ToString("D"), cancellationToken));
    }
}

internal static class AgeCheckGuards
{
    public static void EnsureNotPassed(UserAccount account)
    {
        if (account.IsAgeVerified)
        {
            throw Invalid("You've already confirmed your age.");
        }
    }

    /// <summary>Under 18 by the declared date of birth: sensitive posts aren't for them, so there's nothing to check.</summary>
    public static void EnsureAdultByDateOfBirth(UserAccount account, DateOnly today)
    {
        if (account.DateOfBirth is null)
        {
            throw Invalid(AgeCheckMessages.NoDateOfBirth);
        }

        if (account.IsMinorOn(today))
        {
            throw Invalid(AgeCheckMessages.UnderAgeByDateOfBirth);
        }
    }

    public static void StartAttempt(UserAccount account, DateTimeOffset now, int maxPerDay)
    {
        if (!account.TryStartAgeCheckAttempt(now, maxPerDay))
        {
            throw Invalid($"You've used all {maxPerDay} tries for today. Please come back tomorrow, or contact support.");
        }
    }

    public static ValidationException Invalid(string message) =>
        new([new ValidationFailure("AgeCheck", message)]);
}
