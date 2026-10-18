using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid UserId = Guid.Parse("4f1c2d3e-0000-4000-8000-000000000001");

    public static readonly Guid OtherUserId = Guid.Parse("4f1c2d3e-0000-4000-8000-000000000002");

    public static IOptions<MediaOptions> Options() => Microsoft.Extensions.Options.Options.Create(new MediaOptions());

    public static SignedUpload Upload(string key) => new(
        new Uri("https://example.r2.cloudflarestorage.com/originals/" + key + "?X-Amz-Signature=abc"),
        "PUT",
        new Dictionary<string, string> { ["Content-Type"] = "video/quicktime" },
        Now.AddMinutes(15));
}
