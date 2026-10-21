using BJK.Modules.Shared.Configuration;
using BJK.Modules.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Xunit;

namespace BJK.Modules.Observability.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public void Without_an_address_nothing_is_exported()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        builder.AddBjkObservability("bjk-test");
        using var host = builder.Build();

        Assert.Null(host.Services.GetService<TracerProvider>());
        Assert.Contains(host.Services.GetServices<DeployedSettingRule>(), r => r.Key == ObservabilityExtensions.EndpointSetting);
    }

    [Fact]
    public void With_an_address_logs_traces_and_metrics_are_set_up()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration[ObservabilityExtensions.EndpointSetting] = "http://localhost:4318";
        builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

        builder.AddBjkObservability("bjk-test");
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<TracerProvider>());
    }
}
