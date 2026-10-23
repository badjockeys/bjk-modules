using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Heatmap;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Publishing;
using BJK.Modules.Posts.UseCases.Reels;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Queues;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Uploads;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts;

/// <summary>
/// Registers the Posts module: incident posts, reactions, comments, reports, appeals, content screening and the
/// moderation audit log (for every module in the host). Hosted by the bjk-core API, which must also register the
/// Media, Profiles and Email modules (posts use their media and authors' profiles; report emails go through Email)
/// and a JWT bearer handler under <see cref="ModuleAuthentication.Scheme"/>.
/// </summary>
/// <param name="connectionStringName">Connection string for the app database holding the <see cref="Schema"/> tables.</param>
public sealed class PostsModule(string connectionStringName) : IModule
{
    public const string Schema = "posts";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Scope the apps need to post, react, comment and report.</summary>
    public const string ApiScope = "bjk.posts";

    /// <summary>
    /// Moderation scope, for the back-office: review reports, hide and restore content, delete anyone's posts and
    /// comments. A user token also needs the Moderator or Admin role and two-factor sign-in.
    /// </summary>
    public const string ManageScope = "bjk.posts.manage";

    /// <summary>Policy for a signed-in user with <see cref="ApiScope"/>.</summary>
    public const string UsePolicy = "bjk.posts";

    /// <summary>Policy for public reads. Anyone may call; a token, if sent, identifies the caller.</summary>
    public const string ReadPolicy = "bjk.posts.read";

    /// <summary>Policy for the moderation endpoints under <c>/api/admin</c>.</summary>
    public const string ManagePolicy = "bjk.posts.manage";

    public string Name => "Posts";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<PostsDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddOptions<PostsOptions>()
            .Bind(configuration.GetSection(PostsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PostsOptions>, PostsOptionsValidator>();
        services.AddScoped<IPostRepository, PostRepository>();
        services.AddMediaUsage<PostsMediaUsage>();
        services.AddScoped<IReactionRepository, ReactionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<ICommentReactionRepository, CommentReactionRepository>();
        services.AddScoped<CommentThreads>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IAppealRepository, AppealRepository>();
        services.AddScoped<IModerationAuditRepository, ModerationAuditRepository>();

        // Replaces Shared's fallback: the audit log for Posts, Profiles and Media is stored here.
        services.AddScoped<IModerationAuditLog, PostsModerationAuditLog>();
        services.AddScoped<NewAccountLimits>();
        services.AddScoped<ModerationNotifications>();
        // Blocked web addresses (IWF URL List when connected, moderators' and test entries): kept in memory, and
        // text containing one is held for review by the content screener.
        services.AddOptions<BlockedUrlOptions>()
            .Bind(configuration.GetSection(BlockedUrlOptions.SectionName))
            .Validate(o => o.RefreshInterval >= TimeSpan.FromSeconds(10) && o.SyncInterval >= TimeSpan.FromMinutes(5), "BlockedUrls: RefreshInterval must be at least 10 seconds and SyncInterval at least 5 minutes.")
            .ValidateOnStart();
        services.AddSingleton<BlockedUrlList>();
        services.AddSingleton<IBlockedUrlList>(sp => sp.GetRequiredService<BlockedUrlList>());
        services.AddScoped<IBlockedUrlRepository, BlockedUrlRepository>();
        services.AddSingleton<BlockedUrlMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<BlockedUrlMaintenance>());
        services.AddContentScreening(configuration);
        services.AddCaptchaVerification(configuration);
        services.AddAgeVerification(configuration);
        services.AddScoped<PostViews>();
        services.AddScoped<UseCases.Passengers.PassengerTagging>();

        // Drafts: up to 5 each, deleted with their media 30 days after they were last saved.
        services.AddScoped<IDraftRepository, DraftRepository>();
        services.AddScoped<UseCases.Drafts.DraftViews>();
        services.AddScoped<UseCases.Drafts.DraftDeletion>();
        services.AddHostedService<DraftsCleanup>();

        // Votes, follows, bookmarks, views and shares (owner, 21 Oct 2026); closed votes are announced every 5 minutes.
        services.AddScoped<IEngagementRepository, EngagementRepository>();
        services.AddScoped<VoteResultAnnouncer>();
        services.AddHostedService<VoteResults>();
        services.AddScoped<LocalRegularFinder>();
        services.AddHostedService<LocalRegularBadges>();
        services.AddNotificationSenderFallback();
        services.AddAreaAlertsFallback();

        // Reels (daily plays by plan, ranking refreshed in the background) and the incident heat map.
        services.RequireWhenDeployed("Reels:ViewerKeySecret", "a long random secret for hashing logged-out viewers");
        services.RequireWhenDeployed("Posts:AppUrl", "the web app's https address, for links in report emails", DeployedSettingChecks.IsHttps);
        services.AddOptions<ReelsOptions>()
            .Bind(configuration.GetSection(ReelsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReelsOptions>, ReelsOptionsValidator>();
        services.AddOptions<HeatmapOptions>()
            .Bind(configuration.GetSection(HeatmapOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HeatmapOptions>, HeatmapOptionsValidator>();
        services.AddScoped<IReelRepository, ReelRepository>();
        services.AddScoped<IHeatmapRepository, HeatmapRepository>();
        services.AddScoped<ReelAllowances>();
        services.AddScoped<HeatmapAccess>();
        services.AddHostedService<ReelsMaintenance>();

        // Hashtags and Trending: tags linked when posts are saved, hourly counts rebuilt in the background.
        services.AddOptions<HashtagOptions>()
            .Bind(configuration.GetSection(HashtagOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HashtagOptions>, HashtagOptionsValidator>();
        services.AddScoped<IHashtagRepository, HashtagRepository>();
        services.AddScoped<UseCases.Hashtags.HashtagSync>();
        services.AddHostedService<HashtagMaintenance>();

        // Home timelines: For you (ranked, boosted near the home area), Following and custom timelines.
        services.AddScoped<ITimelineRepository, TimelineRepository>();

        // Communities (owner, 23 Oct 2026).
        services.AddScoped<ICommunityRepository, CommunityRepository>();
        services.AddScoped<IHouseholdCarRepository, HouseholdCarRepository>();
        services.AddHomeAreasFallback();

        // Who's posting where, for Who to follow in the Profiles module.
        services.Replace(ServiceDescriptor.Scoped<IActivePosters, ActivePosters>());

        // Post now, publish when ready: pending posts are published on media.ready / media.failed events.
        services.AddScoped<PostMediaPublisher>();
        services.AddMediaEventHandler<PostsMediaEventHandler>(configuration, connectionStringName);
        services.AddScoped<IPersonalDataExporter, PostsPersonalDataExporter>();
        services.AddScoped<IPersonalDataExporter, CommunitiesPersonalDataExporter>();
        services.AddUserLifecycleHandler<PostsUserLifecycleHandler>(configuration, connectionStringName);

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ApiScope)))
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasModeratorRights(ManageScope)))
            .AddPolicy(ReadPolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAssertion(_ => true));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<PostsDbContext>(cancellationToken);
}
