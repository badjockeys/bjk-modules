using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using FluentValidation.Results;

namespace BJK.Modules.User.UseCases;

internal static class AccountErrors
{
    /// <summary>Turns identity server errors (weak password, email taken) into a 400 validation response.</summary>
    public static void ThrowIfFailed(AccountResult result)
    {
        if (!result.Succeeded)
        {
            throw new ValidationException(result.Errors.Select(e => new ValidationFailure(e.Field, e.Message)));
        }
    }
}
