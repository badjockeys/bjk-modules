using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;

namespace BJK.Modules.User.API.Controllers;

/// <summary>
/// Reads an age check upload into memory. The upload limits keep the whole multipart body in memory too, so the
/// selfie or ID photo is never buffered to a temp file on disk.
/// </summary>
public static class AgeCheckUploads
{
    /// <summary>Upper bound for an age check request; the configured per-file limits are checked by the commands.</summary>
    public const int MaxRequestBytes = 25 * 1024 * 1024;

    /// <summary>The file's bytes and content type, or a 400 if no file was sent.</summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<(byte[] Bytes, string ContentType)> ReadAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new ValidationException([new ValidationFailure("AgeCheck", "Please attach the video or photo.")]);
        }

        if (file.Length > MaxRequestBytes)
        {
            throw new ValidationException([new ValidationFailure("AgeCheck", "That file is too large.")]);
        }

        var bytes = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return (bytes, file.ContentType ?? string.Empty);
    }
}
