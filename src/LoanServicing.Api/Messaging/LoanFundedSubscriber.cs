using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LoanServicing.Api.Data;
using LoanServicing.Api.Models;
using TienFS.Shared.Events;
using Microsoft.EntityFrameworkCore;

namespace LoanServicing.Api.Messaging;

/// <summary>
/// Final link in the chain: subscribes to LoanFundedEvent (published by
/// LoanFunding.Api) and opens a servicing account. This service doesn't publish
/// any further event — the loan lifecycle sample ends here — but the same pattern
/// (subscribe, process idempotently, complete or let it retry) would extend to
/// however many downstream services needed to react to a funded loan.
/// </summary>
public class LoanFundedSubscriber : BackgroundService
{
    private const string TopicName = "loan-events";
    private const string SubscriptionName = "servicing-loan-funded";

    private readonly IServiceProvider _services;
    private readonly ILogger<LoanFundedSubscriber> _logger;
    private readonly string? _connectionString;
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    public LoanFundedSubscriber(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<LoanFundedSubscriber> logger)
    {
        _services = services;
        _logger = logger;
        _connectionString = configuration["ServiceBus:ConnectionString"];
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning(
                "No Service Bus connection string configured — LoanFundedSubscriber will not start.");
            return;
        }

        _client = new ServiceBusClient(_connectionString);
        _processor = _client.CreateProcessor(TopicName, SubscriptionName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
        });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        await _processor.StartProcessingAsync(cancellationToken);
        _logger.LogInformation("LoanFundedSubscriber started, listening on {Topic}/{Subscription}",
            TopicName, SubscriptionName);
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<LoanFundedEvent>(args.Message.Body.ToString())
                ?? throw new InvalidOperationException("Failed to deserialize LoanFundedEvent.");

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicingDbContext>();

            var alreadyOpened = await db.Accounts
                .AnyAsync(a => a.LoanApplicationId == evt.LoanApplicationId);

            if (alreadyOpened)
            {
                _logger.LogInformation(
                    "Servicing account for application {Id} already exists — skipping duplicate event.",
                    evt.LoanApplicationId);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var account = new ServicingAccount
            {
                LoanApplicationId = evt.LoanApplicationId,
                PrincipalBalance = evt.FundedAmount,
                InterestRate = 0, // not carried on LoanFundedEvent by design — see README for discussion
            };

            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Opened servicing account {AccountId} for application {Id}",
                account.Id, evt.LoanApplicationId);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process LoanFundedEvent message {MessageId}", args.Message.MessageId);
            // Not completed — will be redelivered/eventually dead-lettered per the
            // subscription's max delivery count, same reasoning as LoanApprovedSubscriber.
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
