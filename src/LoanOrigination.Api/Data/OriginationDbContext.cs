using Microsoft.EntityFrameworkCore;
using LoanOrigination.Api.Models;

namespace LoanOrigination.Api.Data;

/// <summary>
/// Origination owns its own database — no other microservice reads or writes
/// to it directly. This is the core "each microservice owns its data" rule:
/// Funding and Servicing only ever learn about an approved loan through the
/// LoanApprovedEvent, never by querying this database directly.
/// </summary>
public class OriginationDbContext : DbContext
{
    public OriginationDbContext(DbContextOptions<OriginationDbContext> options) : base(options) { }

    public DbSet<LoanApplication> Applications => Set<LoanApplication>();

    // -----------------------------------------------------------------------------
    // Explicit schema — deliberately not left to EF Core's default conventions.
    // Money and rate columns especially need real, intentional precision rather
    // than whatever EF happens to infer; getting this wrong silently (e.g. a rate
    // truncating to 2 decimal places) is the kind of bug a financial system can't
    // afford to leave to defaults.
    // -----------------------------------------------------------------------------
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoanApplication>(entity =>
        {
            entity.ToTable("LoanApplications");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ApplicantName)
                .IsRequired()
                .HasMaxLength(200);

            // decimal(18,2): up to ~$9.99 quadrillion with cent precision — far more
            // headroom than a loan platform needs, but a standard, unsurprising money
            // column size that won't silently truncate real-world loan amounts.
            entity.Property(e => e.RequestedAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ApprovedAmount).HasColumnType("decimal(18,2)");

            // decimal(6,4): supports rates like 12.3456% — enough precision that
            // rate calculations don't lose accuracy through repeated rounding.
            entity.Property(e => e.InterestRate).HasColumnType("decimal(6,4)");

            // Stored as string, not int — a DBA or auditor looking directly at the
            // table sees "Approved", not an opaque "2". Costs a few extra bytes per
            // row; worth it for a financial system anyone might need to audit.
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.SubmittedAtUtc).IsRequired();

            // Decision-suggestion fields — same precision reasoning as above, but
            // nullable since they're only populated once /decision has run.
            entity.Property(e => e.SuggestedAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.SuggestedRate).HasColumnType("decimal(6,4)");
            entity.Property(e => e.DecisionReason).HasMaxLength(500);

            // Queried by the /api/applications listing endpoint and implicitly by
            // status-based reporting — an index here keeps that cheap as the table
            // grows, rather than degrading to a full table scan.
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubmittedAtUtc);
        });
    }
}
