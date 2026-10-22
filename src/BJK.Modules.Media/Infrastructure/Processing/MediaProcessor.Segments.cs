using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>Videos processed in segments: planning, one segment, and joining them.</summary>
internal sealed partial class MediaProcessor
{
    /// <summary>
    /// Records what the probe found, plans the segments and queues a job for each one not yet done. Safe to repeat:
    /// a second delivery finds the plan already saved and only re-queues unfinished segments.
    /// </summary>
    private async Task<ProcessingOutcome> SplitAsync(
        MediaItem item,
        MediaProcessingJob job,
        VideoSegmentPlan plan,
        ProbeResult probe,
        string sha256,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        item.RecordProbe(probe.DurationSeconds, probe.Width, probe.Height, sha256, now);
        try
        {
            if (!await videoJobs.TryCreateAsync(VideoJob.Create(item.Id, item.Revision, plan, now), cancellationToken))
            {
                await media.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Media {MediaId} changed before it could be split; skipping.", item.Id);
            return ProcessingOutcome.Completed;
        }

        var videoJob = await videoJobs.GetAsync(item.Id, item.Revision, cancellationToken)
            ?? throw new InvalidOperationException($"The segment plan for media {item.Id} wasn't saved.");
        var waiting = videoJob.Segments.Where(s => !s.IsDone).OrderBy(s => s.Index).ToList();
        foreach (var segment in waiting)
        {
            await queue.EnqueueAsync(job.ForSegment(item.Revision, segment.Index), cancellationToken);
        }

        if (waiting.Count == 0)
        {
            // Every segment was finished by earlier deliveries; make sure they get joined.
            await queue.EnqueueAsync(job.ForAssembly(item.Revision), cancellationToken);
        }

        logger.LogInformation(
            "Media {MediaId} ({Duration}s) split into {Count} segments of {Frames} frames; {Waiting} queued.",
            item.Id,
            probe.DurationSeconds,
            videoJob.SegmentCount,
            videoJob.FramesPerSegment,
            waiting.Count);
        return ProcessingOutcome.Completed;
    }

    /// <summary>
    /// Blurs and encodes one segment, stores it privately and marks it done. A segment already done (a repeat delivery)
    /// isn't processed again. The worker that finishes the last segment queues the assemble job.
    /// </summary>
    private async Task<ProcessingOutcome> ProcessSegmentAsync(
        MediaProcessingJob job,
        bool isLastAttempt,
        bool isRedelivery,
        CancellationToken cancellationToken)
    {
        var index = job.Segment ?? throw new InvalidOperationException("A segment job needs a segment number.");
        var item = await media.GetByIdAsync(job.MediaId, cancellationToken);
        if (item is null || item.Status != MediaStatus.Processing || item.Revision != job.Revision)
        {
            logger.LogInformation(
                "Skipping segment {Segment} of media {MediaId}: it is {Status} (revision {Revision}).",
                index,
                job.MediaId,
                item?.Status.ToString() ?? "gone",
                item?.Revision);
            return ProcessingOutcome.Completed;
        }

        var videoJob = await videoJobs.GetAsync(item.Id, job.Revision, cancellationToken);
        var segment = videoJob?.Segments.FirstOrDefault(s => s.Index == index);
        if (videoJob is null || segment is null)
        {
            logger.LogWarning("Skipping segment {Segment} of media {MediaId}: it isn't in the plan.", index, item.Id);
            return ProcessingOutcome.Completed;
        }

        if (segment.IsDone)
        {
            // Already done by an earlier delivery, which may have stopped before queueing the assemble job.
            logger.LogInformation("Segment {Segment} of media {MediaId} is already done.", index, item.Id);
            await QueueAssemblyIfReadyAsync(job, force: true, cancellationToken);
            return ProcessingOutcome.Completed;
        }

        await videoJobs.RecordAttemptAsync(item.Id, job.Revision, index, cancellationToken);
        VideoJob current = videoJob;
        var plan = current.Plan;
        var outcome = await InFolderAsync(
            item,
            $"-r{job.Revision}-s{index}",
            isLastAttempt,
            async folder =>
            {
                var original = Path.Combine(folder, SourceFileName(item));
                await DownloadOriginalAsync(item, original, cancellationToken);

                var output = Path.Combine(folder, $"{index:D3}.mp4");
                var result = await redactor.RedactVideoSegmentAsync(original, plan, index, item.ManualRedactions, output, cancellationToken);
                if (result.Frames > 0)
                {
                    await storage.UploadPrivateAsync(MediaKeys.Segment(item.UserId, item.Id, job.Revision, index), output, "video/mp4", cancellationToken);
                }

                var completed = await videoJobs.TryCompleteAsync(
                    item.Id,
                    job.Revision,
                    index,
                    new VideoSegmentResult(
                        result.Frames,
                        result.FacesBlurred,
                        result.PlatesBlurred,
                        result.ReviewReasons,
                        result.Elapsed,
                        result.SensitiveScore,
                        result.NudityScore,
                        result.PerceptualHashes),
                    clock.GetUtcNow(),
                    cancellationToken);
                logger.LogInformation(
                    "Segment {Segment}/{Count} of media {MediaId}: {Frames} frames, {Faces} faces, {Plates} plates in {Elapsed}{Repeat}.",
                    index + 1,
                    plan.SegmentCount,
                    item.Id,
                    result.Frames,
                    result.FacesBlurred,
                    result.PlatesBlurred,
                    result.Elapsed,
                    completed ? string.Empty : " (already done by another delivery)");

                await QueueAssemblyIfReadyAsync(job, force: isRedelivery || !completed, cancellationToken);
                return ProcessingOutcome.Completed;
            },
            cancellationToken,
            onFailed: () => DeleteSegmentFilesAsync(item, current));
        return outcome;
    }

    /// <summary>
    /// Queues the assemble job if every segment is done. Normally exactly one worker wins the claim and queues it.
    /// With <paramref name="force"/> (a repeat delivery, which may follow a worker that claimed it but stopped before
    /// queueing), it's queued whenever all segments are done; assembling twice is harmless.
    /// </summary>
    private async Task QueueAssemblyIfReadyAsync(MediaProcessingJob job, bool force, CancellationToken cancellationToken)
    {
        var claimed = await videoJobs.TryClaimAssemblyAsync(job.MediaId, job.Revision, clock.GetUtcNow(), cancellationToken);
        if (claimed || (force && await videoJobs.AllDoneAsync(job.MediaId, job.Revision, cancellationToken)))
        {
            await queue.EnqueueAsync(job.ForAssembly(job.Revision), cancellationToken);
            logger.LogInformation("All segments of media {MediaId} are done; queued the assemble job.", job.MediaId);
        }
    }

    /// <summary>
    /// Joins the finished segments into the published video, makes the thumbnail from it, and sets Ready, or
    /// NeedsReview if any segment's detection was uncertain or failed (or a moderator added regions). Runs once: a
    /// repeat finds the media already finished and stops.
    /// </summary>
    private async Task<ProcessingOutcome> AssembleAsync(MediaProcessingJob job, bool isLastAttempt, CancellationToken cancellationToken)
    {
        var item = await media.GetByIdAsync(job.MediaId, cancellationToken);
        if (item is null || item.Status != MediaStatus.Processing || item.Revision != job.Revision)
        {
            logger.LogInformation("Skipping assembly of media {MediaId}: it is {Status}.", job.MediaId, item?.Status.ToString() ?? "gone");
            return ProcessingOutcome.Completed;
        }

        var videoJob = await videoJobs.GetAsync(item.Id, job.Revision, cancellationToken);
        if (videoJob is null || videoJob.AssembledAt is not null)
        {
            return ProcessingOutcome.Completed;
        }

        VideoJob current = videoJob;
        var segments = current.Segments.OrderBy(s => s.Index).ToList();
        if (segments.Any(s => !s.IsDone))
        {
            // Queued too early (shouldn't happen); the last segment to finish queues it again.
            logger.LogWarning("Media {MediaId} isn't ready to assemble: {Waiting} segment(s) still waiting.", item.Id, segments.Count(s => !s.IsDone));
            return ProcessingOutcome.Completed;
        }

        return await InFolderAsync(
            item,
            $"-r{job.Revision}-assemble",
            isLastAttempt,
            async folder =>
            {
                var parts = new List<string>();
                foreach (var segment in segments.Where(s => s.Frames is > 0))
                {
                    var path = Path.Combine(folder, $"{segment.Index:D3}.mp4");
                    await using (var file = File.Create(path))
                    {
                        await storage.DownloadOriginalAsync(MediaKeys.Segment(item.UserId, item.Id, job.Revision, segment.Index), file, cancellationToken);
                    }

                    parts.Add(path);
                }

                if (parts.Count == 0)
                {
                    throw new UnreadableMediaException("No segment of the video had any frames.");
                }

                string? audio = null;
                if (!_redaction.RemoveAudio)
                {
                    audio = Path.Combine(folder, SourceFileName(item));
                    await DownloadOriginalAsync(item, audio, cancellationToken);
                }

                var playback = Path.Combine(folder, MediaKeys.VideoPlaybackFile);
                var thumbnail = Path.Combine(folder, MediaKeys.VideoThumbnailFile);
                await transcoder.ConcatenateVideoAsync(parts, audio, playback, cancellationToken);
                await transcoder.CreateVideoThumbnailAsync(playback, thumbnail, cancellationToken);

                // A frame in any segment matched a known image: block it (deleting the segment files too).
                if (await MatchFromReasonsAsync(segments.SelectMany(s => s.ReviewReasonList), cancellationToken) is { } frameMatch)
                {
                    await TryAsync(() => DeleteSegmentFilesAsync(item, current));
                    return await BlockAsync(item, frameMatch, item.Sha256, cancellationToken);
                }

                // Counts are per segment, so a face crossing a join is counted once in each.
                item.RecordRedaction(segments.Sum(s => s.FacesBlurred ?? 0), segments.Sum(s => s.PlatesBlurred ?? 0));
                await ApplySensitiveScoreAsync(item, segments.Max(s => s.SensitiveScore), cancellationToken);
                // The nudity score and the frames' PDQ hashes (for "block copies") come from the segments too.
                item.RecordContentChecks(segments.Max(s => s.NudityScore), SpreadHashes(segments.Select(s => s.PerceptualHashList)));
                var reasons = ReviewReasons(segments.SelectMany(s => s.ReviewReasonList), item.ManualRedactions.Count);
                var outcome = await PublishAsync(
                    item,
                    playback,
                    thumbnail,
                    reasons,
                    new ProcessedDetails(item.DurationSeconds, item.Width, item.Height, item.Sha256),
                    beforeSave: () => current.MarkAssembled(clock.GetUtcNow()),
                    cancellationToken);

                await TryAsync(() => DeleteSegmentFilesAsync(item, current));
                logger.LogInformation(
                    "Media {MediaId} assembled from {Count} segments ({Frames} frames) and is {Status}.",
                    item.Id,
                    segments.Count,
                    segments.Sum(s => s.Frames ?? 0),
                    item.Status);
                return outcome;
            },
            cancellationToken,
            onFailed: () => DeleteSegmentFilesAsync(item, current));
    }

    private async Task DeleteSegmentFilesAsync(MediaItem item, VideoJob videoJob)
    {
        foreach (var segment in videoJob.Segments)
        {
            await storage.DeleteOriginalAsync(MediaKeys.Segment(item.UserId, item.Id, videoJob.Revision, segment.Index), CancellationToken.None);
        }
    }

    /// <summary>Up to <see cref="MediaItem.MaxPerceptualHashes"/> hashes spread across the segments, in order.</summary>
    internal static List<string> SpreadHashes(IEnumerable<IReadOnlyList<string>> perSegment)
    {
        var all = perSegment.SelectMany(h => h).ToList();
        var count = MediaItem.MaxPerceptualHashes;
        if (all.Count <= count)
        {
            return all;
        }

        return Enumerable.Range(0, count).Select(i => all[(int)((long)i * (all.Count - 1) / (count - 1))]).ToList();
    }
}
