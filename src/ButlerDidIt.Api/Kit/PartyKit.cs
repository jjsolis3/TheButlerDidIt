using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ButlerDidIt.Api.Kit;

/// <summary>
/// Printable PDFs for dinner parties, like the contents of a boxed murder
/// mystery kit. Built with QuestPDF (free Community licence for individuals and
/// small businesses).
///
///   invitations  one page per character: who you'll play, what to wear, a QR code to join
///   booklets     each character's private dossier, secrets split by act (contains spoilers!)
///   nametags     cut-out name tags
///   clues        clue cards by act, plus a sealed "solution" page for the host
/// </summary>
public static class PartyKit
{
    public static readonly string[] Kinds = ["invitations", "booklets", "nametags", "clues"];

    private static readonly string Ink = "#2b2118";
    private static readonly string Accent = "#8a6a2f";

    public static void MapKitEndpoints(this IEndpointRouteBuilder app)
    {
        // Host only: booklets and clue cards contain every secret and the solution.
        app.MapGet("/api/parties/{code}/kit/{kind}.pdf", async (string code, string kind, ClaimsPrincipal user, HttpRequest request,
            PartyService parties, AppDbContext db, MediaStore store, CancellationToken ct) =>
        {
            if (!Kinds.Contains(kind)) return Results.NotFound();
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null || party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.NotFound();

            // With "Surprise me" the killer is dealt when the evening begins, so the spoiler-filled
            // booklets and clue cards can't be printed before then.
            if (kind is "booklets" or "clues" && party.DealAtStart && party.Status == PartyStatus.Lobby)
                return Results.Problem("Booklets and clue cards aren't available before a \"Surprise me\" party begins, because the killer is dealt then. " +
                                       "To print them in advance, create the party with a specific version.", statusCode: 409);

            var snapshot = await parties.LoadAsync(party.Id, ct);
            var images = new ImageLoader(db, store);
            var joinUrl = $"{request.Scheme}://{request.Host}/join/{party.Code}";
            var pdf = kind switch
            {
                "invitations" => await Invitations(snapshot, joinUrl, party.ScheduledFor, images, ct),
                "booklets" => Booklets(snapshot),
                "nametags" => NameTags(snapshot),
                _ => Clues(snapshot),
            };
            var file = $"{Slug(snapshot.Scenario.Title)}-{kind}.pdf";
            return Results.File(pdf, "application/pdf", file);
        }).RequireAuthorization(AuthPolicies.Host);
    }

    /// <summary>Characters to print: those at the party once it has started, otherwise all of them.</summary>
    private static List<Character> Cast(PartySnapshot s) =>
        s.State.Phase == Phase.Lobby ? s.Scenario.Characters : GameEngine.CharactersInPlay(s.State, s.Scenario).ToList();

    private static string? PlayerName(PartySnapshot s, string characterId) => s.State.PlayerFor(characterId)?.Name;

