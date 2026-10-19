using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases.Add;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class AddUserCommandValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly AddUserCommandValidator _validator = new(new FixedTimeProvider(Now));

    [Fact]
    public void Valid_command_passes()
    {
        Assert.True(_validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!", TestDates.Adult)).IsValid);
    }

    [Fact]
    public void Thirteen_today_passes()
    {
        Assert.True(_validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!", new DateOnly(2013, 10, 19))).IsValid);
    }

    [Theory]
    [InlineData("not-an-email", "Passw0rd!", nameof(AddUserCommand.Email))]
    [InlineData("", "Passw0rd!", nameof(AddUserCommand.Email))]
    [InlineData("rider@example.com", "", nameof(AddUserCommand.Password))]
    public void Invalid_fields_fail(string email, string password, string expectedProperty)
    {
        var result = _validator.Validate(new AddUserCommand(email, password, TestDates.Adult));

        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }

    [Fact]
    public void Date_of_birth_is_required()
    {
        var result = _validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!", null));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddUserCommand.DateOfBirth));
    }

    [Fact]
    public void Under_13_is_refused_kindly_with_a_code()
    {
        var result = _validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!", new DateOnly(2013, 10, 20)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(AgeRules.TooYoungErrorCode, error.ErrorCode);
        Assert.Contains("13 or over", error.ErrorMessage);
    }

    [Fact]
    public void Dates_in_the_future_are_refused()
    {
        var result = _validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!", new DateOnly(2027, 1, 1)));

        var error = Assert.Single(result.Errors);
        Assert.Contains("check your date of birth", error.ErrorMessage);
    }

    [Fact]
    public void The_command_never_prints_the_password_or_date_of_birth()
    {
        var text = new AddUserCommand("rider@example.com", "Passw0rd!", TestDates.Adult).ToString();

        Assert.DoesNotContain("Passw0rd", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1990", text, StringComparison.Ordinal);
    }
}
