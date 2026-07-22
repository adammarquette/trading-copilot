using System.Text;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.TradingCopilot.Api;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
JwtOptions jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
{
    throw new InvalidOperationException(
        $"Configure '{JwtOptions.SectionName}:SigningKey' via env / user-secrets — it must never live in source.");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddTradingCopilotData(builder.Configuration.GetConnectionString("Default") ?? string.Empty);

// The ProjectX client (credentials from the "ProjectX" section -- env, never source). One credential set per
// process: the client's websocket is a singleton (ADR-0015); ProjectX:CredentialKey names whose it is, and the
// discovery endpoint refuses a connection whose key this process does not hold.
builder.Services.AddProjectXApiClient(builder.Configuration);
builder.Services.Configure<ProjectXConnectionOptions>(
    builder.Configuration.GetSection(ProjectXConnectionOptions.SectionName));
builder.Services.AddScoped<IProjectXVenueFactory, ProjectXVenueFactory>();

// The event backbone (ADR-0001) behind its seam: today a Timescale hypertable; a future bus is an adapter
// change, not a rewrite. Producers/consumers arrive with market-data ingestion (R-1).
builder.Services.AddScoped<IEventLog, TimescaleEventLog>();

// The R-14 environment, mapped ONCE at the composition root from the host (gh#9): practice anywhere, live only
// in production, undeclared nowhere -- and an unrecognised environment name fails closed to Development
// (practice-only). Wrapped so endpoints bind it from services, never from a request.
builder.Services.AddSingleton(new HostTradingEnvironment(
    DeploymentEnvironmentMapping.From(builder.Environment.EnvironmentName)));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();

WebApplication app = builder.Build();

await StartupTasks.MigrateAndBootstrapAsync(app);

app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapFirmEndpoints();
app.MapConnectionEndpoints();
app.MapAccountEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

/// <summary>The WebApplication entry point for testing integration.</summary>
public partial class Program { }
