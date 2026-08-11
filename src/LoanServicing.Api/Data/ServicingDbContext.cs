using Microsoft.EntityFrameworkCore;
using LoanServicing.Api.Models;

namespace LoanServicing.Api.Data;

public class ServicingDbContext : DbContext
{
    public ServicingDbContext(DbContextOptions<ServicingDbContext> options) : base(options) { }

    public DbSet<ServicingAccount> Accounts => Set<ServicingAccount>();
}
