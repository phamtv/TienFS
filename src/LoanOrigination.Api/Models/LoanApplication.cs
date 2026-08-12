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

    // Populated by the automated decision step (see /api/applications/{id}/decision).
    // Deliberately separate from ApprovedAmount/InterestRate above — a suggestion is
    // not a commitment. A reviewer can accept these as-is or override them when
    // finalizing via /approve; only the final /approve call is what actually commits
    // terms and publishes LoanApprovedEvent.
    public bool? IsEligible { get; set; }
    public decimal? SuggestedAmount { get; set; }
    public decimal? SuggestedRate { get; set; }
    public string? DecisionReason { get; set; }
}

public record SubmitApplicationRequest(string ApplicantName, decimal RequestedAmount);
public record ApproveApplicationRequest(decimal ApprovedAmount, decimal InterestRate);
