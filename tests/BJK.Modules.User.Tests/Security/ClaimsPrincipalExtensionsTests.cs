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
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "sub-1"), new Claim("scope", "bjk.users")]));

        Assert.Null(principal.GetIdentityId());
        Assert.False(principal.HasScope("bjk.users"));
    }

    [Fact]
    public void GetIdentityId_reads_sub()
    {
        Assert.Equal("sub-1", Authenticated(new Claim("sub", "sub-1")).GetIdentityId());
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));
}
