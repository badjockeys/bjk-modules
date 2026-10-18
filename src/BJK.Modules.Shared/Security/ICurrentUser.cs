using Microsoft.AspNetCore.Http;

namespace BJK.Modules.Shared.Security;

/// <summary>The caller of the current request, as seen by use case handlers.</summary>
public interface ICurrentUser
{
    /// <summary>Identity server account id ("sub"), or null for anonymous calls and client-only tokens.</summary>
    string? IdentityId { get; }

    bool HasScope(string scope);

    bool HasRole(string role);

    /// <summary>True if the user signed in with a second factor.</summary>
    bool UsedMfa { get; }

    /// <summary>True if the user has confirmed their email address.</summary>
    bool EmailVerified { get; }
}

internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? IdentityId => accessor.HttpContext?.User.GetIdentityId();

    public bool HasScope(string scope) => accessor.HttpContext?.User.HasScope(scope) ?? false;

    public bool HasRole(string role) => accessor.HttpContext?.User.HasRole(role) ?? false;

    public bool UsedMfa => accessor.HttpContext?.User.UsedMfa() ?? false;

    public bool EmailVerified => accessor.HttpContext?.User.IsEmailVerified() ?? false;
}
