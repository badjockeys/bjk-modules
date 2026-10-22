using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>
/// The segment progress store in memory, with the same rules as the database one (create once, complete once, claim
/// the assembly once), so the processor's repeat-delivery handling can be tested without SQL Server.
/// </summary>
internal sealed class InMemoryVideoJobStore : IVideoJobStore
{
    private readonly Dictionary<(Guid, int), VideoJob> _jobs = [];

    public int Claims { get; private set; }

    public VideoJob? Job(Guid mediaId, int revision = 0) => _jobs.GetValueOrDefault((mediaId, revision));

    public Task<VideoJob?> GetAsync(Guid mediaId, int revision, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.GetValueOrDefault((mediaId, revision)));

    public Task<bool> TryCreateAsync(VideoJob job, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryAdd((job.MediaId, job.Revision), job));

    public Task RecordAttemptAsync(Guid mediaId, int revision, int index, CancellationToken cancellationToken)
    {
        _jobs[(mediaId, revision)].Segments.Single(s => s.Index == index).RecordAttempt();
        return Task.CompletedTask;
    }

    public Task<bool> TryCompleteAsync(
        Guid mediaId,
        int revision,
        int index,
        VideoSegmentResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult(_jobs[(mediaId, revision)].Segments.Single(s => s.Index == index)
            .MarkDone(
                result.Frames,
                result.FacesBlurred,
                result.PlatesBlurred,
                result.ReviewReasons,
                result.Elapsed,
                now,
                result.SensitiveScore,
                result.NudityScore,
                result.PerceptualHashes));

    public Task<bool> AllDoneAsync(Guid mediaId, int revision, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryGetValue((mediaId, revision), out var job) && job.Segments.All(s => s.IsDone));

    public Task<bool> TryClaimAssemblyAsync(Guid mediaId, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = _jobs.TryGetValue((mediaId, revision), out var job) && job.TryClaimAssembly(now);
        Claims += claimed ? 1 : 0;
        return Task.FromResult(claimed);
    }
}
