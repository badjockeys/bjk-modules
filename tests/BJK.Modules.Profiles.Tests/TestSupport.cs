namespace BJK.Modules.Profiles.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid UserId = Guid.Parse("5a1c2d3e-0000-4000-8000-000000000001");

    public static readonly Guid OtherUserId = Guid.Parse("5a1c2d3e-0000-4000-8000-000000000002");
}
