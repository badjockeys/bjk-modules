using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Queues;
using BJK.Modules.Safety;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Describe;

/// <summary>
/// Handles "Suggest a description" requests on the media worker: picks blurred pictures of the media, asks the model
/// for a draft, checks the draft like typed text, and saves it. Nothing about the pictures or the draft is logged.
/// Two attempts; after that the request is marked failed so the app stops waiting.
/// </summary>
internal sealed class DescriptionConsumer(
    IDescriptionStore descriptions,
    IMediaRepository media,
    DescriptionFrames frames,
    IDescriptionModel model,
    IContentScreener screener,
    TimeProvider clock,
    ILogger<DescriptionConsumer> logger)
    : IMessageConsumer
{
    public const int MaxAttempts = 2;

    internal const string CouldNotDescribe = "We couldn't suggest a description this time. You can try again, or write your own.";

    public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        var job = message.TryDeserialize<DescriptionJob>();
        if (job is null)
        {
            return MessageOutcome.DeadLetter;
        }

        var description = await descriptions.GetAsync(job.DescriptionId, cancellationToken);
        if (description is null || description.Status != DescriptionStatus.Queued)
        {
            return MessageOutcome.Completed;
        }

        var items = await media.GetUntrackedAsync(description.MediaIdList, cancellationToken);
        var ready = description.MediaIdList
            .Select(id => items.FirstOrDefault(m => m.Id == id))
            .Where(m => m is { Status: MediaStatus.Ready, PlaybackKey: not null } && m.UserId == description.UserId)
            .Select(m => m!)
            .ToList();
        if (ready.Count == 0)
        {
            description.MarkFailed("The photos or video aren't ready any more.", clock.GetUtcNow());
            await descriptions.SaveChangesAsync(cancellationToken);
            return MessageOutcome.Completed;
        }

        var started = clock.GetTimestamp();
        var pictures = await frames.PickAsync(ready, cancellationToken);
        var draft = await model.DescribeAsync(pictures, DescriptionContext.FromJson(description.Context), cancellationToken);

        // The draft goes through the same checks as text people type; anything that would be held isn't offered.
        var screened = await screener.ScreenAsync($"{draft.Title}\n{draft.Description}", cancellationToken);
        if (screened.Verdict == ScreeningVerdict.Hold)
        {
            logger.LogWarning("Description {Id}: the draft didn't pass the text checks, so it isn't offered.", description.Id);
            description.MarkFailed(CouldNotDescribe, clock.GetUtcNow());
        }
        else
        {
            description.MarkReady(draft.Title, draft.Description, draft.Model, clock.GetUtcNow());
        }

        await descriptions.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Description {Id}: {Status} from {Pictures} pictures by {Model} in {Elapsed}.",
            description.Id,
            description.Status,
            pictures.Count,
            draft.Model,
            clock.GetElapsedTime(started));
        return MessageOutcome.Completed;
    }

    public async Task OnGivenUpAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.TryDeserialize<DescriptionJob>() is { } job && await descriptions.GetAsync(job.DescriptionId, cancellationToken) is { } description)
        {
            description.MarkFailed(CouldNotDescribe, clock.GetUtcNow());
            await descriptions.SaveChangesAsync(cancellationToken);
        }
    }
}
