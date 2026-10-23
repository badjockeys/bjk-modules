using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Guard;
using BJK.Modules.Chat.Infrastructure;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Queues;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat;

/// <summary>
/// Registers Ask Bad Jockeys, the Premium AI chat. A model behind an OpenAI-compatible server (chosen by settings) writes
/// SQL over <c>reporting.Incidents</c> and <c>reporting.Subcategories</c>; every query is checked by a T-SQL parser,
/// limited to the member's plan dates and run through the bjk_chat login, which can read nothing else. Counts under 3
/// are hidden and single incidents can't be listed. Daily limits are kept in the <see cref="Schema"/> schema of the
/// app database. Hosted by the bjk-core API.
/// </summary>
/// <param name="connectionStringName">The app database (for the daily counts), e.g. <c>BadJockeysDatabase</c>.</param>
/// <param name="chatConnectionStringName">The bjk_chat login's connection string, e.g. <c>BadJockeysChat</c>.</param>
/// <example>
/// <code>
/// builder.Services.AddBjkModules(builder.Configuration, ..., new ChatModule("BadJockeysDatabase", "BadJockeysChat"));
/// </code>
/// </example>
public sealed class ChatModule(string connectionStringName, string chatConnectionStringName) : IModule
{
    public const string Schema = "chat";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Policy for the allowance: anyone may call; a token, if sent, identifies the caller.</summary>
    public const string ReadPolicy = "bjk.chat.read";

    public string Name => "Chat";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<ChatDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));

        services.AddOptions<ChatOptions>()
            .Bind(configuration.GetSection(ChatOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChatOptions>, ChatOptionsValidator>());
        // No model is chosen yet (owner, 23 Oct 2026): without Chat:Model:Endpoint the chat logs each question it
        // couldn't send (event 4905) and answers "not switched on yet", in every environment.
        services.RequireWhenDeployed(
            "Chat:HistoryKey",
            "a random secret of at least 32 characters that signs the chat history",
            value => value?.Length >= 32);
        services.RequireWhenDeployed(
            $"ConnectionStrings:{chatConnectionStringName}",
            "the bjk_chat login's connection string (it can read only reporting.Incidents and reporting.Subcategories)");

        services.TryAddSingleton(TimeProvider.System);

        // Home areas tailor the example questions and "near me"; without the Notifications module nobody has one.
        services.AddHomeAreasFallback();
        services.AddSingleton(new ChatDatabase(configuration.GetConnectionString(chatConnectionStringName)));
        services.AddScoped<IIncidentQueries, SqlIncidentQueries>();
        services.AddScoped<IChatUsage, ChatUsage>();
        services.AddScoped<ChatAgent>();

        // The guard rails: fixed checks, the shared content screener (keywords, plus the classifier when
        // Screening:Endpoint is set), the topic gate and signed history.
        services.AddContentScreening(configuration);
        services.AddScoped<ITopicGate, ModelTopicGate>();
        services.AddScoped<ChatGuard>();
        services.AddSingleton<TurnTokens>();
        services.AddHttpClient<IChatModel, OpenAiCompatibleChatModel>((sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ChatOptions>>().Value.Model.TimeoutSeconds));

        services.AddScoped<IPersonalDataExporter, ChatPersonalDataExporter>();
        services.AddUserLifecycleHandler<ChatUserLifecycleHandler>(configuration, connectionStringName);

        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAssertion(_ => true));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<ChatDbContext>(cancellationToken);
}
