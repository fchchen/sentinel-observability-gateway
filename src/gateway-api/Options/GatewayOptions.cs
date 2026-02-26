namespace Gateway.Api.Options;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string KafkaBootstrapServers { get; set; } = "localhost:29092";

    public string KafkaTopic { get; set; } = "events.raw.v1";
}
