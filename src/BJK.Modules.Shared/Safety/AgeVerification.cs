using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Safety;

/// <summary>
/// Marks a command that needs an age-verified user when <see cref="SafetyOptions.RequireAgeVerification"/> is on
/// (it's off by default): creating or editing a post, commenting, reacting and reporting as a signed-in user.
/// </summary>
public interface IRequiresAgeVerification
{
}

/// <summary>
/// Refuses <see cref="IRequiresAgeVerification"/> commands from signed-in users whose token lacks the
/// <c>age_verified</c> claim, with a 403 problem response the apps recognise by its <c>code</c>
/// (<see cref="AgeVerificationRequiredException.Code"/>) and use to send the user to the age check.
/// Runs before validation.
/// </summary>
public sealed class AgeVerificationBehaviour<TRequest, TResponse>(ICurrentUser caller, IOptions<SafetyOptions> options)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Anonymous and machine callers are refused by the handler itself; this only concerns signed-in users.
        if (request is IRequiresAgeVerification
            && options.Value.RequireAgeVerification
            && caller.UserId() is not null
            && !caller.AgeVerified)
        {
            throw new AgeVerificationRequiredException();
        }

        return next(cancellationToken);
    }
}

/// <summary>The user hasn't passed the age check yet. Returned as 403 with <c>code</c> = <see cref="Code"/>.</summary>
public sealed class AgeVerificationRequiredException()
    : Exception("Please verify your age (18 or over) before you post, comment, react or report.")
{
    public const string Code = "age_verification_required";

    public const string ProblemType = "https://badjockeys.com/problems/age-verification-required";
}
