using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.Posts;

/// <summary>
/// Registers the Posts module: incident posts, reactions, comments and reports. Hosted by the bjk-core API,
/// which must also register the Media and Profiles modules (posts use their media and authors' profiles)
/// and a JWT bearer handler under <see cref="ModuleAuthentication.Scheme"/>.
/// </summary>
/// <param name="connectionStringName">Connection string for the app database holding the <see cref="Schema"/> tables.</param>
public sealed class PostsModule(string connectionStringName) : IModule
{
    public const string Schema = "posts";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Scope the apps need to post, react, comment and report.</summary>
    public const string ApiScope = "bjk.posts";

    /// <summary>Admin scope: review reports, hide and restore content, delete anyone's posts and comments.</summary>
    public const string ManageScope = "bjk.posts.manage";

    /// <summary>Policy for a signed-in user with <see cref="ApiScope"/>.</summary>
    public const string UsePolicy = "bjk.posts";

    /// <summary>Policy for public reads. Anyone may call; a token, if sent, identifies the caller.</summary>
    public const string ReadPolicy = "bjk.posts.read";

    /// <summary>Policy for admin actions: <see cref="ManageScope"/>.</summary>
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
        services.AddScoped<IPostRepository, PostRepository>();
        services.AddScoped<IReactionRepository, ReactionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<PostViews>();
        services.AddScoped<IPersonalDataExporter, PostsPersonalDataExporter>();
        services.AddUserLifecycleHandler<PostsUserLifecycleHandler>(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ApiScope)))
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ManageScope)))
            .AddPolicy(ReadPolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAssertion(_ => true));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<PostsDbContext>(cancellationToken);
}
