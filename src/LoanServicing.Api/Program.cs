// =====================================================================================
// LOAN SERVICING SERVICE
// -------------------------------------------------------------------------------------
// Third microservice. Independently subscribes to LoanFundedEvent published by
// LoanFunding.Api and opens a servicing/payment account. Completes the loan
// origination -> funding -> servicing event chain.
// =====================================================================================

using Azure.Identity;
using LoanServicing.Api.Data;
using LoanServicing.Api.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"]
        ?? throw new InvalidOperationException("Key Vault URI must be configured.");
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Services.AddDbContext<ServicingDbContext>(o => o.UseInMemoryDatabase("ServicingDb"));

// Servicing only ever consumes events in this sample — it doesn't publish any
// further event, so it doesn't need IEventBus registered at all.
builder.Services.AddHostedService<LoanFundedSubscriber>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Loan Servicing API", Version = "v1" }));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "servicing" }));

app.MapGet("/api/accounts", async (ServicingDbContext db) =>
    Results.Ok(await db.Accounts.AsNoTracking().ToListAsync()));

app.MapGet("/api/accounts/{id:guid}", async (Guid id, ServicingDbContext db) =>
{
    var account = await db.Accounts.FindAsync(id);
    return account is not null ? Results.Ok(account) : Results.NotFound();
});

app.MapGet("/api/accounts/by-application/{loanApplicationId:guid}", async (Guid loanApplicationId, ServicingDbContext db) =>
{
    var account = await db.Accounts
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.LoanApplicationId == loanApplicationId);
    return account is not null ? Results.Ok(account) : Results.NotFound();
});

app.Run();
