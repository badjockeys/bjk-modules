using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.Add;
using NSubstitute;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class AddUserCommandValidatorTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    [Fact]
    public async Task Valid_command_passes()
    {
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand("rider@example.com", "Passw0rd!", "Road Hog"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Duplicate_email_fails()
    {
        _users.EmailExistsAsync("rider@example.com", Arg.Any<CancellationToken>()).Returns(true);
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand(" rider@example.com ", "Passw0rd!", "Road Hog"),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddUserCommand.Email));
    }

    [Theory]
    [InlineData("not-an-email", "Passw0rd!", "Road Hog", nameof(AddUserCommand.Email))]
    [InlineData("rider@example.com", "", "Road Hog", nameof(AddUserCommand.Password))]
    [InlineData("rider@example.com", "Passw0rd!", "", nameof(AddUserCommand.DisplayName))]
    public async Task Invalid_fields_fail(string email, string password, string displayName, string expectedProperty)
    {
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand(email, password, displayName),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }
}
