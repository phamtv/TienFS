namespace LoanServicing.Api.Models;

public class ServicingAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoanApplicationId { get; set; }
    public decimal PrincipalBalance { get; set; }
    public decimal InterestRate { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextPaymentDueUtc { get; set; } = DateTimeOffset.UtcNow.AddMonths(1);
}
