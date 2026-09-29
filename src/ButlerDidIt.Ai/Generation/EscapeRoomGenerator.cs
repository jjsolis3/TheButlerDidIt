using System.Text;
using System.Text.Json;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Ai.Generation;

/// <param name="Theme">What the host typed: "a haunted lighthouse", "a spaceship whose computer has gone rogue".</param>
/// <param name="Minutes">The clock: 30, 45 or 60.</param>
public sealed record EscapeRoomRequest(string Theme, ContentRating ContentRating, int Minutes);

public sealed record EscapeRoomResult(EscapeRoom Room, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a brand-new escape room from a theme with the Storyteller, then proves it works.
///
///   1. The Storyteller writes the whole room as JSON, in the same format as the hand-written rooms.
///      It writes the story, the riddles and the items, and picks which code templates fill the
///      code slots (digitFacts, colorDigits, wordSequence). It never writes a code itself: those
///      come from the templates and the seed, so they are correct by construction.
///   2. <see cref="ShapeErrors"/> and <see cref="EscapeRoomValidator"/>: broken references, a stage
///      that can't be finished, a code that isn't a template… are sent back with the exact errors,
///      and the model fixes them (up to MaxRepairs times).
///   3. Blind solver: the Inspector sees each riddle as the group would (the prompt and every clue
///      piece, never the answers or the hints) and must answer it. A missed riddle is sent back
///      once to be made clearer.
///
/// Nothing is returned unless the room passes validation, so a bad generation can never become a broken party.
/// </summary>
public sealed class EscapeRoomGenerator(AiGateway ai)
{
    public const int MaxRepairs = 3;
    public const string WriteTask = "escape-room-write";
    public const string SolveTask = "escape-room-solve";

    public async Task<EscapeRoomResult> GenerateAsync(EscapeRoomRequest request, AiCallContext context, IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var system = System(request);
        var conversation = new List<ChatMessage> { new(ChatRole.User, Instructions(request)) };

        progress?.Report("Building the room: locks, keys and riddles…");
        var solverRetried = false;
        for (var attempt = 0; attempt <= MaxRepairs; attempt++)
        {
            var text = await ai.CompleteAsync(AiRole.Storyteller, system, conversation,
                context with { Purpose = attempt == 0 ? "generate-escape-room" : "repair-escape-room" }, maxOutputTokens: 16_000, jsonOutput: true, ct);

            var errors = new List<string>();
            EscapeRoom? room = null;
            try
            {
                room = Normalize(JsonExtraction.Parse<EscapeRoom>(text), request);
                errors.AddRange(ShapeErrors(room));
                if (errors.Count == 0) errors.AddRange(EscapeRoomValidator.Validate(room));
            }
            catch (AiCallFailedException ex)
            {
                errors.Add(ex.Message);
            }

            if (errors.Count == 0 && room is not null)
            {
                progress?.Report("A tester is trying the riddles…");
                var missed = await EscapeRoomSolver.MissedAsync(ai, room, context, ct);
                if (missed.Count == 0 || solverRetried)
                {
                    if (missed.Count > 0)
                        warnings.Add($"A tester couldn't crack {Plural(missed.Count, "riddle")} from the clues alone ({string.Join(", ", missed.Select(m => $"\"{room.FindPuzzle(m.PuzzleId)?.Title}\""))}). The hints will help!");
                    return new EscapeRoomResult(Anonymize(room), warnings);
                }
                solverRetried = true;
                errors.AddRange(missed.Select(m =>
                    $"A tester who saw only the prompt and the clue pieces of riddle '{m.PuzzleId}' answered \"{m.Guess}\", which isn't accepted. " +
                    "Make the riddle clearer so there is one fair answer, or add the tester's answer to \"answers\" if it is also correct."));
            }

            if (attempt == MaxRepairs) break;
            progress?.Report($"Fixing {Plural(errors.Count, "problem")} in the room (attempt {attempt + 2})…");
            conversation.Add(new ChatMessage(ChatRole.Assistant, text.Length > 40_000 ? text[..40_000] : text));
            conversation.Add(new ChatMessage(ChatRole.User,
                $"TASK: {WriteTask}\nYour room has these problems:\n- " + string.Join("\n- ", errors.Take(25)) +
                "\n\nReturn the complete corrected room JSON only."));
        }

        throw new AiCallFailedException("The Storyteller couldn't build a working escape room this time. Please try again, or try a different theme or model.");
    }

    /// <summary>
    /// Rules for rooms the AI writes, on top of the validator's (which hand-written rooms follow too).
    /// They keep generated rooms to the shape that is known to play well, and keep codes out of the AI's hands.
    /// </summary>
    public static List<string> ShapeErrors(EscapeRoom room)
    {
        var errors = new List<string>();
        if (room.Stages.Count is < 2 or > 4) errors.Add($"Use 3 stages (2 to 4 at most); this room has {room.Stages.Count}.");
        if (room.Puzzles.Count is < 5 or > 10) errors.Add($"Use 6 to 9 puzzles (5 to 10 at most); this room has {room.Puzzles.Count}.");
        if (room.Puzzles.Count(p => p.Generator is not null) < 2) errors.Add("At least two puzzles must use a \"generator\" (digitFacts, colorDigits or wordSequence).");
        if (!room.Puzzles.Any(IsRiddle)) errors.Add("At least one puzzle must be a riddle: kind \"text\", no generator, with its accepted \"answers\".");
        if (!room.Puzzles.Any(p => p.Kind == PuzzleKind.Use)) errors.Add("At least one puzzle must be kind \"use\", opened with an item another puzzle gives.");
        foreach (var p in room.Puzzles)
        {
            if (p.Kind == PuzzleKind.Code && p.Generator is null)
                errors.Add($"Puzzle '{p.Id}' is a code: codes must use a digitFacts or colorDigits \"generator\", never fixed answers.");
            if (p.Generator?.Type == GeneratorType.ColorDigits && !p.Hints.Prepend(p.Prompt).Any(t => t.Contains("{order}")))
                errors.Add($"Puzzle '{p.Id}' uses colorDigits, so its prompt or a hint must contain {{order}} (the colour order).");
            if (p.Hints.Count < 2) errors.Add($"Puzzle '{p.Id}' needs 2 or 3 hints, from a gentle nudge to nearly the answer.");
            if (IsRiddle(p) && p.Answers.Count > 0 && EscapeHintGuard.Leaks(string.Join(" ", p.Pieces.Prepend(p.Prompt)), p))
                errors.Add($"Riddle '{p.Id}' gives its own answer away in its prompt or clue pieces.");
        }
        return errors;
    }

    /// <summary>A puzzle whose answer the AI wrote (a riddle), as opposed to a code or password from a template.</summary>
    public static bool IsRiddle(EscapePuzzle p) => p.Kind == PuzzleKind.Text && p.Generator is null;

    /// <summary>Server-side facts the model doesn't get to choose.</summary>
    public static EscapeRoom Normalize(EscapeRoom room, EscapeRoomRequest request)
    {
        var slug = new string(room.Title.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        // Round-trip through JSON with overrides: rooms use init-only properties.
        var node = JsonSerializer.SerializeToNode(room, GameJson.Options)!.AsObject();
        node["id"] = $"ai-escape-{(slug.Length > 0 ? slug + "-" : "")}{Guid.NewGuid().ToString("N")[..6]}";
        node["contentRating"] = JsonSerializer.SerializeToNode(request.ContentRating, GameJson.Options);
        node["theme"] = "ai";
        node["timeLimitMinutes"] = request.Minutes;
        node["hintPenaltySeconds"] = request.Minutes <= 30 ? 90 : 120;
        node["minPlayers"] = Math.Clamp(room.MinPlayers, 2, 8);
        node["maxPlayers"] = 8;
        // One fixed version: the generators already make every play different.
        foreach (var puzzle in node["puzzles"]!.AsArray()) puzzle!.AsObject().Remove("variants");
        return node.Deserialize<EscapeRoom>(GameJson.Options)!;
    }

    /// <summary>
    /// Renames the puzzles to neutral ids ("puzzle-1"…). The model names them after what they are
    /// ("echo-riddle"), and puzzle ids reach the browsers, so its names could give an answer away.
    /// Done last, so the repair turns can quote the model's own ids back to it.
    /// </summary>
    public static EscapeRoom Anonymize(EscapeRoom room)
    {
        var ids = room.Puzzles.Select((p, i) => (p.Id, New: $"puzzle-{i + 1}")).ToDictionary(x => x.Id, x => x.New);
        var node = JsonSerializer.SerializeToNode(room, GameJson.Options)!.AsObject();
        foreach (var puzzle in node["puzzles"]!.AsArray()) puzzle!["id"] = ids[puzzle["id"]!.GetValue<string>()];
        foreach (var stage in node["stages"]!.AsArray())
            stage!["puzzles"] = new System.Text.Json.Nodes.JsonArray(stage["puzzles"]!.AsArray().Select(id => (System.Text.Json.Nodes.JsonNode?)ids[id!.GetValue<string>()]).ToArray());
        return node.Deserialize<EscapeRoom>(GameJson.Options)!;
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string System(EscapeRoomRequest r) => $"""
        You design escape rooms for groups of 2 to 8 friends playing at home. A TV shows the room, the puzzles and the clock;
        everyone holds a phone, and clue pieces are split between the phones, so the group must talk to escape.
        The room is played in {r.Minutes} minutes. Each hint costs time, so hints go from a gentle nudge to nearly the answer.
        {ContentGuidance.For(r.ContentRating)}

        Craft rules:
        - A story in 3 stages (rooms of the place), each opening when every puzzle in the one before is solved.
        - 6 to 9 puzzles in total, 2 or 3 per stage. Mix the kinds: riddles, codes, passwords, and using items found earlier.
        - Every puzzle is fair: a group that reads the prompt on the TV and shares their phones' pieces can solve it.
        - Riddles have one clear answer that fits the theme. Never put the answer in the prompt or the pieces.
        - You never invent codes: every code or password comes from a generator (see the format), which picks it fresh every game.
        - A game master (the villain or host of the room) taunts the group: invent their name and persona.
        - Invent all names; no real people, no copyrighted characters or villains.
        """;

    private static string Instructions(EscapeRoomRequest r) => $$"""
        TASK: {{WriteTask}}
        Write a new escape room on this theme: "{{r.Theme.Trim()}}"
        Reply with a single JSON object in exactly this format (camelCase keys):
        {
          "id": "draft", "title": "...", "synopsis": "2 sentences for the shelf", "contentRating": "{{(r.ContentRating == ContentRating.Family ? "family" : "mature")}}",
          "artStyle": "a few words describing the look", "soundscape": "the background sound: drone, workshop, carnival, sea, space or haunted", "minPlayers": 2, "maxPlayers": 8, "timeLimitMinutes": {{r.Minutes}},
          "intro": "read on the TV when the clock starts, in the game master's voice",
          "escapedText": "read when they escape", "failedText": "read when time runs out",
          "gameMaster": {"name": "...", "persona": "how they talk, 1-2 sentences", "voice": {"accent": "en-GB", "pitch": 1.0, "rate": 1.0, "style": "two or three words"} },
          "stages": [{"id": "short-id", "title": "...", "description": "read when the stage opens", "puzzles": ["puzzle-id", "..."]}],
          "puzzles": [
            {"id": "a-riddle", "title": "...", "kind": "text", "prompt": "what the TV shows, including the riddle",
             "answers": ["answer", "other accepted spelling"], "pieces": [], "hints": ["nudge", "stronger", "nearly the answer"],
             "rewards": ["item-id"], "solvedText": "what happens when it opens"},
            {"id": "a-code", "title": "...", "kind": "code", "prompt": "... Use {order} if you use colorDigits ...", "hints": ["...", "..."], "solvedText": "...",
             "generator": {"type": "digitFacts", "count": 3, "pieceTemplate": "The {ordinal} digit is {fact}." } },
            {"id": "a-password", "title": "...", "kind": "text", "prompt": "...", "requires": ["item-id"], "hints": ["...", "..."], "solvedText": "The word was {answer}!",
             "generator": {"type": "wordSequence", "count": 3, "words": ["ten or more", "themed single words", "..."], "pieceTemplate": "The {ordinal} word is {word}." } },
            {"id": "a-lock", "title": "...", "kind": "use", "prompt": "...", "requires": ["item-id"], "rewards": ["other-item"], "hints": ["...", "use the X on the Y"], "solvedText": "..."}
          ],
          "items": [{"id": "item-id", "name": "Brass key", "description": "..."}]
        }
        Generators (codes and passwords are picked from them every game; you write only the flavour):
        - "digitFacts": a code whose digits are everyday facts ("the number of legs on a spider"), one per phone. "kind": "code". pieceTemplate must contain {ordinal} and {fact}.
        - "colorDigits": a code read from coloured objects in a colour order; each phone sees one colour and its number. "kind": "code".
          Give 5 or more "colors"; pieceTemplate must contain {color} and {digit}; put {order} in the prompt or a hint so the group knows the order.
        - "wordSequence": a password of words in order; each phone remembers one word. "kind": "text". Give 10 or more themed "words";
          pieceTemplate must contain {ordinal} and {word}. Prompts, hints and solvedText may use {answer}.
        Rules the room is checked against:
        - Every puzzle id is in exactly one stage. "kind" is "text", "code" or "use". Codes always use a generator; never write code answers.
        - At least 2 puzzles use a generator, at least 1 is a riddle (text, no generator, with "answers"), and at least 1 is "use".
        - "use" puzzles require at least one item and have no answers. Every item in "requires" is a reward of an earlier puzzle
          (in an earlier stage, or earlier in the same stage), and each item is given by only one puzzle. Every item used is listed in "items".
        - Every puzzle has 2 or 3 hints. Leave out "variants".
        Reply with the JSON object only, no commentary.
        """;
}

/// <summary>
/// The fairness test for AI-written riddles: the Inspector sees each riddle as the group would (its
/// prompt and every clue piece, never the answers or the hints) and must answer it. Codes and passwords
/// from generators aren't tested: they are correct by construction, and the validator checks them.
/// </summary>
public static class EscapeRoomSolver
{
    public sealed record Miss(string PuzzleId, string Guess);

    public static string Prompt(EscapeRoom room)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {EscapeRoomGenerator.SolveTask}");
        sb.AppendLine($"You are testing an escape room called \"{room.Title}\". Answer each riddle below using ONLY what is written there.");
        sb.AppendLine("Give the single most likely answer, one or two words.");
        // Numbered rather than by id: the model names puzzles after their answers ("echo-riddle").
        foreach (var (p, i) in room.Puzzles.Where(EscapeRoomGenerator.IsRiddle).Select((p, i) => (p, i)))
        {
            sb.AppendLine();
            sb.AppendLine($"## Riddle {i + 1}: {p.Title}");
            sb.AppendLine(p.Prompt);
            foreach (var piece in p.Pieces) sb.AppendLine($"- A clue piece: {piece}");
        }
        sb.AppendLine();
        sb.AppendLine("Reply with JSON only: {\"answers\": {\"1\": \"<answer to riddle 1>\", \"2\": \"...\"}}");
        return sb.ToString();
    }

    /// <summary>The riddles the tester got wrong. Empty when every riddle was solved, or when the test itself failed.</summary>
    public static async Task<List<Miss>> MissedAsync(AiGateway ai, EscapeRoom room, AiCallContext context, CancellationToken ct)
    {
        var riddles = room.Puzzles.Where(EscapeRoomGenerator.IsRiddle).ToList();
        if (riddles.Count == 0) return [];
        try
        {
            var reply = await ai.CompleteJsonAsync<SolverReply>(AiRole.Inspector, Prompt(room),
                [new ChatMessage(ChatRole.User, "Answer every riddle.")], context with { Purpose = "escape-room-solve" }, 1_500, ct);
            return riddles
                .Select((p, i) => new Miss(p.Id, reply.Answers.GetValueOrDefault($"{i + 1}") ?? ""))
                .Where(m => !Answers.Matches(room.FindPuzzle(m.PuzzleId)!, m.Guess))
                .ToList();
        }
        catch (AiCallFailedException)
        {
            // If the test itself fails, don't throw away a valid room.
            return [];
        }
    }

    private sealed class SolverReply
    {
        public Dictionary<string, string> Answers { get; set; } = [];
    }
}
