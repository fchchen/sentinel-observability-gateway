namespace Processor.Worker.Options;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public string KafkaBootstrapServers { get; set; } = "localhost:29092";

    public string KafkaTopic { get; set; } = "events.raw.v1";

    public string KafkaGroupId { get; set; } = "sentinel-processor-worker";

    public string RealtimePublishUrl { get; set; } = "http://localhost:8082/v1/realtime/publish";
}
