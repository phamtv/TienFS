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
}
