namespace BJK.Modules.User.Infrastructure.Services;

public sealed class IdentityServerOptions
{
    public const string SectionName = "IdentityServer";

    /// <summary>Base URL of bjk-identity-server, e.g. https://bjk-identity-dev.azurewebsites.net/.</summary>
    public Uri? Authority { get; set; }
}

/// <summary>
/// Typed HttpClient for bjk-identity-server.
/// The endpoint path is provisional until the identity server's account API is built.
/// </summary>
internal sealed class IdentityServerClient(HttpClient http) : IIdentityService
{
    public async Task SendPasswordResetAsync(string identityId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            $"api/accounts/{Uri.EscapeDataString(identityId)}/password-reset",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