    public static async Task<byte[]> Invitations(PartySnapshot s, string joinUrl, DateTimeOffset? when, ImageLoader images, CancellationToken ct)
    {
        var qr = new PngByteQRCode(new QRCodeGenerator().CreateQrCode(joinUrl, QRCodeGenerator.ECCLevel.M)).GetGraphic(12);
        var portraits = new Dictionary<string, byte[]?>();
        foreach (var c in Cast(s)) portraits[c.Id] = await images.LoadAsync(c.Portrait, ct);
        var scenario = s.Scenario;

        return Document.Create(doc =>
        {
            foreach (var c in Cast(s))
            {
                doc.Page(page =>
                {
                    Setup(page);
                    page.Content().Column(col =>
                    {
                        col.Spacing(12);
                        col.Item().AlignCenter().Text("YOU ARE CORDIALLY INVITED TO").FontSize(10).LetterSpacing(0.2f).FontColor(Accent);
                        col.Item().AlignCenter().Text(scenario.Title).FontSize(30).Bold();
                        col.Item().AlignCenter().Text($"{scenario.Setting.Place} · {scenario.Setting.Era}").Italic();
                        if (when is { } w) col.Item().AlignCenter().Text(w.ToLocalTime().ToString("dddd d MMMM yyyy, h:mm tt")).FontSize(13);
                        col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Accent);

                        col.Item().Row(row =>
                        {
                            if (portraits[c.Id] is { } img) row.ConstantItem(110).Image(img).FitArea();
                            row.RelativeItem().PaddingLeft(portraits[c.Id] is null ? 0 : 14).Column(info =>
                            {
                                var guest = PlayerName(s, c.Id);
                                info.Item().Text(guest is null ? "You will play" : $"{guest}, you will play").FontColor(Accent);
                                info.Item().Text(c.Name).FontSize(22).Bold();
                                info.Item().Text($"{c.Title} · {c.Pronouns}").Italic();
                                info.Item().PaddingTop(6).Text(c.PublicBio);
                            });
                        });

                        col.Item().Background(Colors.Grey.Lighten4).Padding(10).Column(box =>
                        {
                            box.Item().Text("WHAT TO WEAR").FontSize(9).Bold().FontColor(Accent);
                            box.Item().Text(c.CostumeTips);
                        });

                        col.Item().Text(scenario.Synopsis).Italic();

                        col.Item().PaddingTop(10).Row(row =>
                        {
                            row.ConstantItem(110).Image(qr);
                            row.RelativeItem().PaddingLeft(14).AlignMiddle().Column(j =>
                            {
                                j.Item().Text("On the night, scan this code with your phone").Bold();
                                j.Item().Text("to open your secret dossier.");
                                j.Item().PaddingTop(4).Text(joinUrl).FontSize(9).FontColor(Colors.Grey.Darken1);
                            });
                        });
                        col.Item().AlignCenter().Text("Keep your character's secrets to yourself. Trust no one.").FontSize(9).Italic();
                    });
                });
            }
        }).GeneratePdf();
    }

    public static byte[] Booklets(PartySnapshot s)
    {
        var scenario = s.Scenario;
        return Document.Create(doc =>
        {
            foreach (var c in Cast(s))
            {
                doc.Page(page =>
                {
                    Setup(page);
                    page.Header().Row(r =>
                    {
                        r.RelativeItem().Text(scenario.Title).Italic().FontColor(Accent);
                        r.RelativeItem().AlignRight().Text("PRIVATE: FOR YOUR EYES ONLY").FontSize(9).Bold().FontColor(Colors.Red.Darken2);
                    });
                    page.Content().Column(col =>
                    {
                        col.Spacing(10);
                        var guest = PlayerName(s, c.Id);
                        col.Item().Text(c.Name).FontSize(24).Bold();
                        col.Item().Text($"{c.Title} · {c.Pronouns}{(guest is null ? "" : $" · played by {guest}")}").Italic();
                        if (scenario.MurdererKnows && scenario.Solution.MurdererId == c.Id)
                            col.Item().Background(Colors.Red.Lighten4).Padding(8).Text("YOU ARE THE MURDERER. Keep it to yourself.").Bold().FontColor(Colors.Red.Darken3);
                        Section(col, "Who you are", c.Private.Backstory);
                        Section(col, "Your alibi", c.Private.Alibi);
                        List(col, "Your goals tonight", c.Private.Objectives);
                        List(col, "What you know", c.Private.Knows);

                        var byAct = c.Private.Secrets.GroupBy(x => x.UnlockAct).OrderBy(g => g.Key);
                        foreach (var group in byAct)
                        {
                            List(col, group.Key == 0 ? "Your secrets" : $"Secrets to read when Act {group.Key} begins", group.Select(x => x.Text));
                        }
                        foreach (var act in scenario.Acts)
                        {
                            if (c.Private.Lines.TryGetValue(act.Id, out var lines) && lines.Count > 0)
                                List(col, $"Say aloud in {act.Title}", lines.Select(l => $"“{l}”"));
                        }
                    });
                    Footer(page);
                });
            }
        }).GeneratePdf();
    }

    public static byte[] NameTags(PartySnapshot s)
    {
        var cast = Cast(s);
        return Document.Create(doc => doc.Page(page =>
        {
            Setup(page);
            page.Content().Table(table =>
            {
                table.ColumnsDefinition(cols => { cols.RelativeColumn(); cols.RelativeColumn(); });
                foreach (var c in cast)
                {
                    table.Cell().Padding(6).Border(1).BorderColor(Accent).Height(120).Padding(12).AlignMiddle().Column(tag =>
                    {
                        tag.Item().AlignCenter().Text("HELLO, MY NAME IS").FontSize(8).LetterSpacing(0.2f).FontColor(Accent);
                        tag.Item().AlignCenter().Text(c.Name).FontSize(18).Bold();
                        tag.Item().AlignCenter().Text(c.Title).Italic();
                        if (PlayerName(s, c.Id) is { } guest) tag.Item().AlignCenter().Text($"({guest})").FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
                }
            });
        })).GeneratePdf();
    }

    public static byte[] Clues(PartySnapshot s)
    {
        var scenario = s.Scenario;
        return Document.Create(doc =>
        {
            foreach (var act in scenario.Acts.Select((a, i) => (Act: a, Number: i + 1)))
            {
                doc.Page(page =>
                {
                    Setup(page);
                    page.Header().Text($"Clue cards: {act.Act.Title}").FontSize(16).Bold();
                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(cols => { cols.RelativeColumn(); cols.RelativeColumn(); });
                        foreach (var clue in scenario.Clues.Where(c => c.Act == act.Number))
                        {
                            table.Cell().Padding(6).Border(1).BorderColor(Accent).MinHeight(150).Padding(10).Column(card =>
                            {
                                var who = clue.Visibility == ClueVisibility.Private && clue.Recipient is not null ? scenario.FindCharacter(clue.Recipient)?.Name : null;
                                card.Item().Text(who is null ? (clue.Wave == ClueWave.Midway ? "Reveal halfway through the act" : "Reveal at the start of the act")
                                    : $"Give privately to {who}").FontSize(8).FontColor(Accent);
                                card.Item().Text(clue.Title).FontSize(14).Bold();
                                card.Item().PaddingTop(4).Text(clue.Text);
                                if (clue.Puzzle is not null) card.Item().PaddingTop(6).Text(clue.Puzzle.Prompt).Italic();
                            });
                        }
                    });
                    Footer(page);
                });
            }

            // The host's sealed envelope: puzzle answers and the solution.
            doc.Page(page =>
            {
                Setup(page);
                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().AlignCenter().Text("SOLUTION: DO NOT READ UNTIL THE REVEAL").FontSize(16).Bold().FontColor(Colors.Red.Darken2);
                    col.Item().AlignCenter().Text("Fold this page in half and seal it.").Italic();
                    foreach (var clue in scenario.Clues.Where(c => c.Puzzle is not null))
                        Section(col, $"Answer to \"{clue.Title}\"", $"{string.Join(" / ", clue.Puzzle!.Answers)}. {clue.Puzzle.SolvedText}");
                    var murderer = scenario.FindCharacter(scenario.Solution.MurdererId)?.Name;
                    Section(col, "The murderer", murderer ?? scenario.Solution.MurdererId);
                    foreach (var p in scenario.Solution.Explanation) col.Item().Text(p);
                });
            });
        }).GeneratePdf();
    }

    private static void Setup(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(1.6f, Unit.Centimetre);
        page.DefaultTextStyle(t => t.FontSize(11).FontColor(Ink).LineHeight(1.3f));
    }

    private static void Footer(PageDescriptor page) =>
        page.Footer().AlignCenter().Text(t =>
        {
            t.Span("The Butler Did It · page ").FontSize(8).FontColor(Colors.Grey.Medium);
            t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
        });

    private static void Section(ColumnDescriptor col, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        col.Item().Column(c =>
        {
            c.Item().Text(title.ToUpperInvariant()).FontSize(9).Bold().FontColor(Accent);
            c.Item().Text(text);
        });
    }

    private static void List(ColumnDescriptor col, string title, IEnumerable<string> items)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (list.Count == 0) return;
        col.Item().Column(c =>
        {
            c.Item().Text(title.ToUpperInvariant()).FontSize(9).Bold().FontColor(Accent);
            foreach (var item in list) c.Item().Text($"•  {item}");
        });
    }

    private static string Slug(string title) =>
        new string(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
}

/// <summary>Loads portraits for the PDFs from the media store. Only generated or uploaded assets are embedded.</summary>
public sealed class ImageLoader(AppDbContext db, MediaStore store)
{
    public async Task<byte[]?> LoadAsync(string? url, CancellationToken ct)
    {
        if (url is null || !url.StartsWith("/media/assets/", StringComparison.Ordinal)) return null;
        if (!Guid.TryParse(url["/media/assets/".Length..], out var id)) return null;
        var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null || !asset.ContentType.StartsWith("image/", StringComparison.Ordinal) || asset.ContentType == "image/svg+xml") return null;
        return store.Resolve(asset.Path) is { } path ? await File.ReadAllBytesAsync(path, ct) : null;
    }
}
