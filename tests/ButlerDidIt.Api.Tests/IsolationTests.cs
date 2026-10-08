using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Billing;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The server for <see cref="IsolationTests"/>. Its first account is the admin, so hosts A and B are ordinary hosts,
/// and it sells a plan through the fake payment provider, so payments can be tried too.
/// </summary>
public sealed class IsolationFactory : ApiFactory
{
    /// <summary>Host A's things and host B, made once by the first test that asks (see IsolationTests.WorldAsync).</summary>
    internal IsolationTests.World? World { get; set; }

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("Billing:Provider", "Fake"),
        ("Billing:Prices:BothMonthly", "fake_both_month_1200"),
    ];
}

/// <summary>
/// Host isolation (#103), in one place: host B, an ordinary signed-in host, tries everything against host A and gets
/// nothing. A has a murder mystery party and an escape room party with a guest at each, their own copy of a mystery
/// and of a room (with an uploaded cover), a story the AI wrote for them, some AI spending and a subscription.
///
/// The first two tests read the server's own list of routes, so a new endpoint is covered the day it's added: it either
/// needs a sign-in, or it's on <see cref="PublicRoutes"/> on purpose, and every admin route refuses an ordinary host.
/// The hub test finds every host control by reflection in the same spirit.
///
/// Many of these checks also live next to their feature (PartyFlowTests, EscapeEditorTests, MysteryMediaTests,
/// BillingTests…); this class is the one place that answers "can another host get at my things?".
/// </summary>
public class IsolationTests(IsolationFactory app) : IClassFixture<IsolationFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";
    private const string Funhouse = "the-funhouse";

    /// <summary>One signed-in host: their client (which sends their cookie), the cookie for hub connections, and their id.</summary>
    public sealed record Host(HttpClient Client, string Cookie, string Id, string Email);

    /// <summary>Everything host A owns.</summary>
    public sealed record World(Host A, Host B, string MysteryParty, string EscapeParty, SeatResponse Guest, SeatResponse EscapeGuest,
        string Mystery, string Room, Guid Job, string CheckoutId, string EscapeOfB, SeatResponse GuestOfB);

    /// <summary>
    /// Routes anyone may call, without signing in, and why. Anything else must need a sign-in (a host's cookie or a
    /// guest's seat token): a new public route has to be added here on purpose.
    /// </summary>
    private static readonly Dictionary<string, string> PublicRoutes = new()
    {
        ["* /healthz"] = "uptime checks",
        ["GET /api/auth/options"] = "the sign-in page: is sign-up open?",
        ["POST /api/auth/register"] = "signing up",
        ["POST /api/auth/login"] = "signing in",
        ["POST /api/auth/logout"] = "signing out",
        ["POST /api/auth/forgot"] = "asking for a reset link",
        ["POST /api/auth/reset"] = "using a reset link",
        ["POST /api/auth/confirm"] = "using a confirmation link",
        ["POST /api/auth/invite"] = "checking an invite link before signing up",
        ["POST /api/account/email/confirm"] = "using an email-change link (the token is the proof)",
        ["GET /api/legal/{page}"] = "the terms, privacy and refund pages",
        ["GET /api/themes"] = "the mystery shelf: only what a card shows",
        ["GET /api/escape-rooms"] = "the escape room shelf: only what a card shows",
        ["GET /api/escape-rooms/{id}/leaderboard"] = "times only; a host's own room only for them or with a party's code",
        ["GET /api/parties/{code}"] = "the join page (the code is the invitation)",
        ["POST /api/parties/{code}/join"] = "a guest taking a seat (rate-limited)",
        ["POST /api/parties/{code}/watch"] = "watching the TV, when the host allows it",
        ["GET /api/recap/{slug}"] = "a recap the host shared (random link)",
        ["GET /api/escape-recap/{slug}"] = "an escape recap the host shared (random link)",
        ["GET /escape/recap/{slug}"] = "the shared escape recap's link preview",
        ["GET /media/assets/{id:guid}"] = "generated and uploaded files (random ids)",
        ["GET /media/themes/{slug}/{**path}"] = "the themes' own pictures and sounds",
        ["POST /api/billing/webhook"] = "Stripe, proven by the signature",
        ["GET /api/billing/fake/checkout/{id}"] = "the fake payment provider's pages: tests only, refused in Production",
        ["POST /api/billing/fake/checkout/{id}/pay"] = "the fake payment provider's pages: tests only, refused in Production",
        ["GET /api/billing/fake/portal/{customer}"] = "the fake payment provider's pages: tests only, refused in Production",
        ["POST /api/billing/fake/portal/{customer}/cancel/{subscription}"] = "the fake payment provider's pages: tests only, refused in Production",
        ["GET /{*path:nonfile}"] = "the web app's pages",
        ["HEAD /{*path:nonfile}"] = "the web app's pages",
    };

    // ---------------------------------------------------------------- set-up

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {res.RequestMessage?.RequestUri}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static void Refused(HttpStatusCode expected, HttpResponseMessage res, string what) =>
        Assert.True(res.StatusCode == expected, $"{what}: expected {(int)expected}, got {(int)res.StatusCode}");

    private async Task<Host> SignUpAsync(string name)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        var (client, cookie) = await app.RegisterHostAsync(email);
        var me = await Read<MeResponse>(await client.GetAsync("/api/auth/me"));
        Assert.False(me.IsAdmin);
        return new Host(client, cookie, me.Id, email);
    }

    private async Task<World> WorldAsync()
    {
        // Tests in one class run one at a time, so the world is made once, by the first test that asks.
        if (app.World is { } made) return made;
        // The first account is the admin. (SignUpAsync checks A and B aren't.)
        await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner@example.com", "password123", "Owner"));

        var a = await SignUpAsync("alice");
        // A mystery the AI wrote for A, saved as the writer saves one: a copy of Blackwood Manor's story under a new id.
        var mystery = $"alices-mystery-{Guid.NewGuid():N}"[..24];
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var original = await db.Scenarios.AsNoTracking().SingleAsync(s => s.Id == Blackwood);
            var document = JsonNode.Parse(original.Document)!;
            document["id"] = mystery;
            document["title"] = "Alice's Mystery";
            db.Scenarios.Add(new ScenarioEntity
            {
                Id = mystery, ThemeSlug = original.ThemeSlug, Title = "Alice's Mystery", MinPlayers = original.MinPlayers, MaxPlayers = original.MaxPlayers,
                ContentRating = original.ContentRating, Source = ScenarioSource.AiGenerated, OwnerUserId = a.Id, Document = document.ToJsonString(),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var room = (await Read<Dictionary<string, string>>(await a.Client.PostAsync($"/api/escape-rooms/{Funhouse}/duplicate", null)))["id"];
        await Read<RoomMediaView>(await RoomMedia.UploadAsync(a.Client, room, EscapeArt.Cover, RoomMedia.Png(64, 48), "image/png"));

        var mysteryParty = await Read<PartyInfo>(await a.Client.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(mystery, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        var escapeParty = await Read<PartyInfo>(await a.Client.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(room, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        var guest = await JoinAsync(mysteryParty.Code, "Gus");
        var escapeGuest = await JoinAsync(escapeParty.Code, "Esme");

        // A story the AI wrote for A, and what A's AI has cost this month.
        var job = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.GenerationJobs.Add(new GenerationJobEntity
            {
                Id = job, HostUserId = a.Id, ThemeSlug = "the-butler-did-it", Request = "{}", Status = GenerationStatus.Failed,
                Progress = "Stopped", Error = "A's own story", CreatedAt = now, UpdatedAt = now,
            });
            db.AiUsage.Add(new AiUsageEntity
            {
                At = now, Role = ButlerDidIt.Ai.AiRole.Storyteller, ProviderName = "Fake", Model = "fake-model", CostUsd = 1.25m, PriceKnown = true,
                Success = true, HostUserId = a.Id, Purpose = "story",
            });
            await db.SaveChangesAsync();
        }

        // A subscribes, paying on the (fake) payment page.
        var checkout = (await Read<RedirectView>(await a.Client.PostAsJsonAsync("/api/billing/checkout", new BuyRequest(BillingPlan.BothMonthly), GameJson.Options))).Url;
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await browser.PostAsync(new Uri(checkout).AbsolutePath + "/pay", null);

        // B has a party of their own, with a guest.
        var b = await SignUpAsync("bob");
        var escapeOfB = await Read<PartyInfo>(await b.Client.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(Funhouse, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        var guestOfB = await JoinAsync(escapeOfB.Code, "Bea");

        return app.World = new World(a, b, mysteryParty.Code, escapeParty.Code, guest, escapeGuest, mystery, room, job,
            checkout[(checkout.LastIndexOf('/') + 1)..], escapeOfB.Code, guestOfB);
    }

    private async Task<SeatResponse> JoinAsync(string code, string name) =>
        await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest(name)));

    // ---------------------------------------------------------------- every route

    /// <summary>A route as "GET /api/parties/{code}", with what it needs to be called.</summary>
    private sealed record Route(string Method, string Pattern, IReadOnlyList<string> Policies)
    {
        public string Name => $"{Method} {Pattern}";

        /// <summary>A path that reaches this route: each {value} filled with something that fits its constraint.</summary>
        public string Path() => Regex.Replace(Pattern, @"\{\*{0,2}\w+(?::(\w+))?\}", m => m.Groups[1].Value switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "int" or "long" => "1",
            _ => "x",
        });
    }

    private List<Route> Routes() =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "(default)").ToList();
                if (e.Metadata.GetMetadata<IAllowAnonymous>() is not null) policies.Clear();
                var pattern = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/').TrimEnd('/');
                var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => new Route(m, pattern, policies));
            })
            .DistinctBy(r => r.Name)
            .ToList();

    [Fact]
    public async Task Every_route_is_public_on_purpose_or_needs_a_sign_in()
    {
        var routes = Routes();
        var open = routes.Where(r => r.Policies.Count == 0).Select(r => r.Name).ToHashSet();
        var unexpected = open.Except(PublicRoutes.Keys).Order().ToList();
        Assert.True(unexpected.Count == 0, "These routes need no sign-in. Protect them, or add them to PublicRoutes with the reason:\n" + string.Join("\n", unexpected));
        var gone = PublicRoutes.Keys.Except(open).Order().ToList();
        Assert.True(gone.Count == 0, "These public routes no longer exist (or now need a sign-in); take them off PublicRoutes:\n" + string.Join("\n", gone));

        // Nobody signed in gets anywhere with the others, whatever the path holds.
        var stranger = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var protectedRoutes = routes.Where(r => r.Policies.Count > 0 && !r.Pattern.StartsWith("/hubs/")).ToList();
        Assert.True(protectedRoutes.Count > 60, $"Only {protectedRoutes.Count} protected routes found");
        foreach (var route in protectedRoutes)
        {
            using var req = new HttpRequestMessage(new HttpMethod(route.Method), route.Path());
            if (route.Method is "POST" or "PUT") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Refused(HttpStatusCode.Unauthorized, await stranger.SendAsync(req), route.Name);
        }
    }

    [Fact]
    public async Task Every_admin_route_refuses_an_ordinary_host()
    {
        var world = await WorldAsync();
        var admin = Routes().Where(r => r.Pattern.StartsWith("/api/admin/") || r.Pattern == "/api/admin").ToList();
        Assert.True(admin.Count >= 25, $"Only {admin.Count} admin routes found");
        foreach (var route in admin)
        {
            Assert.Contains(AuthPolicies.Host, route.Policies);
            using var req = new HttpRequestMessage(new HttpMethod(route.Method), route.Path());
            if (route.Method is "POST" or "PUT") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Refused(HttpStatusCode.Forbidden, await world.B.Client.SendAsync(req), route.Name);
        }

        // With real ids too: B can't give themselves free access, or check A's payments.
        Refused(HttpStatusCode.Forbidden, await world.B.Client.PostAsync($"/api/admin/hosts/{world.B.Id}/free-access", null), "free access for themselves");
        Refused(HttpStatusCode.Forbidden, await world.B.Client.PostAsync($"/api/admin/billing/hosts/{world.A.Id}/sync", null), "sync A's payments");
    }

    // ---------------------------------------------------------------- parties

    [Fact]
    public async Task Host_B_cannot_open_change_or_delete_host_As_parties()
    {
        var w = await WorldAsync();
        var b = w.B.Client;

        // B's list is B's alone.
        var mine = await Read<List<PartyInfo>>(await b.GetAsync("/api/parties"));
        Assert.DoesNotContain(mine, p => p.Code == w.MysteryParty || p.Code == w.EscapeParty);

        foreach (var code in new[] { w.MysteryParty, w.EscapeParty })
        {
            // The join page is public, but it doesn't treat B as the host.
            var info = await Read<PartyInfo>(await b.GetAsync($"/api/parties/{code}"));
            Assert.False(info.IsHost);

            Refused(HttpStatusCode.Forbidden, await b.DeleteAsync($"/api/parties/{code}"), "delete A's party");
            Refused(HttpStatusCode.Forbidden, await b.PostAsJsonAsync($"/api/parties/{code}/seats", new AddSeatRequest("Mallory", IsLocal: true)), "add a seat");
            Refused(HttpStatusCode.Forbidden, await b.PostAsJsonAsync($"/api/parties/{code}/seats", new AddSeatRequest("Mallory", IsLocal: false)), "join as host");
            foreach (var kind in ButlerDidIt.Api.Kit.PartyKit.Kinds)
                Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/parties/{code}/kit/{kind}.pdf"), $"print A's {kind}");
            Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/parties/{code}/media"), "A's pictures and voices");
            Refused(HttpStatusCode.NotFound, await b.PostAsync($"/api/parties/{code}/media", null), "make pictures on A's budget");
            Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/parties/{code}/recap"), "A's recap");
            Refused(HttpStatusCode.NotFound, await b.PostAsync($"/api/parties/{code}/recap/share", null), "share A's recap");
            Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/parties/{code}/recap/share"), "unshare A's recap");
            Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/parties/{code}/escape-recap"), "A's escape recap");
            Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/parties/{code}/spectators"), "who's watching A's party");
            Refused(HttpStatusCode.NotFound, await b.PutAsJsonAsync($"/api/parties/{code}/spectators", new AllowSpectatorsRequest(true)), "open A's party to watchers");
            Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/parties/{code}/spectators/{Guid.NewGuid()}"), "send A's watchers away");
        }

        // A's leaderboard line for this party isn't B's to see.
        var board = await Read<Leaderboard>(await b.GetAsync($"/api/escape-rooms/{w.Room}/leaderboard?party={w.EscapeParty}"));
        Assert.Null(board.ThisParty);

        // And nothing changed: both parties are A's, waiting, with their guests.
        var list = await Read<List<PartyInfo>>(await w.A.Client.GetAsync("/api/parties"));
        Assert.All(new[] { w.MysteryParty, w.EscapeParty }, code => Assert.Contains(list, p => p.Code == code && p.Status == PartyStatus.Lobby && p.PlayerCount == 1));
    }

    /// <summary>Every hub method that takes a party code is a host control. Found by reflection, so a new one is covered too.</summary>
    private static List<MethodInfo> HostControls() =>
        typeof(PartyHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters() is [{ Name: "code" } first, ..] && first.ParameterType == typeof(string) && m.Name != nameof(PartyHub.WatchParty))
            .ToList();

    private static object?[] Arguments(MethodInfo method, string code) =>
        method.GetParameters().Select((p, i) => i == 0 ? code : p.ParameterType == typeof(Guid) ? Guid.NewGuid() : p.ParameterType == typeof(int) ? 5 : (object)"x").ToArray();

    private static async Task<string> RefusedAsync(HubConnection hub, string method, params object?[] args)
    {
        var ex = await Assert.ThrowsAsync<HubException>(() => hub.InvokeCoreAsync(method, typeof(object), args));
        return ex.Message;
    }

    [Fact]
    public async Task Host_B_cannot_watch_or_run_host_As_parties_on_the_live_connection()
    {
        var w = await WorldAsync();
        var controls = HostControls();
        Assert.True(controls.Count >= 16, $"Only {controls.Count} host controls found: {string.Join(", ", controls.Select(m => m.Name))}");

        await using var b = await app.ConnectAsync(cookie: w.B.Cookie);
        foreach (var code in new[] { w.MysteryParty, w.EscapeParty })
        {
            Assert.Contains("You are not part of this party.", await RefusedAsync(b, nameof(PartyHub.WatchParty), code));
            foreach (var control in controls)
                Assert.Contains("Only the host can do that.", await RefusedAsync(b, control.Name, Arguments(control, code)));
        }
        // B's own "jobs" signal is about B's jobs only: it takes no one else's id.
        Assert.Empty(typeof(PartyHub).GetMethod(nameof(PartyHub.WatchMyJobs))!.GetParameters());

        // Neither game moved, and A's guests are still seated.
        await using var a = await app.ConnectAsync(cookie: w.A.Cookie);
        var stage = await a.InvokeAsync<StageView>(nameof(PartyHub.WatchParty), w.MysteryParty);
        Assert.Equal(Phase.Lobby, stage.Phase);
        Assert.Equal(new[] { "Gus" }, stage.Players.Select(p => p.Name));
        var escape = await a.InvokeAsync<EscapeStageView>(nameof(PartyHub.WatchParty), w.EscapeParty);
        Assert.Equal(EscapePhase.Lobby, escape.Phase);
        Assert.Equal(new[] { "Esme" }, escape.Players.Select(p => p.Name));
    }

    [Fact]
    public async Task A_host_can_only_remove_seats_at_their_own_party()
    {
        var w = await WorldAsync();

        // B's guest is at B's escape room, listening for messages to their seat.
        await using var bea = await app.ConnectAsync(w.GuestOfB.Token);
        var heard = new List<string>();
        bea.On("removed", () => { lock (heard) heard.Add("removed"); });
        bea.On<JsonElement>("player", _ => { lock (heard) heard.Add("player"); });
        await bea.InvokeAsync<JsonElement>(nameof(PartyHub.JoinSeat));

        // A names B's guest at each of A's parties. An escape room would quietly ignore a player it doesn't have, so the
        // hub checks the seat is at the party before telling the phone it was removed.
        await using var a = await app.ConnectAsync(cookie: w.A.Cookie);
        foreach (var code in new[] { w.MysteryParty, w.EscapeParty })
            Assert.Contains("That seat isn't at this party.", await RefusedAsync(a, nameof(PartyHub.RemoveSeat), code, w.GuestOfB.SeatId));

        // Messages to one phone arrive in order: once the next update reaches Bea, anything sent before it has too.
        await JoinAsync(w.EscapeOfB, "Ben");
        for (var i = 0; i < 50 && !heard.Contains("player"); i++) await Task.Delay(100);
        lock (heard) Assert.Equal(new[] { "player" }, heard.Distinct());

        // Bea is still seated, and her token still works.
        await using var b = await app.ConnectAsync(cookie: w.B.Cookie);
        var stage = await b.InvokeAsync<EscapeStageView>(nameof(PartyHub.WatchParty), w.EscapeOfB);
        Assert.Contains(stage.Players, p => p.Name == "Bea");
        await using var again = await app.ConnectAsync(w.GuestOfB.Token);
        await again.InvokeAsync<JsonElement>(nameof(PartyHub.JoinSeat));
    }

    [Fact]
    public async Task A_guests_seat_token_works_only_at_their_own_party_and_never_as_a_host()
    {
        var w = await WorldAsync();

        // On the live connection: their own party, and nothing else.
        await using var gus = await app.ConnectAsync(w.Guest.Token);
        await gus.InvokeAsync<StageView>(nameof(PartyHub.WatchParty), w.MysteryParty);
        foreach (var code in new[] { w.EscapeParty, w.EscapeOfB })
            Assert.Contains("You are not part of this party.", await RefusedAsync(gus, nameof(PartyHub.WatchParty), code));
        foreach (var control in HostControls())
            Assert.Contains("Only the host can do that.", await RefusedAsync(gus, control.Name, Arguments(control, w.MysteryParty)));

        // Over HTTP, a seat token is never a host's sign-in.
        var phone = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        phone.DefaultRequestHeaders.Add(SeatTokens.HeaderName, w.Guest.Token);
        Refused(HttpStatusCode.Unauthorized, await phone.GetAsync("/api/parties"), "list parties with a seat token");
        Refused(HttpStatusCode.Unauthorized, await phone.GetAsync($"/api/parties/{w.MysteryParty}/kit/booklets.pdf"), "print booklets with a seat token");
        Refused(HttpStatusCode.Unauthorized, await phone.DeleteAsync($"/api/parties/{w.MysteryParty}"), "delete the party with a seat token");

        // Someone watching one party can't watch another, or act as a guest.
        Assert.True((await w.A.Client.PutAsJsonAsync($"/api/parties/{w.EscapeParty}/spectators", new AllowSpectatorsRequest(true))).IsSuccessStatusCode);
        var watcher = await Read<WatchResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{w.EscapeParty}/watch", new WatchRequest("Wendy")));
        await using var wendy = await app.ConnectAsync(watcher.Token);
        await wendy.InvokeAsync<JsonElement>(nameof(PartyHub.WatchParty), w.EscapeParty);
        Assert.Contains("You are not part of this party.", await RefusedAsync(wendy, nameof(PartyHub.WatchParty), w.EscapeOfB));
        Assert.Contains("Join the party first.", await RefusedAsync(wendy, nameof(PartyHub.JoinSeat)));
        Assert.Contains("Only the host can do that.", await RefusedAsync(wendy, nameof(PartyHub.EscapeStart), w.EscapeParty));
        Assert.True((await w.A.Client.PutAsJsonAsync($"/api/parties/{w.EscapeParty}/spectators", new AllowSpectatorsRequest(false))).IsSuccessStatusCode);
    }

    // ---------------------------------------------------------------- mysteries and escape rooms

    [Fact]
    public async Task Host_B_cannot_read_change_copy_or_play_host_As_mystery()
    {
        var w = await WorldAsync();
        var (b, id) = (w.B.Client, w.Mystery);

        Assert.DoesNotContain(id, await b.GetStringAsync("/api/scenarios/mine"));
        Assert.DoesNotContain(id, await b.GetStringAsync("/api/themes"));
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/scenarios/{id}"), "read A's mystery");
        Refused(HttpStatusCode.NotFound, await b.PutAsJsonAsync($"/api/scenarios/{id}", new { document = new { id } }), "save over A's mystery");
        Refused(HttpStatusCode.NotFound, await b.PostAsync($"/api/scenarios/{id}/duplicate", null), "copy A's mystery");
        Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/scenarios/{id}"), "delete A's mystery");
        Refused(HttpStatusCode.Forbidden, await b.PutAsJsonAsync($"/api/scenarios/{id}/sharing", new SharingRequest(true, null)), "share A's mystery");
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/scenarios/{id}/media"), "A's mystery's media");
        Refused(HttpStatusCode.NotFound, await b.PostAsync($"/api/scenarios/{id}/media/{MediaOverlay.Music}", new ByteArrayContent(RoomMedia.Mp4())), "upload to A's mystery");
        Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/scenarios/{id}/media/{MediaOverlay.Music}"), "remove A's mystery's music");
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/insights/mystery/{id}"), "A's mystery's insights");
        Refused(HttpStatusCode.BadRequest, await b.PostAsJsonAsync("/api/parties", new CreatePartyRequest(id, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options),
            "play A's mystery");

        // Still A's, untouched.
        Assert.True((await w.A.Client.GetAsync($"/api/scenarios/{id}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Host_B_cannot_read_change_copy_or_play_host_As_escape_room()
    {
        var w = await WorldAsync();
        var (b, id) = (w.B.Client, w.Room);

        Assert.DoesNotContain(id, await b.GetStringAsync("/api/escape-rooms"));
        Assert.DoesNotContain(id, await b.GetStringAsync("/api/escape-rooms/library"));
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/escape-rooms/{id}/document"), "read A's room (answers and all)");
        Refused(HttpStatusCode.NotFound, await b.PutAsJsonAsync($"/api/escape-rooms/{id}/document", new { document = new { id } }), "save over A's room");
        Refused(HttpStatusCode.NotFound, await b.PostAsync($"/api/escape-rooms/{id}/duplicate", null), "copy A's room");
        Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/escape-rooms/{id}"), "delete A's room");
        Refused(HttpStatusCode.Forbidden, await b.PutAsJsonAsync($"/api/escape-rooms/{id}/sharing", new SharingRequest(true, null)), "share A's room");
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/escape-rooms/{id}/media"), "A's room's media");
        Refused(HttpStatusCode.NotFound, await RoomMedia.UploadAsync(b, id, EscapeArt.Cover, RoomMedia.Png(64, 48), "image/png"), "replace A's cover");
        Refused(HttpStatusCode.NotFound, await b.DeleteAsync($"/api/escape-rooms/{id}/media/{EscapeArt.Cover}"), "remove A's cover");
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/insights/escape/{id}"), "A's room's insights");
        Refused(HttpStatusCode.BadRequest, await b.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(id, PartyMode.SharedScreen, UseAi: false), GameJson.Options),
            "play A's room");

        // A private room's leaderboard is A's, and the screens' of a party playing it (they send its code).
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/escape-rooms/{id}/leaderboard"), "A's room's leaderboard");
        Refused(HttpStatusCode.NotFound, await app.CreateClient().GetAsync($"/api/escape-rooms/{id}/leaderboard"), "A's room's leaderboard, signed out");
        Refused(HttpStatusCode.NotFound, await b.GetAsync($"/api/escape-rooms/{id}/leaderboard?party={w.EscapeOfB}"), "with B's party, which plays another room");
        Assert.True((await w.A.Client.GetAsync($"/api/escape-rooms/{id}/leaderboard")).IsSuccessStatusCode);
        Assert.True((await app.CreateClient().GetAsync($"/api/escape-rooms/{id}/leaderboard?party={w.EscapeParty}")).IsSuccessStatusCode);

        // Still A's, cover and all.
        var media = await Read<RoomMediaView>(await w.A.Client.GetAsync($"/api/escape-rooms/{id}/media"));
        Assert.NotNull(RoomMedia.Slot(media, EscapeArt.Cover).Url);
    }

    // ---------------------------------------------------------------- AI, payments and the account

    [Fact]
    public async Task Host_B_cannot_see_host_As_AI_stories_or_spending()
    {
        var w = await WorldAsync();
        Refused(HttpStatusCode.NotFound, await w.B.Client.GetAsync($"/api/generation/{w.Job}"), "A's AI story");
        Assert.True((await w.A.Client.GetAsync($"/api/generation/{w.Job}")).IsSuccessStatusCode);

        Assert.Equal(1.25m, (await Read<ButlerDidIt.Api.Ai.AiStatus>(await w.A.Client.GetAsync("/api/ai/status"))).SpentThisMonthUsd);
        Assert.Equal(0m, (await Read<ButlerDidIt.Api.Ai.AiStatus>(await w.B.Client.GetAsync("/api/ai/status"))).SpentThisMonthUsd);
        Refused(HttpStatusCode.Forbidden, await w.B.Client.GetAsync("/api/admin/ai/usage"), "everyone's AI spending");
    }

    [Fact]
    public async Task Host_B_cannot_see_or_use_host_As_payments_or_account()
    {
        var w = await WorldAsync();
        var b = w.B.Client;

        // A's checkout id gives B nothing, and B's plans page knows nothing of A's subscription.
        var sync = await Read<SyncView>(await b.PostAsJsonAsync("/api/billing/sync", new SyncRequest(w.CheckoutId)));
        Assert.Null(sync.Paid);
        Assert.False((await Read<BillingView>(await b.GetAsync("/api/billing"))).Subscribed);
        Assert.True((await Read<BillingView>(await w.A.Client.GetAsync("/api/billing"))).Subscribed);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.AccessGrants.AnyAsync(g => g.UserId == w.B.Id && g.ExternalId != null));
            Assert.NotNull((await db.Users.SingleAsync(u => u.Id == w.A.Id)).BillingCustomerId);
            Assert.Null((await db.Users.SingleAsync(u => u.Id == w.B.Id)).BillingCustomerId);
        }

        // B's account, preferences and export hold only B's things.
        foreach (var path in new[] { "/api/account", "/api/account/preferences", "/api/account/export", "/api/auth/me", "/api/billing" })
        {
            var json = await b.GetStringAsync(path);
            foreach (var secret in new[] { w.A.Email, w.A.Id, w.MysteryParty, w.EscapeParty, w.Mystery, w.Room, w.CheckoutId })
                Assert.False(json.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{path} shows A's {secret}");
        }
    }
}
