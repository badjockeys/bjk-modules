using System.Security.Claims;
using BJK.Modules.Shared.Security;
using Xunit;

namespace BJK.Modules.User.Tests.Security;

public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void HasScope_reads_one_claim_per_scope()
    {
        var principal = Authenticated(new Claim("scope", "bjk.users"), new Claim("scope", "bjk.users.manage"));

        Assert.True(principal.HasScope("bjk.users.manage"));
    }

    [Fact]
    public void HasScope_reads_space_delimited_scopes()
    {
        var principal = Authenticated(new Claim("scope", "openid bjk.users"));

        Assert.True(principal.HasScope("bjk.users"));
        Assert.False(principal.HasScope("bjk.users.manage"));
    }

    [Fact]
    public void Anonymous_principal_has_no_identity_or_scopes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "00000000-0000-4000-8000-000000000001"), new Claim("scope", "bjk.users")]));

        Assert.Null(principal.GetIdentityId());
        Assert.False(principal.HasScope("bjk.users"));
    }

    [Fact]
    public void GetIdentityId_reads_sub()
    {
        Assert.Equal("00000000-0000-4000-8000-000000000001", Authenticated(new Claim("sub", "00000000-0000-4000-8000-000000000001")).GetIdentityId());
    }

    [Fact]
    public void Moderator_rights_need_scope_role_and_two_factor()
    {
        var full = Authenticated(new Claim("sub", "u1"), new Claim("scope", "bjk.posts.manage"), new Claim("role", "Moderator"), new Claim("amr", "pwd"), new Claim("amr", "mfa"));
        var noMfa = Authenticated(new Claim("sub", "u1"), new Claim("scope", "bjk.posts.manage"), new Claim("role", "Moderator"), new Claim("amr", "pwd"));
        var noRole = Authenticated(new Claim("sub", "u1"), new Claim("scope", "bjk.posts.manage"), new Claim("amr", "mfa"));

        Assert.True(full.HasModeratorRights("bjk.posts.manage"));
        Assert.False(full.HasAdminRights("bjk.posts.manage"));
        Assert.False(noMfa.HasModeratorRights("bjk.posts.manage"));
        Assert.False(noRole.HasModeratorRights("bjk.posts.manage"));
    }

    [Fact]
    public void Mapped_claim_types_are_understood()
    {
        var principal = Authenticated(
            new Claim("sub", "u1"),
            new Claim("scope", "bjk.users.manage"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("http://schemas.microsoft.com/claims/authnmethodsreferences", "mfa"),
            new Claim("email_verified", "True"));

        Assert.True(principal.HasAdminRights("bjk.users.manage"));
        Assert.True(principal.IsEmailVerified());
    }

    [Fact]
    public void Machine_tokens_with_the_scope_have_staff_rights()
    {
        var machine = Authenticated(new Claim("client_id", "bjk-local-admin"), new Claim("scope", "bjk.users.manage"));

        Assert.True(machine.HasAdminRights("bjk.users.manage"));
        Assert.False(machine.HasAdminRights("bjk.posts.manage"));
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));
}
