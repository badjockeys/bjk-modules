namespace BJK.Modules.Shared.Exceptions;

/// <summary>Thrown by a handler when the requested resource doesn't exist. Returned to callers as 404.</summary>
public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;

    public object Key { get; } = key;
}
