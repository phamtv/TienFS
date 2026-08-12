using Microsoft.EntityFrameworkCore;
using LoanServicing.Api.Models;

namespace LoanServicing.Api.Data;

/// <summary>
/// Servicing owns its own database, separate from Origination's and Funding's.
/// It never queries either directly — everything it knows comes from the
/// LoanFundedEvent it received.
/// </summary>
public class ServicingDbContext : DbContext
{
    public ServicingDbContext(DbContextOptions<ServicingDbContext> options) : base(options) { }

    public DbSet<ServicingAccount> Accounts => Set<ServicingAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServicingAccount>(entity =>
        {
            entity.ToTable("ServicingAccounts");
            entity.HasKey(e => e.Id);

            // One servicing account per funded loan — same reasoning as Funding's
            // unique index on LoanApplicationId.
            entity.HasIndex(e => e.LoanApplicationId).IsUnique();

            entity.Property(e => e.PrincipalBalance).HasColumnType("decimal(18,2)");
            entity.Property(e => e.InterestRate).HasColumnType("decimal(6,4)");

            entity.Property(e => e.OpenedAtUtc).IsRequired();
            entity.Property(e => e.NextPaymentDueUtc).IsRequired();

            // Servicing's whole job is tracking upcoming payments — an index here
            // is what makes "which accounts have a payment due soon" a cheap query
            // instead of a full scan as the book of business grows.
            entity.HasIndex(e => e.NextPaymentDueUtc);
        });
    }
}
