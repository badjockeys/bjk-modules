using Amazon.S3;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Messaging;
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
    /// Registers the media processing worker: reads jobs from <c>Media:Queue:QueueName</c>, blurs faces and number
    /// plates (ONNX models from <c>Media:Redaction</c>), converts files with ffmpeg and publishes them, or holds them for
    /// review. For the worker host only (bjk-core's media worker), which needs ffmpeg installed. It
    /// doesn't register the Media API, the user lifecycle consumer or database migrations; the API host does those.
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
            .ValidateOnStart();
        services.AddSingleton<IMediaTranscoder, FfmpegTranscoder>();
        services.AddSingleton<IRedactionDetector, YuNetFaceDetector>();
        services.AddSingleton<IRedactionDetector, LpdYuNetPlateDetector>();
        services.AddSingleton<IMediaRedactor, MediaRedactor>();
        services.AddScoped<MediaProcessor>();
        services.AddHostedService<MediaProcessingWorker>();
        return services;
    }

    internal static void AddStorageAndData(IServiceCollection services, IConfiguration configuration, string connectionStringName)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<MediaDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MediaModule.MigrationsHistoryTable, MediaModule.Schema);
                sql.EnableRetryOnFailure();
            }));
        services.TryAddScoped<IMediaRepository, MediaRepository>();

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaQueueOptions>()
            .Bind(configuration.GetSection(MediaQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddBjkMessaging(configuration);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IAmazonS3>(sp =>
            S3MediaStorage.CreateClient(sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value));
        services.TryAddSingleton<IMediaStorage, S3MediaStorage>();
    }
}
