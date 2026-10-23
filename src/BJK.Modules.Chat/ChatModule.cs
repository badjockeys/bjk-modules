using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Infrastructure;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Queues;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat;

/// <summary>
/// Registers Ask Bad Jockeys, the Premium AI chat. An open model (Qwen3 through an OpenAI-compatible server) writes
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
        services.RequireWhenDeployed(
            "Chat:Model:Endpoint",
            "the chat model server's address, ending /v1 (without it Ask Bad Jockeys answers 503)");
        services.RequireWhenDeployed(
            $"ConnectionStrings:{chatConnectionStringName}",
            "the bjk_chat login's connection string (it can read only reporting.Incidents and reporting.Subcategories)");

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new ChatDatabase(configuration.GetConnectionString(chatConnectionStringName)));
        services.AddScoped<IIncidentQueries, SqlIncidentQueries>();
        services.AddScoped<IChatUsage, ChatUsage>();
        services.AddScoped<ChatAgent>();
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
