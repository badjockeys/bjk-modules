using BJK.Modules.User.UseCases.Add;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class AddUserCommandValidatorTests
{
    private readonly AddUserCommandValidator _validator = new();

    [Fact]
    public void Valid_command_passes()
    {
        Assert.True(_validator.Validate(new AddUserCommand("rider@example.com", "Passw0rd!")).IsValid);
    }

    [Theory]
    [InlineData("not-an-email", "Passw0rd!", nameof(AddUserCommand.Email))]
    [InlineData("", "Passw0rd!", nameof(AddUserCommand.Email))]
    [InlineData("rider@example.com", "", nameof(AddUserCommand.Password))]
    public void Invalid_fields_fail(string email, string password, string expectedProperty)
    {
        var result = _validator.Validate(new AddUserCommand(email, password));

        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }
}
