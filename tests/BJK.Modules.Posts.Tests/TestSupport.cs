using BJK.Modules.Posts.Domain;

namespace BJK.Modules.Posts.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid UserId = Guid.Parse("6b1c2d3e-0000-4000-8000-000000000001");

    public static readonly Guid OtherUserId = Guid.Parse("6b1c2d3e-0000-4000-8000-000000000002");

    public static PostDetails Details(string title = "Overtook on a blind bend") => new(
        title,
        "Came round the bend on the wrong side of the road.",
        IncidentCategory.Undertaking,
        Now.AddDays(-1),
        53.5933m,
        -2.2966m,
        "A56, Bury");

    public static Post NewPost(Guid authorId, int mediaCount = 1) =>
        Post.Create(authorId, Details(), Enumerable.Range(0, mediaCount).Select(_ => Guid.NewGuid()).ToList(), Now);
}
