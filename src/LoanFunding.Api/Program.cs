// =====================================================================================
// LOAN FUNDING SERVICE
// -------------------------------------------------------------------------------------
// Second microservice. Independently subscribes to LoanApprovedEvent published by
// LoanOrigination.Api (no direct call between the two services), disburses funds,
// and publishes LoanFundedEvent for LoanServicing.Api to react to.
// =====================================================================================

using Azure.Identity;
using LoanFunding.Api.Data;
using LoanFunding.Api.Messaging;
using TienFS.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"]
        ?? throw new InvalidOperationException("Key Vault URI must be configured.");
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Services.AddDbContext<FundingDbContext>(o => o.UseInMemoryDatabase("FundingDb"));

var serviceBusConnectionString = builder.Configuration["ServiceBus:ConnectionString"];
if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
{
    builder.Services.AddSingleton<IEventBus>(sp =>
        new ServiceBusEventBus(serviceBusConnectionString, sp.GetRequiredService<ILogger<ServiceBusEventBus>>()));
}
else
{
    builder.Services.AddSingleton<IEventBus, NullEventBus>();
}

// The background subscriber that listens for LoanApprovedEvent — registered as a
// hosted service so it starts automatically with the app and runs for its lifetime.
builder.Services.AddHostedService<LoanApprovedSubscriber>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Loan Funding API", Version = "v1" }));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "funding" }));

app.MapGet("/api/funding", async (FundingDbContext db) =>
    Results.Ok(await db.FundingRecords.AsNoTracking().ToListAsync()));

app.MapGet("/api/funding/{id:guid}", async (Guid id, FundingDbContext db) =>
{
    var record = await db.FundingRecords.FindAsync(id);
    return record is not null ? Results.Ok(record) : Results.NotFound();
});

app.MapGet("/api/funding/by-application/{loanApplicationId:guid}", async (Guid loanApplicationId, FundingDbContext db) =>
{
    var record = await db.FundingRecords
        .AsNoTracking()
        .FirstOrDefaultAsync(f => f.LoanApplicationId == loanApplicationId);
    return record is not null ? Results.Ok(record) : Results.NotFound();
});

app.Run();
