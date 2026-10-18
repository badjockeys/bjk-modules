using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace BJK.Modules.Shared.PersonalData;

/// <summary>
/// One module's part of a user's data export ("download my data"). Each module that stores personal data
/// registers one; <c>GET /api/me/data-export</c> on each host gathers every registered section.
/// </summary>
public interface IPersonalDataExporter
{
    /// <summary>The section's name in the export, e.g. "profile" or "posts".</summary>
    string Section { get; }

    /// <summary>Everything this module holds about the user, as a JSON-serialisable object.</summary>
    Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// "Download my data". Returns everything this host's modules hold about the signed-in user as one JSON file.
/// The identity server and the core API each serve this endpoint for their own data; the apps download both.
/// </summary>
[ApiController]
[Route("api/me/data-export")]
[Authorize(Policy = ModuleAuthentication.SignedInUserPolicy)]
public sealed class PersonalDataController(
    IEnumerable<IPersonalDataExporter> exporters,
    ICurrentUser caller,
    TimeProvider clock,
    IHostEnvironment environment)
    : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var now = clock.GetUtcNow();

        var sections = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var exporter in exporters)
        {
            sections[exporter.Section] = await exporter.ExportAsync(userId, cancellationToken);
        }

        var export = new PersonalDataExport(now, userId, environment.ApplicationName, sections);
        var file = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
        var name = $"badjockeys-data-{environment.ApplicationName.ToLowerInvariant()}-{now:yyyy-MM-dd}.json";

        return File(file, "application/json", name);
    }
}

/// <summary>The export file.</summary>
/// <param name="ExportedAt">When the export was made.</param>
/// <param name="UserId">The user's id, the same in every part of Bad Jockeys.</param>
/// <param name="Source">Which part of Bad Jockeys produced this file.</param>
/// <param name="Sections">Each module's data, by section name.</param>
public sealed record PersonalDataExport(
    DateTimeOffset ExportedAt,
    Guid UserId,
    string Source,
    IReadOnlyDictionary<string, object?> Sections);
