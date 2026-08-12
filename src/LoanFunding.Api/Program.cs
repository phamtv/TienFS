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
using TienFS.Shared.Auth;
using TienFS.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.ApplicationInsights.Extensibility;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"]
        ?? throw new InvalidOperationException("Key Vault URI must be configured.");
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// Database — SQL Server in every environment. See LoanOrigination.Api/Program.cs
// for the full rationale (same pattern here).
var sqlConnectionString = builder.Configuration["Sql-Funding-ConnectionString"]
    ?? throw new InvalidOperationException(
        "Sql-Funding-ConnectionString must be configured — locally via " +
        "appsettings.Development.json, in production via Key Vault.");
builder.Services.AddDbContext<FundingDbContext>(o => o.UseSqlServer(sqlConnectionString));

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

// Auth — validates tokens issued by Origination's /api/auth/token (same shared key).
builder.Services.AddSharedJwtAuth(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Monitoring — Application Insights, same graceful-fallback pattern as elsewhere.
builder.Services.AddApplicationInsightsTelemetry(new ApplicationInsightsServiceOptions
{
    ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"],
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Loan Funding API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste a token obtained from Origination's POST /api/auth/token here.",
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "funding" }));

app.MapGet("/api/funding", async (FundingDbContext db) =>
    Results.Ok(await db.FundingRecords.AsNoTracking().ToListAsync())).RequireAuthorization();

app.MapGet("/api/funding/{id:guid}", async (Guid id, FundingDbContext db) =>
{
    var record = await db.FundingRecords.FindAsync(id);
    return record is not null ? Results.Ok(record) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/funding/by-application/{loanApplicationId:guid}", async (Guid loanApplicationId, FundingDbContext db) =>
{
    var record = await db.FundingRecords
        .AsNoTracking()
        .FirstOrDefaultAsync(f => f.LoanApplicationId == loanApplicationId);
    return record is not null ? Results.Ok(record) : Results.NotFound();
}).RequireAuthorization();

app.Run();
