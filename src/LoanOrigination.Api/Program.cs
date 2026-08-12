// =====================================================================================
// LOAN ORIGINATION SERVICE
// -------------------------------------------------------------------------------------
// First microservice in the loan platform. Owns loan application intake and the
// approval decision. On approval, publishes a LoanApprovedEvent to Azure Service Bus
// so LoanFunding.Api can react — Origination never calls Funding directly.
//
// Also acts as the demo token issuer (see /api/auth/token) — see TokenIssuer.cs for
// why this is explicitly NOT how a real deployment should handle authentication.
// =====================================================================================

using Azure.Identity;
using LoanOrigination.Api.Data;
using LoanOrigination.Api.Models;
using TienFS.Shared.Auth;
using TienFS.Shared.Events;
using TienFS.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.ApplicationInsights.Extensibility;

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
// Database — each service owns its own, on SQL Server in every environment (real
// Azure SQL in production; a local SQL Server container in Development — see
// emulator/docker-compose.yml). Connection string comes from config either way:
// appsettings.Development.json locally, Key Vault in production (same "Sql-
// Origination-ConnectionString" key name, different source per environment via
// the Key Vault provider registered above).
// EnsureCreated() rather than migrations, to keep this sample runnable without a
// separate migration step — a real production service should switch to EF Core
// migrations (dotnet ef migrations add ...) for schema change control over time.
// -------------------------------------------------------------------------------------
var sqlConnectionString = builder.Configuration["Sql-Origination-ConnectionString"]
    ?? throw new InvalidOperationException(
        "Sql-Origination-ConnectionString must be configured — locally via " +
        "appsettings.Development.json, in production via Key Vault.");
builder.Services.AddDbContext<OriginationDbContext>(o => o.UseSqlServer(sqlConnectionString));

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

// -------------------------------------------------------------------------------------
// Auth — shared JWT Bearer validation (see TienFS.Shared/Auth/JwtAuthExtensions.cs).
// -------------------------------------------------------------------------------------
builder.Services.AddSharedJwtAuth(builder.Configuration);

// -------------------------------------------------------------------------------------
// CORS — permissive policy so a browser-based UI (served from a different origin,
// e.g. a static file server on a different port) can call this API directly.
// DEMO SIMPLIFICATION: AllowAnyOrigin is fine for local development against a UI
// you control, but a real production deployment should list specific allowed
// origins explicitly rather than accepting requests from anywhere.
// -------------------------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// -------------------------------------------------------------------------------------
// Monitoring — Application Insights. Connection string comes from config
// (APPLICATIONINSIGHTS_CONNECTION_STRING, set as an App Service setting by Bicep in
// production). In Development with no connection string configured, this becomes a
// harmless no-op rather than failing — same "degrade gracefully" pattern as the event
// bus and Key Vault above.
// -------------------------------------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetry(new ApplicationInsightsServiceOptions
{
    ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"],
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Loan Origination API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste the token from POST /api/auth/token here (no 'Bearer ' prefix needed).",
    });
    o.AddSecurityRequirement(new()
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer",
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Creates the database schema on startup if it doesn't exist yet — in every
// environment now that SQL Server (not SQLite) is used everywhere. Acceptable for
// this reference project; a real production service should run EF Core migrations
// as an explicit, reviewed release step instead of auto-creating schema on boot.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OriginationDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

const string TopicName = "loan-events";

// -------------------------------------------------------------------------------------
// Auth endpoint — demo token issuance. See TokenIssuer.cs for why this is NOT how a
// real deployment should handle authentication.
// -------------------------------------------------------------------------------------
app.MapPost("/api/auth/token", (LoginRequest request, IConfiguration config) =>
{
    var signingKey = config["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey must be configured.");
    var issuer = config["Jwt:Issuer"] ?? "tienfs-loan-platform";
    var audience = config["Jwt:Audience"] ?? "tienfs-loan-platform";

    // DEMO ONLY: no real password check. See file header for what a production
    // deployment must replace this with.
    if (string.IsNullOrWhiteSpace(request.Username))
        return Results.BadRequest(new { error = "Username is required." });

    var token = TokenIssuer.IssueDemoToken(signingKey, issuer, audience, request.Username);
    return Results.Ok(new { token, expiresInMinutes = 60 });
});

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
}).RequireAuthorization();

app.MapGet("/api/applications", async (OriginationDbContext db) =>
    Results.Ok(await db.Applications.AsNoTracking().ToListAsync())).RequireAuthorization();

app.MapGet("/api/applications/{id:guid}", async (Guid id, OriginationDbContext db) =>
{
    var application = await db.Applications.FindAsync(id);
    return application is not null ? Results.Ok(application) : Results.NotFound();
}).RequireAuthorization();

// -------------------------------------------------------------------------------------
// Automated decisioning — the step between "submit" and "approve" that actually
// evaluates the application. Deliberately simple rules for this reference project
// (real underwriting would weigh credit score, income, debt-to-income, collateral
// value, etc.) — but structurally this is where that logic lives: separate from the
// human finalize step, so a UI can show a suggestion and let a reviewer accept or
// override it before anything is committed or published.
// -------------------------------------------------------------------------------------
app.MapPost("/api/applications/{id:guid}/decision", async (Guid id, OriginationDbContext db) =>
{
    var application = await db.Applications.FindAsync(id);
    if (application is null) return Results.NotFound();

    const decimal maxEligibleAmount = 75_000m;
    var isEligible = application.RequestedAmount > 0 && application.RequestedAmount <= maxEligibleAmount;

    application.IsEligible = isEligible;
    application.Status = ApplicationStatus.UnderReview;

    if (isEligible)
    {
        application.SuggestedAmount = application.RequestedAmount;
        // Simple tiered rate — illustrative, not a real risk model.
        application.SuggestedRate = application.RequestedAmount switch
        {
            <= 15_000m => 5.99m,
            <= 35_000m => 6.49m,
            _ => 7.25m,
        };
        application.DecisionReason = "Eligible under automated review — within standard lending limits.";
    }
    else
    {
        application.SuggestedAmount = null;
        application.SuggestedRate = null;
        application.DecisionReason = application.RequestedAmount > maxEligibleAmount
            ? $"Requested amount exceeds automated approval limit of {maxEligibleAmount:C0}. Refer to manual underwriting."
            : "Requested amount must be greater than zero.";
    }

    await db.SaveChangesAsync();
    return Results.Ok(application);
}).RequireAuthorization();

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
}).RequireAuthorization();

app.MapPost("/api/applications/{id:guid}/deny", async (Guid id, OriginationDbContext db) =>
{
    var application = await db.Applications.FindAsync(id);
    if (application is null) return Results.NotFound();

    application.Status = ApplicationStatus.Denied;
    application.DecisionAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(application);
}).RequireAuthorization();

app.Run();

// Request record for the demo token endpoint.
public record LoginRequest(string Username);
