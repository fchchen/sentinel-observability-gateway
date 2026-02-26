using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Processor.Worker.Observability;

public static class WorkerTelemetry
{
    public const string ServiceName = "sentinel-processor-worker";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
