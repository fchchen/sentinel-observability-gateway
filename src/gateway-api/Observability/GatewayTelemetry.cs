using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gateway.Api.Observability;

public static class GatewayTelemetry
{
    public const string ServiceName = "sentinel-gateway-api";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
