using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Processor.Worker.Observability;

public sealed class WorkerMetrics
{
    private readonly Counter<long> _processorEventsTotal;
    private readonly Counter<long> _dlqEventsTotal;
    private readonly Histogram<double> _endToEndFreshnessSeconds;
    private double _lastLagSeconds;

    public WorkerMetrics()
    {
        _processorEventsTotal = WorkerTelemetry.Meter.CreateCounter<long>("processor_events_total");
        _dlqEventsTotal = WorkerTelemetry.Meter.CreateCounter<long>("dlq_events_total");
        _endToEndFreshnessSeconds = WorkerTelemetry.Meter.CreateHistogram<double>("end_to_end_freshness_seconds");
        WorkerTelemetry.Meter.CreateObservableGauge(
            name: "processor_lag_seconds",
            observeValue: () => new Measurement<double>(Volatile.Read(ref _lastLagSeconds)));
    }

    public void RecordSuccess()
    {
        _processorEventsTotal.Add(1, new TagList { { "result", "success" } });
    }

    public void RecordRetry()
    {
        _processorEventsTotal.Add(1, new TagList { { "result", "retry" } });
    }

    public void RecordDlq()
    {
        var tags = new TagList { { "result", "dlq" } };
        _processorEventsTotal.Add(1, tags);
        _dlqEventsTotal.Add(1);
    }

    public void RecordLag(double lagSeconds)
    {
        Volatile.Write(ref _lastLagSeconds, Math.Max(0, lagSeconds));
    }

    public void RecordFreshness(double freshnessSeconds)
    {
        _endToEndFreshnessSeconds.Record(Math.Max(0, freshnessSeconds));
    }
}
