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
using TienFS.Shared.Auth;
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
var sqlConnectionString = builder.Configuration["Sql-Servicing-ConnectionString"]
    ?? throw new InvalidOperationException(
        "Sql-Servicing-ConnectionString must be configured — locally via " +
        "appsettings.Development.json, in production via Key Vault.");
builder.Services.AddDbContext<ServicingDbContext>(o => o.UseSqlServer(sqlConnectionString));

// Servicing only ever consumes events in this sample — it doesn't publish any
// further event, so it doesn't need IEventBus registered at all.
builder.Services.AddHostedService<LoanFundedSubscriber>();

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
    o.SwaggerDoc("v1", new() { Title = "Loan Servicing API", Version = "v1" });
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
    var db = scope.ServiceProvider.GetRequiredService<ServicingDbContext>();
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "servicing" }));

app.MapGet("/api/accounts", async (ServicingDbContext db) =>
    Results.Ok(await db.Accounts.AsNoTracking().ToListAsync())).RequireAuthorization();

app.MapGet("/api/accounts/{id:guid}", async (Guid id, ServicingDbContext db) =>
{
    var account = await db.Accounts.FindAsync(id);
    return account is not null ? Results.Ok(account) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/accounts/by-application/{loanApplicationId:guid}", async (Guid loanApplicationId, ServicingDbContext db) =>
{
    var account = await db.Accounts
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.LoanApplicationId == loanApplicationId);
    return account is not null ? Results.Ok(account) : Results.NotFound();
}).RequireAuthorization();

app.Run();
