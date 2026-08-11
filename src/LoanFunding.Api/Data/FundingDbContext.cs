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
}
