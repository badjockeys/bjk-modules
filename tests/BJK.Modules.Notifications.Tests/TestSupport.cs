namespace BJK.Modules.Notifications.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid UserId = Guid.Parse("7c1c2d3e-0000-4000-8000-000000000001");

    public static readonly Guid SamId = Guid.Parse("7c1c2d3e-0000-4000-8000-000000000002");

    public static readonly Guid PostId = Guid.Parse("7c1c2d3e-0000-4000-8000-0000000000f1");
}
