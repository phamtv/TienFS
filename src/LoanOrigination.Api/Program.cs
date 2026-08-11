// =====================================================================================
// LOAN ORIGINATION SERVICE
// -------------------------------------------------------------------------------------
// First microservice in the loan platform. Owns loan application intake and the
// approval decision. On approval, publishes a LoanApprovedEvent to Azure Service Bus
// so LoanFunding.Api can react — Origination never calls Funding directly.
// =====================================================================================

using Azure.Identity;
using LoanOrigination.Api.Data;
using LoanOrigination.Api.Models;
using TienFS.Shared.Events;
using TienFS.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// -------------------------------------------------------------------------------------
// Key Vault — skipped in Development, same pattern as the earlier security-sample
// -------------------------------------------------------------------------------------
if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"]
        ?? throw new InvalidOperationException("Key Vault URI must be configured.");
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// -------------------------------------------------------------------------------------
// Database — each service owns its own. InMemory here for a runnable sample;
// swap for UseSqlServer(...) against a real Azure SQL/SQL Server instance in production.
// -------------------------------------------------------------------------------------
builder.Services.AddDbContext<OriginationDbContext>(o => o.UseInMemoryDatabase("OriginationDb"));

// -------------------------------------------------------------------------------------
// Event bus — real Service Bus if a connection string is configured (works against
// the local Docker emulator too), otherwise the safe no-op fallback so this service
// can be run and tested completely on its own.
// -------------------------------------------------------------------------------------
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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Loan Origination API", Version = "v1" }));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

const string TopicName = "loan-events";

// -------------------------------------------------------------------------------------
// Endpoints
// -------------------------------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "origination" }));

app.MapPost("/api/applications", async (SubmitApplicationRequest request, OriginationDbContext db) =>
{
    var application = new LoanApplication
    {
        ApplicantName = request.ApplicantName,
        RequestedAmount = request.RequestedAmount,
    };
    db.Applications.Add(application);
    await db.SaveChangesAsync();
    return Results.Created($"/api/applications/{application.Id}", application);
});

app.MapGet("/api/applications", async (OriginationDbContext db) =>
    Results.Ok(await db.Applications.AsNoTracking().ToListAsync()));

app.MapGet("/api/applications/{id:guid}", async (Guid id, OriginationDbContext db) =>
{
    var application = await db.Applications.FindAsync(id);
    return application is not null ? Results.Ok(application) : Results.NotFound();
});

// This is the key integration point: approving a loan publishes an event that
// LoanFunding.Api independently subscribes to and reacts to — Origination has
// no idea who's listening, or how many services react to this event.
app.MapPost("/api/applications/{id:guid}/approve", async (
    Guid id,
    ApproveApplicationRequest request,
    OriginationDbContext db,
    IEventBus eventBus,
    ILogger<Program> logger) =>
{
    var application = await db.Applications.FindAsync(id);
    if (application is null) return Results.NotFound();

    application.Status = ApplicationStatus.Approved;
    application.ApprovedAmount = request.ApprovedAmount;
    application.InterestRate = request.InterestRate;
    application.DecisionAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    var evt = new LoanApprovedEvent
    {
        LoanApplicationId = application.Id,
        ApplicantName = application.ApplicantName,
        ApprovedAmount = request.ApprovedAmount,
        InterestRate = request.InterestRate,
        ApprovedAtUtc = application.DecisionAtUtc.Value,
    };

    await eventBus.PublishAsync(TopicName, subject: "LoanApproved", evt);
    logger.LogInformation("Application {Id} approved and LoanApprovedEvent published", id);

    return Results.Ok(application);
});

app.MapPost("/api/applications/{id:guid}/deny", async (Guid id, OriginationDbContext db) =>
{
    var application = await db.Applications.FindAsync(id);
    if (application is null) return Results.NotFound();

    application.Status = ApplicationStatus.Denied;
    application.DecisionAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(application);
});

app.Run();
