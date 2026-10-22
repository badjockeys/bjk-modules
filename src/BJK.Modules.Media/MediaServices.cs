using Amazon.S3;
using BJK.Modules.Media.Infrastructure.Describe;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Safety;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Queues;
using BJK.Modules.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media;

/// <summary>Registrations shared by the API (<see cref="MediaModule"/>) and the processing worker.</summary>
public static class MediaServices
{
    /// <summary>
    /// Registers the media processing worker: reads jobs from the <c>Queues:MediaProcessing</c> queue (a SQL Server
    /// table, read with row locks, so replicas share the work and retries survive crashes), blurs faces and number
    /// plates (ONNX models from <c>Media:Redaction</c>), converts files with ffmpeg and publishes them, or holds them for
    /// review. Longer videos are split into segment jobs on the same queue, so run several replicas. For the worker
    /// host only (bjk-core's media worker), which needs ffmpeg installed. It doesn't register the Media API, the user
    /// lifecycle consumer, the media events publisher (the outbox it writes to is published by the API host) or
    /// database migrations; the API host does those.
    /// </summary>
    /// <param name="services">The worker host's services.</param>
    /// <param name="configuration">Its configuration: the same <c>Media</c> settings as the API, plus <c>Media:Processing</c>.</param>
    /// <param name="connectionStringName">Connection string for the app database, as passed to <see cref="MediaModule"/>.</param>
    public static IServiceCollection AddMediaProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName)
    {
        AddStorageAndData(services, configuration, connectionStringName);
        services.AddOptions<MediaProcessingOptions>()
            .Bind(configuration.GetSection(MediaProcessingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaRedactionOptions>()
            .Bind(configuration.GetSection(MediaRedactionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.FaceConfidentAt >= o.FaceMinConfidence, "Media:Redaction:FaceConfidentAt must be at least FaceMinConfidence.")
            .Validate(o => o.PlateConfidentAt >= o.PlateMinConfidence, "Media:Redaction:PlateConfidentAt must be at least PlateMinConfidence.")
            .Validate(o => o.FaceTrackConfidence <= o.FaceMinConfidence, "Media:Redaction:FaceTrackConfidence must be at most FaceMinConfidence.")
            .Validate(o => o.PlateTrackConfidence <= o.PlateMinConfidence, "Media:Redaction:PlateTrackConfidence must be at most PlateMinConfidence.")
            .Validate(o => o.HeadTrackConfidence <= o.HeadMinConfidence, "Media:Redaction:HeadTrackConfidence must be at most HeadMinConfidence.")
            .ValidateOnStart();
        services.RequireWhenDeployed("Media:Redaction:FaceModelPath", "the face detection model's path in the worker image");
        services.RequireWhenDeployed("Media:Redaction:PlateModelPath", "the number plate detection model's path in the worker image");
        services.RequireWhenDeployed("Media:Redaction:PersonModelPath", "the person detection model's path in the worker image");
        services.TryAddSingleton<IMediaJobQueue, MediaJobQueue>();
        services.TryAddScoped<IVideoJobStore, VideoJobStore>();
        services.AddSingleton<IMediaTranscoder, FfmpegTranscoder>();
        services.AddSingleton<IRedactionDetector, YuNetFaceDetector>();
        services.AddSingleton<IRedactionDetector, LpdYuNetPlateDetector>();
        services.AddSingleton<IRedactionDetector, YoloxPersonDetector>();
        services.AddOptions<SensitiveDetectionOptions>()
            .Bind(configuration.GetSection(SensitiveDetectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.RequireWhenDeployed(
            "Media:SensitiveDetection:ModelPath",
            "the CLIP model's path in the worker image (the nudity and graphic content checks are required: Online Safety Act)");
        // CLIP when its model is set; otherwise the Development stand-in, which only runs in Development (elsewhere
        // the setting above stops the app starting, and the stand-in reports itself unavailable, so media go to review).
        services.TryAddSingleton<ISensitiveContentClassifier>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<SensitiveDetectionOptions>>().Value.ModelPath)
                ? ActivatorUtilities.CreateInstance<DevelopmentSensitiveContentClassifier>(sp)
                : ActivatorUtilities.CreateInstance<ClipContentClassifier>(sp));
        services.AddOptions<KnownImageOptions>()
            .Bind(configuration.GetSection(KnownImageOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.RefreshInterval >= TimeSpan.FromSeconds(10), "Media:KnownImages:RefreshInterval must be at least 10 seconds.")
            .Validate(o => o.SyncInterval >= TimeSpan.FromMinutes(5), "Media:KnownImages:SyncInterval must be at least 5 minutes.")
            .ValidateOnStart();
        services.RequireWhenDeployed(
            "Media:KnownImages:Enabled",
            "true (or unset): checking uploads against known illegal images is required (Online Safety Act)",
            v => bool.TryParse(v, out var on) && on,
            allowMissing: true);
        services.TryAddSingleton<IKnownImageList, KnownImageList>();
        services.AddHostedService<KnownImageListRefresher>();
        services.TryAddSingleton<KnownImageSync>();
        services.AddHostedService(sp => sp.GetRequiredService<KnownImageSync>());
        services.AddSingleton<IMediaRedactor, MediaRedactor>();
        services.AddScoped<MediaProcessor>();

        // One job per replica by default (Media:Processing:MaxConcurrentJobs): each job already uses every core.
        var queue = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()?.MediaProcessing ?? new MessagingOptions().MediaProcessing;
        var concurrency = configuration.GetSection(MediaProcessingOptions.SectionName).Get<MediaProcessingOptions>()?.MaxConcurrentJobs ?? 1;
        // "Suggest a description": the model when Media:Describe:Endpoint is set, the stand-in locally otherwise.
        services.RequireWhenDeployed(
            "Media:Describe:Endpoint",
            "the description model's OpenAI-compatible address (the Qwen3-VL GPU container), ending /v1");
        var describe = configuration.GetSection(DescribeOptions.SectionName).Get<DescribeOptions>() ?? new DescribeOptions();
        if (describe.IsConfigured)
        {
            services.AddHttpClient<IDescriptionModel, OpenAiCompatibleDescriptionModel>(http => http.Timeout = Timeout.InfiniteTimeSpan);
        }
        else
        {
            services.TryAddSingleton<IDescriptionModel, DevelopmentDescriptionModel>();
        }

        services.AddContentScreening(configuration);
        services.TryAddScoped<DescriptionFrames>();
        var descriptionQueue = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()?.MediaDescriptions
            ?? new MessagingOptions().MediaDescriptions;
        services.AddMessageConsumer<DescriptionConsumer>(
            configuration,
            connectionStringName,
            new MessageConsumerDefinition(descriptionQueue) { MaxAttempts = DescriptionConsumer.MaxAttempts });

        services.AddMessageConsumer<MediaJobConsumer>(
            configuration,
            connectionStringName,
            new MessageConsumerDefinition(queue) { MaxAttempts = MediaQueueTopology.MaxDeliveries, Concurrency = Math.Clamp(concurrency, 1, 16) });
        return services;
    }

    internal static void AddStorageAndData(IServiceCollection services, IConfiguration configuration, string connectionStringName)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");
        services.RequireWhenDeployed(
            "Media:Storage:SensitiveBucket",
            "the private bucket for sensitive media (without it they'd go in the originals bucket and its 90-day deletion rule)");

        services.AddDbContext<MediaDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MediaModule.MigrationsHistoryTable, MediaModule.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.TryAddScoped<IMediaRepository, MediaRepository>();
        services.TryAddScoped<IKnownImageRepository, KnownImageRepository>();

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.SensitiveLinkLifetime >= TimeSpan.FromMinutes(1) && o.SensitiveLinkLifetime <= TimeSpan.FromHours(1),
                "Media:SensitiveLinkLifetime must be between 1 minute and 1 hour.")
            .Validate(o => o.UnusedOriginalRetention > TimeSpan.Zero, "Media:UnusedOriginalRetention must be positive.")
            .Validate(o => o.OriginalSweepInterval >= TimeSpan.FromMinutes(1), "Media:OriginalSweepInterval must be at least a minute.")
            .ValidateOnStart();
        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DescribeOptions>()
            .Bind(configuration.GetSection(DescribeOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Endpoint is null || o.Endpoint.IsAbsoluteUri, "Media:Describe:Endpoint must be an absolute address.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "Media:Describe:Timeout must be positive.")
            .ValidateOnStart();
        services.TryAddScoped<IDescriptionStore, DescriptionStore>();
        services.AddBjkMessaging(configuration, connectionStringName);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IAmazonS3>(sp =>
            S3MediaStorage.CreateClient(sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value));
        services.TryAddSingleton<IMediaStorage, S3MediaStorage>();

        // Logs which public files need purging from Cloudflare's cache; a host can register a real purger first.
        services.TryAddSingleton<IMediaCachePurger, LoggingMediaCachePurger>();
    }
}
