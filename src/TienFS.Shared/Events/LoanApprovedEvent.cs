namespace TienFS.Shared.Events;

/// <summary>
/// Published by LoanOrigination.Api when an application is approved.
/// Consumed by LoanFunding.Api to begin the funding process.
/// This is the contract both services agree on — the "API" between them,
/// just delivered over a message bus instead of a synchronous HTTP call.
/// </summary>
public record LoanApprovedEvent
{
    public required Guid LoanApplicationId { get; init; }
    public required string ApplicantName { get; init; }
    public required decimal ApprovedAmount { get; init; }
    public required decimal InterestRate { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }
}
