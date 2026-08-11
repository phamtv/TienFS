using Microsoft.Extensions.Logging;

namespace TienFS.Shared.Messaging;

/// <summary>
/// Safe no-op event bus, used when no Service Bus connection string is configured.
/// Lets a single microservice (e.g., just LoanOrigination.Api) be run and tested
/// via Swagger on its own — the API call still succeeds, it just logs what WOULD
/// have been published instead of failing outright with a connection error.
/// This mirrors the DevSecretRetriever pattern from the security-sample project:
/// make the "no real backing service available" case explicit and obvious, not
/// a silent failure or a confusing exception.
/// </summary>
public class NullEventBus : IEventBus
{
    private readonly ILogger<NullEventBus> _logger;

    public NullEventBus(ILogger<NullEventBus> logger) => _logger = logger;

    public Task PublishAsync<T>(string topicName, string subject, T message, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "[NullEventBus] No Service Bus configured — would have published {Subject} to topic {Topic}. " +
            "Run the Service Bus emulator (see /emulator) for full cross-service testing.",
            subject, topicName);
        return Task.CompletedTask;
    }
}
