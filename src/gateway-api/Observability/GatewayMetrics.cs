using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gateway.Api.Observability;

public sealed class GatewayMetrics
{
    private readonly Counter<long> _requestsTotal;
    private readonly Histogram<double> _requestDurationMs;

    public GatewayMetrics()
    {
        _requestsTotal = GatewayTelemetry.Meter.CreateCounter<long>("gateway_requests_total");
        _requestDurationMs = GatewayTelemetry.Meter.CreateHistogram<double>("gateway_request_duration_ms");
    }

    public void RecordRequest(int statusCode, double durationMs)
    {
        var tags = new TagList
        {
            { "status", statusCode.ToString() }
        };

        _requestsTotal.Add(1, tags);
        _requestDurationMs.Record(durationMs, tags);
    }
}
