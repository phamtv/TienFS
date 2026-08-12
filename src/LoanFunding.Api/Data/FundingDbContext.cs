using Microsoft.EntityFrameworkCore;
using LoanFunding.Api.Models;

namespace LoanFunding.Api.Data;

/// <summary>
/// Funding owns its own database, separate from Origination's. It never queries
/// Origination's database directly — everything it knows about an application
/// comes from the LoanApprovedEvent it received.
/// </summary>
public class FundingDbContext : DbContext
{
    public FundingDbContext(DbContextOptions<FundingDbContext> options) : base(options) { }

    public DbSet<FundingRecord> FundingRecords => Set<FundingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FundingRecord>(entity =>
        {
            entity.ToTable("FundingRecords");
            entity.HasKey(e => e.Id);

            // One funding record per approved loan — enforced at the database level,
            // not just by application logic, so a duplicate/replayed LoanApprovedEvent
            // can't silently create a second disbursement for the same loan even if
            // the idempotency check in LoanApprovedSubscriber were ever bypassed.
            entity.HasIndex(e => e.LoanApplicationId).IsUnique();

            entity.Property(e => e.ApplicantName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.InterestRate).HasColumnType("decimal(6,4)");

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.CreatedAtUtc).IsRequired();
        });
    }
}
