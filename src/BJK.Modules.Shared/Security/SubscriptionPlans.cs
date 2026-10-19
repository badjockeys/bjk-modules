using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Security;

/// <summary>
/// The caller's subscription plan. Logged-out callers have no plan (modules treat them separately). Billing isn't
/// built yet: the identity server will issue the access-token claim <c>plan</c> (<c>free</c>, <c>premium</c> or
/// <c>premium_plus</c>) once it is; until then every signed-in user is <see cref="Free"/> unless listed in
/// <c>Plans:Overrides</c>.
/// </summary>
public enum SubscriptionPlan
{
    /// <summary>The default, and what a missing or unknown <c>plan</c> claim means.</summary>
    Free,

    /// <summary>£19.99 a year or £2.49 a month.</summary>
    Premium,

    /// <summary>£149 a year or £14.99 a month per user (professionals and organisations).</summary>
    PremiumPlus,
}

/// <summary>The <c>plan</c> claim's values.</summary>
public static class SubscriptionPlanClaims
{
    /// <summary>The access-token claim carrying the plan.</summary>
    public const string ClaimType = "plan";

    /// <summary>Reads a claim value (<c>free</c>, <c>premium</c>, <c>premium_plus</c>, any case). Anything else is Free.</summary>
    /// <param name="value">The claim value, or null.</param>
    public static SubscriptionPlan Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "premium" => SubscriptionPlan.Premium,
        "premium_plus" or "premiumplus" or "premium+" => SubscriptionPlan.PremiumPlus,
        _ => SubscriptionPlan.Free,
    };

    /// <summary>True if the value is one of the known plan names.</summary>
    /// <param name="value">A plan name.</param>
    public static bool IsKnown(string? value) =>
        value?.Trim().ToLowerInvariant() is "free" or "premium" or "premium_plus" or "premiumplus" or "premium+";

    /// <summary>The claim value for a plan.</summary>
    /// <param name="plan">The plan.</param>
    public static string ToClaimValue(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Premium => "premium",
        SubscriptionPlan.PremiumPlus => "premium_plus",
        _ => "free",
    };
}

/// <summary>
/// Plan settings. Section <c>Plans</c>. <see cref="Overrides"/> gives chosen accounts a plan whatever their token
/// says, for testing Premium features before billing exists (e.g. <c>Plans:Overrides:&lt;user id&gt; = premium</c>).
/// Remove the overrides once the identity server issues the <c>plan</c> claim.
/// </summary>
public sealed class PlansOptions
{
    public const string SectionName = "Plans";

    /// <summary>User id (GUID) to plan name (<c>free</c>, <c>premium</c>, <c>premium_plus</c>).</summary>
    public Dictionary<string, string> Overrides { get; set; } = [];
}

internal sealed class PlansOptionsValidator : IValidateOptions<PlansOptions>
{
    public ValidateOptionsResult Validate(string? name, PlansOptions options)
    {
        var bad = options.Overrides
            .Where(o => !Guid.TryParse(o.Key, out _) || !SubscriptionPlanClaims.IsKnown(o.Value))
            .Select(o => o.Key)
            .ToList();
        return bad.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Plans:Overrides needs user ids (GUIDs) mapped to free, premium or premium_plus. Check: {string.Join(", ", bad)}.");
    }
}

/// <summary>Works out the caller's plan: an override from <c>Plans:Overrides</c>, else the token's <c>plan</c> claim.</summary>
public interface IPlanResolver
{
    /// <summary>The signed-in caller's plan, or null for a logged-out caller (or a machine token).</summary>
    SubscriptionPlan? CurrentPlan();
}

internal sealed class HttpContextPlanResolver(IHttpContextAccessor accessor, IOptions<PlansOptions> options) : IPlanResolver
{
    public SubscriptionPlan? CurrentPlan()
    {
        var user = accessor.HttpContext?.User;
        if (user?.GetIdentityId() is not { } id)
        {
            return null;
        }

        if (Guid.TryParse(id, out var userId))
        {
            foreach (var (key, plan) in options.Value.Overrides)
            {
                if (Guid.TryParse(key, out var overridden) && overridden == userId)
                {
                    return SubscriptionPlanClaims.Parse(plan);
                }
            }
        }

        return SubscriptionPlanClaims.Parse(user.FindFirst(SubscriptionPlanClaims.ClaimType)?.Value);
    }
}

internal static class PlansServiceCollectionExtensions
{
    public static IServiceCollection AddPlans(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlansOptions>()
            .Bind(configuration.GetSection(PlansOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PlansOptions>, PlansOptionsValidator>());
        services.TryAddScoped<IPlanResolver, HttpContextPlanResolver>();
        return services;
    }
}
