namespace LoanOrigination.Api.Models;

public enum ApplicationStatus { Submitted, UnderReview, Approved, Denied }

public class LoanApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ApplicantName { get; set; } = string.Empty;
    public decimal RequestedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal? InterestRate { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Submitted;
    public DateTimeOffset SubmittedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecisionAtUtc { get; set; }
}

public record SubmitApplicationRequest(string ApplicantName, decimal RequestedAmount);
public record ApproveApplicationRequest(decimal ApprovedAmount, decimal InterestRate);
