namespace BJK.Modules.Shared.Exceptions;

/// <summary>Thrown when the caller is authenticated but not allowed to act on the resource. Returned as 403.</summary>
public sealed class ForbiddenException(string message) : Exception(message);
