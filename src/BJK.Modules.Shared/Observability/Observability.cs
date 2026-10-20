using System.Reflection;
using BJK.Modules.Shared.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BJK.Modules.Shared.Observability;

/// <summary>
/// Logs, traces and metrics for every host, through OpenTelemetry. They're sent over OTLP to
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: locally the <c>otel-lgtm</c> container in bjk-core's Docker group
/// (http://localhost:4318), when deployed Grafana Cloud (its OTLP address, with <c>OTEL_EXPORTER_OTLP_HEADERS</c> for
/// the key). Only the address changes between them, never the code. Without an address nothing is exported and
/// logging works as before (console). Outside Development the address is required (<see cref="DeployedSettingRule"/>).
/// </summary>
/// <remarks>
/// No personal data: request and outgoing URLs keep their path but query string values are redacted (the
/// instrumentation's default), SQL text isn't recorded, nothing identifies the user, and log messages follow the rule
/// that they never contain emails, names or IP addresses. Health checks aren't traced.
/// </remarks>
public static class ObservabilityExtensions
{
    /// <summary>The OTLP address setting (the standard OpenTelemetry name).</summary>
    public const string EndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Adds OpenTelemetry logs, traces and metrics for this host, named <paramref name="serviceName"/> (e.g.
    /// <c>bjk-core-api</c>), with the environment as <c>deployment.environment.name</c>.
    /// </summary>
    public static IHostApplicationBuilder AddBjkObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        builder.Services.RequireWhenDeployed(
            EndpointSetting,
            "the https OTLP address logs, traces and metrics go to (Grafana Cloud's, with OTEL_EXPORTER_OTLP_HEADERS for its key)",
            DeployedSettingChecks.IsHttps);

        if (string.IsNullOrWhiteSpace(builder.Configuration[EndpointSetting]))
        {
            return builder;
        }

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsHealthCheck(context))
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();

        return builder;
    }

    private static bool IsHealthCheck(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
