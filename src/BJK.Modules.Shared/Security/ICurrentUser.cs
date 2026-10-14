using Microsoft.AspNetCore.Http;

namespace BJK.Modules.Shared.Security;

/// <summary>The caller of the current request, as seen by use case handlers.</summary>
public interface ICurrentUser
{
    /// <summary>Identity server account id ("sub"), or null for anonymous calls and client-only tokens.</summary>
    string? IdentityId { get; }

    bool HasScope(string scope);
}

internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? IdentityId => accessor.HttpContext?.User.GetIdentityId();

    public bool HasScope(string scope) => accessor.HttpContext?.User.HasScope(scope) ?? false;
}
