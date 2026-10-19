namespace BJK.Modules.Shared.Exceptions;

/// <summary>Something the request depends on (such as the CAPTCHA service) is unavailable. Returned as 503.</summary>
public sealed class ServiceUnavailableException(string message) : Exception(message);
