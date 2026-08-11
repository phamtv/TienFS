namespace TienFS.Shared.Messaging;

/// <summary>
/// Abstraction over "publish an event so other microservices can react to it."
/// Two implementations exist: ServiceBusEventBus (real Azure Service Bus / emulator)
/// and NullEventBus (safe no-op fallback so an individual service can still be run
/// and tested on its own, e.g., via Swagger, without a running Service Bus).
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event to the given topic, with a subject used for
    /// subscription filtering (e.g., "LoanApproved", "LoanFunded").
    /// </summary>
    Task PublishAsync<T>(string topicName, string subject, T message, CancellationToken ct = default);
}
