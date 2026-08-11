using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LoanFunding.Api.Data;
using LoanFunding.Api.Models;
using TienFS.Shared.Events;
using TienFS.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace LoanFunding.Api.Messaging;

/// <summary>
/// Runs for the lifetime of the application, listening on a Service Bus subscription
/// for LoanApprovedEvent messages. This is the "microservice reacting to another
/// microservice's event" piece — Funding never receives a direct call from
/// Origination; it independently listens for events it cares about.
///
/// Only runs when a Service Bus connection string is configured — in NullEventBus
/// mode (no connection string), this background service simply doesn't start,
/// so the API can still run standalone for basic testing via Swagger.
/// </summary>
public class LoanApprovedSubscriber : BackgroundService
{
    private const string TopicName = "loan-events";
    private const string SubscriptionName = "funding-loan-approved";

    private readonly IServiceProvider _services;
    private readonly IEventBus _eventBus;
    private readonly ILogger<LoanApprovedSubscriber> _logger;
    private readonly string? _connectionString;
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    public LoanApprovedSubscriber(
        IServiceProvider services,
        IEventBus eventBus,
        IConfiguration configuration,
        ILogger<LoanApprovedSubscriber> logger)
    {
        _services = services;
        _eventBus = eventBus;
        _logger = logger;
        _connectionString = configuration["ServiceBus:ConnectionString"];
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning(
                "No Service Bus connection string configured — LoanApprovedSubscriber will not start. " +
                "This service will not react to approved loans until Service Bus is configured.");
            return;
        }

        _client = new ServiceBusClient(_connectionString);
        _processor = _client.CreateProcessor(TopicName, SubscriptionName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,          // process one at a time — simple, predictable for a sample
            AutoCompleteMessages = false,     // we complete manually, only after successful processing
        });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        await _processor.StartProcessingAsync(cancellationToken);
        _logger.LogInformation("LoanApprovedSubscriber started, listening on {Topic}/{Subscription}",
            TopicName, SubscriptionName);
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<LoanApprovedEvent>(args.Message.Body.ToString())
                ?? throw new InvalidOperationException("Failed to deserialize LoanApprovedEvent.");

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();

            // Idempotency check: if we've already processed this loan application
            // (e.g., the message was redelivered after a crash before we completed it),
            // don't fund it twice. This matters because Service Bus's "at-least-once"
            // delivery guarantee means duplicate delivery is a normal, expected case,
            // not an edge case to ignore.
            var alreadyProcessed = await db.FundingRecords
                .AnyAsync(f => f.LoanApplicationId == evt.LoanApplicationId);

            if (alreadyProcessed)
            {
                _logger.LogInformation(
                    "Funding record for application {Id} already exists — skipping duplicate event.",
                    evt.LoanApplicationId);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var record = new FundingRecord
            {
                LoanApplicationId = evt.LoanApplicationId,
                ApplicantName = evt.ApplicantName,
                Amount = evt.ApprovedAmount,
                InterestRate = evt.InterestRate,
                Status = FundingStatus.Disbursed,   // simulated — a real system would call a payment rail here
                DisbursedAtUtc = DateTimeOffset.UtcNow,
            };

            db.FundingRecords.Add(record);
            await db.SaveChangesAsync();

            var fundedEvent = new LoanFundedEvent
            {
                LoanApplicationId = evt.LoanApplicationId,
                FundingRecordId = record.Id,
                FundedAmount = record.Amount,
                FundedAtUtc = record.DisbursedAtUtc.Value,
            };

            await _eventBus.PublishAsync("loan-events", subject: "LoanFunded", fundedEvent);

            _logger.LogInformation(
                "Funded loan application {Id}, published LoanFundedEvent", evt.LoanApplicationId);

            // Only complete (remove from queue) after successful processing AND
            // successful publish of the downstream event — if anything above threw,
            // the message stays in the subscription and will be redelivered/retried.
            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process LoanApprovedEvent message {MessageId}", args.Message.MessageId);
            // Deliberately NOT completing the message — Service Bus will redeliver
            // it based on the subscription's max delivery count, eventually dead-
            // lettering it if it keeps failing. This is the dead-lettering concept
            // in action: don't lose the message, don't silently swallow the failure.
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service Bus processor error in {Source}", args.ErrorSource);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) await _processor.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}
