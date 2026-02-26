using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Realtime.Hub.Observability;

public static class RealtimeTelemetry
{
    public const string ServiceName = "sentinel-realtime-hub";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
