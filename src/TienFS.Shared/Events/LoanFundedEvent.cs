namespace TienFS.Shared.Events;

/// <summary>
/// Published by LoanFunding.Api once funds have been disbursed.
/// Consumed by LoanServicing.Api to open a servicing/payment account.
/// </summary>
public record LoanFundedEvent
{
    public required Guid LoanApplicationId { get; init; }
    public required Guid FundingRecordId { get; init; }
    public required decimal FundedAmount { get; init; }
    public required DateTimeOffset FundedAtUtc { get; init; }
}
