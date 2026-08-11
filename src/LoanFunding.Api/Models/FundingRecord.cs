namespace LoanFunding.Api.Models;

public enum FundingStatus { Pending, Disbursed, Failed }

public class FundingRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoanApplicationId { get; set; }
    public string ApplicantName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal InterestRate { get; set; }
    public FundingStatus Status { get; set; } = FundingStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisbursedAtUtc { get; set; }
}
