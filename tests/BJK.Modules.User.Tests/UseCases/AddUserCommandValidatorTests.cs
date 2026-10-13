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
            new AddUserCommand("sub-1", "rider@example.com", "Road Hog"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Duplicate_email_fails()
    {
        _users.EmailExistsAsync("rider@example.com", Arg.Any<CancellationToken>()).Returns(true);
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand("sub-1", "rider@example.com", "Road Hog"),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddUserCommand.Email));
    }

    [Fact]
    public async Task Existing_identity_fails()
    {
        _users.IdentityExistsAsync("sub-1", Arg.Any<CancellationToken>()).Returns(true);
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand("sub-1", "rider@example.com", "Road Hog"),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddUserCommand.IdentityId));
    }

    [Theory]
    [InlineData("", "rider@example.com", "Road Hog", nameof(AddUserCommand.IdentityId))]
    [InlineData("sub-1", "not-an-email", "Road Hog", nameof(AddUserCommand.Email))]
    [InlineData("sub-1", "rider@example.com", "", nameof(AddUserCommand.DisplayName))]
    public async Task Invalid_fields_fail(string identityId, string email, string displayName, string expectedProperty)
    {
        var validator = new AddUserCommandValidator(_users);

        var result = await validator.ValidateAsync(
            new AddUserCommand(identityId, email, displayName),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, e => e.PropertyName == expectedProperty);
    }
}
