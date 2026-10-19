namespace BJK.Modules.Shared.Exceptions;

/// <summary>The caller has reached a limit, such as the new-account posting limit. Returned as 429.</summary>
public sealed class TooManyRequestsException(string message, string code) : Exception(message)
{
    /// <summary>A stable code for the apps, e.g. <c>new_account_limit</c>.</summary>
    public string Code { get; } = code;
}
