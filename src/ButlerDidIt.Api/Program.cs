using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Api.Kit;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Scale;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

// `--migrate` is our own flag (see "startup tasks" below). Take it out before ASP.NET reads the
// arguments as settings, which would otherwise treat the next argument as its value.
var migrateOnly = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
var config = builder.Configuration;

// ---------------------------------------------------------------- options
builder.Services.Configure<ContentOptions>(config.GetSection("Content"));
builder.Services.Configure<AuthOptions>(config.GetSection("Auth"));
builder.Services.Configure<EmailOptions>(config.GetSection("Email"));
builder.Services.Configure<AppOptions>(config.GetSection("App"));
builder.Services.Configure<AiOptions>(config.GetSection("Ai"));
builder.Services.Configure<MediaOptions>(config.GetSection("Media"));
builder.Services.Configure<RetentionOptions>(config.GetSection("Retention"));
builder.Services.Configure<ScaleOptions>(config.GetSection("Scale"));
var scale = config.GetSection("Scale").Get<ScaleOptions>() ?? new ScaleOptions();

// ---------------------------------------------------------------- database
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Default to your PostgreSQL connection string.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

// Locks shared by every server (Postgres advisory locks). They only do something with
// Scale:MultiInstance on; one server uses in-process locks, exactly as before.
builder.Services.AddSingleton(_ => new ClusterLock(scale.MultiInstance ? NpgsqlDataSource.Create(connectionString) : null));

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

// Keys that encrypt the auth cookie (and the AI API keys). If they only lived in memory,
// every redeploy would log every host out. In Docker, point KeysPath at a mounted volume.
// With several servers, every server must use the same keys, so keep them in the database
// (DataProtection:Store=Database); see DataProtectionKeyImport for moving existing keys there.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ButlerDidIt");
if (string.Equals(config["DataProtection:Store"], "Database", StringComparison.OrdinalIgnoreCase))
{
    dataProtection.PersistKeysToDbContext<AppDbContext>();
}
else if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

// ---------------------------------------------------------------- rate limiting
// Joins per minute per address: enough for a whole party on one Wi-Fi, too few to guess party codes.
// Configurable (RateLimits:JoinPerMinute) so the end-to-end tests, which seat dozens of guests from
// one machine within a minute, can raise it. Real servers keep the default.
var joinsPerMinute = builder.Configuration.GetValue("RateLimits:JoinPerMinute", 20);
// Sign-ups per hour per address. Registering can send a confirmation email, so without a limit an
// open server could be used to spam inboxes. Ten an hour is plenty for a household of hosts.
var registrationsPerHour = builder.Configuration.GetValue("RateLimits:RegisterPerHour", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(PartyEndpoints.JoinRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = joinsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AuthEndpoints.EmailRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
    o.AddPolicy(AuthEndpoints.RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = registrationsPerHour, Window = TimeSpan.FromHours(1) }));
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
// Each kind of game plugs in as a module; the platform finds it by the party's GameKind.
builder.Services.AddSingleton<IGameModule, MysteryModule>();
builder.Services.AddSingleton<EscapeCatalog>();
builder.Services.AddSingleton<IGameModule, EscapeModule>();
builder.Services.AddScoped<EscapeService>();
builder.Services.AddSingleton<GameModules>();
builder.Services.AddScoped<PartyRuntime>();
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
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddHostedService<VerdictWorker>();
builder.Services.AddSingleton<GenerationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GenerationWorker>());

// ---------------------------------------------------------------- media (voices, pictures, selfies, printables)
builder.Services.AddSingleton<IMediaClientFactory>(sp =>
    new MediaClientFactory(allowFake: sp.GetRequiredService<IOptions<AiOptions>>().Value.AllowFakeProvider));
builder.Services.AddScoped<MediaGateway>();
// Local folder by default; S3-compatible storage (Media:Storage=S3) when several servers must share the files.
if (string.Equals(config["Media:Storage"], "S3", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IMediaStore, S3MediaStore>();
else
    builder.Services.AddSingleton<IMediaStore, LocalMediaStore>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddSingleton<MediaWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaWorker>());

var signalR = builder.Services
    .AddSignalR(o => o.AddFilter<GameRuleHubFilter>())
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.PropertyNamingPolicy = GameJson.Options.PropertyNamingPolicy;
        o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });
// SignalR groups live inside one process. With several servers, Redis relays every message to
// all of them, so the TV on one server hears about a guest's move made on another.
if (scale.Redis is { Length: > 0 } redis)
{
    signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("butler"));
}

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddProblemDetails();

// QuestPDF is free under its Community licence for individuals, open-source
// projects and companies under $1M revenue; this line records that choice.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();

// ---------------------------------------------------------------- startup tasks
// Apply database migrations and load content before accepting traffic.
//   * `dotnet ButlerDidIt.Api.dll --migrate` does only this and exits: a deploy step that runs
//     before the new servers start (set Database__MigrateOnStartup=false on the servers then).
//   * With several servers starting together, the startup lock makes them take turns, so two
//     never seed the same themes at once. The second finds nothing left to do.
if (migrateOnly || !app.Configuration.GetValue<bool>("SkipStartupTasks"))
{
    await using (await app.Services.GetRequiredService<ClusterLock>().AcquireAsync("startup", CancellationToken.None))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (migrateOnly || app.Configuration.GetValue("Database:MigrateOnStartup", true)) await db.Database.MigrateAsync();
        await DataProtectionKeyImport.RunAsync(app.Configuration, db, app.Logger);
        var contentRoot = Path.Combine(app.Environment.ContentRootPath, app.Services.GetRequiredService<IOptions<ContentOptions>>().Value.Root);
        await app.Services.GetRequiredService<ContentCatalog>().SeedAsync(contentRoot);
        app.Logger.LogInformation("Loaded {Count} escape rooms", app.Services.GetRequiredService<EscapeCatalog>().Rooms.Count); // fails fast if one is broken
        await AiConfigSeeder.SeedAsync(app.Services);
    }
    if (migrateOnly)
    {
        app.Logger.LogInformation("Database migrated and content loaded; exiting because of --migrate.");
        return;
    }
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
app.MapEscapeEndpoints(app.Configuration);
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
