using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace TienFS.Shared.Messaging;

/// <summary>
/// Real Azure Service Bus-backed event bus. Works against either a real Azure
/// Service Bus namespace or the local Docker emulator — same connection-string
/// based client either way, which is exactly why the emulator is useful for dev.
/// </summary>
public class ServiceBusEventBus : IEventBus, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusEventBus> _logger;

    public ServiceBusEventBus(string connectionString, ILogger<ServiceBusEventBus> logger)
    {
        _client = new ServiceBusClient(connectionString);
        _logger = logger;
    }

    public async Task PublishAsync<T>(string topicName, string subject, T message, CancellationToken ct = default)
    {
        await using var sender = _client.CreateSender(topicName);

        var body = JsonSerializer.Serialize(message);
        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = subject,              // used by subscription rules for filtering
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString(), // supports duplicate detection if enabled
        };

        await sender.SendMessageAsync(serviceBusMessage, ct);

        _logger.LogInformation(
            "Published {Subject} event to topic {Topic} (MessageId: {MessageId})",
            subject, topicName, serviceBusMessage.MessageId);
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
