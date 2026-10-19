using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace BJK.Modules.User.UseCases.SignUp;

/// <summary>
/// Whether the signed-in user's account has finished sign-up (a date of birth on record). Used by the identity
/// server's sign-in pages, with the sign-in cookie (an account with no date of birth can't get tokens).
/// </summary>
public sealed record GetMySignUpQuery : IRequest<SignUpStatusDto>;

public sealed class GetMySignUpQueryHandler(IUserRepository users, ICurrentUser caller)
    : IRequestHandler<GetMySignUpQuery, SignUpStatusDto>
{
    public async Task<SignUpStatusDto> Handle(GetMySignUpQuery request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        return SignUpStatusDto.From(account);
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

public sealed class SetMyDateOfBirthCommandHandler(IUserRepository users, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SetMyDateOfBirthCommand, SignUpStatusDto>
{
    public async Task<SignUpStatusDto> Handle(SetMyDateOfBirthCommand request, CancellationToken cancellationToken)
    {
        var account = await UserAccess.GetMineAsync(users, caller, cancellationToken);
        if (account.DateOfBirth is not null)
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    nameof(SetMyDateOfBirthCommand.DateOfBirth),
                    "We already have your date of birth. If it's wrong, please contact support."),
            ]);
        }

        account.SetDateOfBirth(
            request.DateOfBirth ?? throw new InvalidOperationException("The validator requires a date of birth."),
            clock.GetUtcNow());
        await users.SaveChangesAsync(cancellationToken);
        return SignUpStatusDto.From(account);
    }
}

/// <summary>The signed-in user's sign-up status.</summary>
/// <param name="HasDateOfBirth">True once a date of birth is recorded.</param>
/// <param name="IsComplete">True once sign-up is finished: the account can get tokens.</param>
public sealed record SignUpStatusDto(bool HasDateOfBirth, bool IsComplete)
{
    internal static SignUpStatusDto From(UserAccount account) =>
        new(account.DateOfBirth is not null, account.IsSignUpComplete);
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
