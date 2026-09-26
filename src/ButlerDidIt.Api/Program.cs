using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Api.Kit;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- options
builder.Services.Configure<ContentOptions>(config.GetSection("Content"));
builder.Services.Configure<AuthOptions>(config.GetSection("Auth"));
builder.Services.Configure<EmailOptions>(config.GetSection("Email"));
builder.Services.Configure<AppOptions>(config.GetSection("App"));
builder.Services.Configure<AiOptions>(config.GetSection("Ai"));
builder.Services.Configure<MediaOptions>(config.GetSection("Media"));
builder.Services.Configure<RetentionOptions>(config.GetSection("Retention"));

// ---------------------------------------------------------------- database
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Set ConnectionStrings__Default to your PostgreSQL connection string.")));

// ---------------------------------------------------------------- authentication
// Two ways to prove who you are:
//   * Hosts sign in with email/password and get a cookie (ASP.NET Core Identity).
//   * Guests get a seat token when they join a party (SeatTokenHandler).
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, SeatTokenHandler>(SeatTokens.Scheme, null);

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "butler.auth";
    o.Cookie.HttpOnly = true; // JavaScript can't read it, so XSS can't steal it
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // This is an API: answer 401/403 instead of redirecting to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});

builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    // Password-reset and email-confirmation tokens. They're signed with the Data
    // Protection keys and include the user's security stamp, so a reset link stops
    // working once it has been used (using it changes the stamp).
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AccountTokens.Lifespan);
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Host, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser())
    .AddPolicy(AuthPolicies.Seat, p => p.AddAuthenticationSchemes(SeatTokens.Scheme).RequireAuthenticatedUser())
    .AddPolicy(AuthPolicies.PartyMember, p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, SeatTokens.Scheme)
        .RequireAuthenticatedUser());

// Keys that encrypt the auth cookie. If they only lived in memory, every redeploy
// would log every host out. In Docker, point this at a mounted volume.
if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("ButlerDidIt");
}
else
{
    builder.Services.AddDataProtection().SetApplicationName("ButlerDidIt");
}

// ---------------------------------------------------------------- rate limiting
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(PartyEndpoints.JoinRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AuthEndpoints.EmailRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
});

// ---------------------------------------------------------------- JSON
// Reuse the game's JSON rules everywhere (camelCase, enums as strings) so the
// browser sees the same shapes over HTTP and over SignalR.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

// ---------------------------------------------------------------- game services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ContentCatalog>();
builder.Services.AddSingleton<PartyLocks>();
builder.Services.AddScoped<PartyService>();
builder.Services.AddScoped<PartyDealer>();
builder.Services.AddHostedService<PartyTicker>();
builder.Services.AddSingleton<RetentionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionWorker>());

// ---------------------------------------------------------------- AI (see docs/ai-setup.md)
// Every AI call goes through AiGateway, which picks the provider for the role
// (Claude, ChatGPT, Gemini or Ollama), enforces the budget and logs usage.
builder.Services.AddSingleton<AiKeyProtector>();
builder.Services.AddSingleton<IChatClientFactory>(sp =>
    new ChatClientFactory(allowFake: sp.GetRequiredService<IOptions<AiOptions>>().Value.AllowFakeProvider));
builder.Services.AddScoped<IAiSettingsSource, DbAiSettingsSource>();
builder.Services.AddSingleton<IAiUsageSink, DbAiUsageSink>();
builder.Services.AddScoped<IAiBudget, DbAiBudget>();
builder.Services.AddScoped<AiGateway>();
builder.Services.AddScoped<MysteryGenerator>();
builder.Services.AddScoped<VersionRemixer>();
builder.Services.AddScoped<AiGameService>();
builder.Services.AddSingleton<VerdictQueue>();
builder.Services.AddHostedService<VerdictWorker>();
builder.Services.AddSingleton<GenerationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GenerationWorker>());

// ---------------------------------------------------------------- media (voices, pictures, selfies, printables)
builder.Services.AddSingleton<IMediaClientFactory>(sp =>
    new MediaClientFactory(allowFake: sp.GetRequiredService<IOptions<AiOptions>>().Value.AllowFakeProvider));
builder.Services.AddScoped<MediaGateway>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddSingleton<MediaWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaWorker>());

builder.Services
    .AddSignalR(o => o.AddFilter<GameRuleHubFilter>())
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.PropertyNamingPolicy = GameJson.Options.PropertyNamingPolicy;
        o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddProblemDetails();

// QuestPDF is free under its Community licence for individuals, open-source
// projects and companies under $1M revenue; this line records that choice.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();

// ---------------------------------------------------------------- startup tasks
// Apply database migrations and load content before accepting traffic. Fine for
// a single instance; with several instances you'd run migrations as a deploy step.
if (!app.Configuration.GetValue<bool>("SkipStartupTasks"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    var contentRoot = Path.Combine(app.Environment.ContentRootPath, app.Services.GetRequiredService<IOptions<ContentOptions>>().Value.Root);
    await app.Services.GetRequiredService<ContentCatalog>().SeedAsync(contentRoot);
    await AiConfigSeeder.SeedAsync(app.Services);
}

// ---------------------------------------------------------------- pipeline
// Game rule violations become 400s with a friendly message instead of 500s.
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = error switch
    {
        GameRuleException e => (StatusCodes.Status400BadRequest, e.Message),
        AiException e => (StatusCodes.Status400BadRequest, e.Message),
        KeyNotFoundException e => (StatusCodes.Status404NotFound, e.Message),
        _ => (StatusCodes.Status500InternalServerError, "Something went wrong."),
    };
    ctx.Response.StatusCode = status;
    await Results.Problem(message, statusCode: status).ExecuteAsync(ctx);
}));

app.UseDefaultFiles();
app.UseStaticFiles(); // the built React app in wwwroot
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapAuthEndpoints();
app.MapAdminHostEndpoints();
app.MapRecapEndpoints();
app.MapScenarioEditorEndpoints();
app.MapThemeEndpoints();
app.MapPartyEndpoints();
app.MapMediaEndpoints();
app.MapAiEndpoints();
app.MapGenerationEndpoints();
app.MapMediaApi();
app.MapKitEndpoints();
app.MapHub<PartyHub>("/hubs/party");

// Any other URL (/join/ABC123, /stage/ABC123…) is a page in the React app.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so integration tests can start the app with WebApplicationFactory.</summary>
public partial class Program;
