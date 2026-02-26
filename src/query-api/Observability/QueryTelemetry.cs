using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Query.Api.Observability;

public static class QueryTelemetry
{
    public const string ServiceName = "sentinel-query-api";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
