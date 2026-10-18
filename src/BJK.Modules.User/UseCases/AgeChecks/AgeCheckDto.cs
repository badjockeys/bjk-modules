using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.AgeAssurance;

namespace BJK.Modules.User.UseCases.AgeChecks;

/// <summary>The signed-in user's age check, and what they can do next.</summary>
/// <param name="Status">Where they are: NotChecked, DocumentRequired, Passed or Failed.</param>
/// <param name="Method">How the status was reached.</param>
/// <param name="Result">The most recent attempt's outcome.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="AttemptsLeft">Attempts left in the current 24 hours.</param>
/// <param name="CanUseFaceCheck">True if the head-turn selfie is available now.</param>
/// <param name="CanUseDocumentCheck">True if the photo-ID route is available now.</param>
/// <param name="Message">A plain-English explanation of the latest outcome, for the user.</param>
public sealed record AgeCheckDto(
    AgeCheckStatus Status,
    AgeCheckMethod? Method,
    AgeCheckResult? Result,
    DateTimeOffset? RecordedAt,
    int AttemptsLeft,
    bool CanUseFaceCheck,
    bool CanUseDocumentCheck,
    string? Message)
{
    internal static AgeCheckDto From(UserAccount account, DateTimeOffset now, int maxPerDay, LivenessFailure? livenessFailure = null)
    {
        var attemptsLeft = account.IsAgeVerified ? 0 : account.AgeCheckAttemptsLeft(now, maxPerDay);
        return new AgeCheckDto(
            account.AgeCheckStatus,
            account.AgeCheckMethod,
            account.AgeCheckResult,
            account.AgeCheckRecordedAt,
            attemptsLeft,
            account.CanUseFaceCheck && attemptsLeft > 0,
            account.CanUseDocumentCheck && attemptsLeft > 0,
            AgeCheckMessages.For(account, attemptsLeft, livenessFailure));
    }
}

/// <summary>Age check status for staff (never the video, photo or estimate).</summary>
/// <param name="Status">Where the account is.</param>
/// <param name="Method">How the status was reached.</param>
/// <param name="Result">The most recent attempt's outcome.</param>
/// <param name="Provider">Which provider gave it.</param>
/// <param name="RecordedAt">When.</param>
public sealed record AgeCheckSummaryDto(
    AgeCheckStatus Status,
    AgeCheckMethod? Method,
    AgeCheckResult? Result,
    string? Provider,
    DateTimeOffset? RecordedAt)
{
    internal static AgeCheckSummaryDto From(UserAccount account) => new(
        account.AgeCheckStatus,
        account.AgeCheckMethod,
        account.AgeCheckResult,
        account.AgeCheckProvider,
        account.AgeCheckRecordedAt);
}

/// <summary>What we tell the user. Kind and plain; never mentions an estimated age.</summary>
internal static class AgeCheckMessages
{
    public static string? For(UserAccount account, int attemptsLeft, LivenessFailure? livenessFailure)
    {
        if (account.AgeCheckResult is null)
        {
            return null;
        }

        var outOfTries = attemptsLeft == 0
            ? " You've used all your tries for today; please come back tomorrow."
            : string.Empty;

        return account.AgeCheckResult switch
        {
            AgeCheckResult.Passed => "Thanks, you're all set. You can now post, comment and react.",
            AgeCheckResult.Under25 =>
                "Thanks. We couldn't confirm your age from the video alone, so please finish with a photo of your ID." + outOfTries,
            AgeCheckResult.Uncertain =>
                "Sorry, we couldn't get a clear enough look to check your age. Try again in good light, or use photo ID instead." + outOfTries,
            AgeCheckResult.LivenessFailed => LivenessHint(livenessFailure) + " Or use photo ID instead." + outOfTries,
            AgeCheckResult.DocumentUnreadable =>
                "Sorry, we couldn't read that ID. Try a sharp, well-lit photo of the whole card or page, with no glare." + outOfTries,
            AgeCheckResult.Underage when account.AgeCheckStatus == AgeCheckStatus.Failed && account.CanUseDocumentCheck =>
                "Bad Jockeys is only for people aged 18 and over, and we couldn't confirm you're 18 yet. "
                + "If you are, you can use photo ID instead." + outOfTries,
            AgeCheckResult.Underage =>
                "Bad Jockeys is only for people aged 18 and over, so we can't let you post or comment. "
                + "You can still browse the public feed. If you think we've got this wrong, contact support.",
            _ => null,
        };
    }

    private static string LivenessHint(LivenessFailure? failure) => failure switch
    {
        LivenessFailure.NoFace => "We couldn't see your face clearly. Face the camera in good light and try again.",
        LivenessFailure.MultipleFaces => "We saw more than one face. Make sure only you are in the picture and try again.",
        LivenessFailure.NoHeadTurn => "We didn't see you turn your head. Slowly turn left, then right, while recording.",
        _ => "We couldn't check that video. Please try again.",
    };
}
