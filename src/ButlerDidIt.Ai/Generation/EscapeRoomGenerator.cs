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
                        warnings.Add($"A tester couldn't crack {Plural(missed.Count, "puzzle")} from the clues alone ({string.Join(", ", missed.Select(m => $"\"{room.FindPuzzle(m.PuzzleId)?.Title}\""))}). The hints will help!");
                    return new EscapeRoomResult(Anonymize(room), warnings);
                }
                solverRetried = true;
                errors.AddRange(missed.Select(m => m.Logic
                    ? $"A tester who saw the line-up and every clue of logic puzzle '{m.PuzzleId}' couldn't work out the code. " +
                      "Make the things being lined up short and clearly different (\"red jar\", \"blue jar\"…), and the pieceTemplate plain, so every clue reads clearly."
                    : $"A tester who saw only the prompt, the clue pieces and what the room shows for riddle '{m.PuzzleId}' answered \"{m.Guess}\", which isn't accepted. " +
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

    /// <summary>The fewest puzzles a game of each length plays (Normal difficulty); hand-written rooms aim a little higher.</summary>
    public static int MinPuzzles(int minutes) => minutes switch { <= 30 => 7, <= 45 => 9, _ => 11 };
    public const int MaxPuzzles = 16;
    public const int MaxSpotsPerStage = 12;

    /// <summary>
    /// Rules for rooms the AI writes, on top of the validator's (which hand-written rooms follow too).
    /// They ask for the variety that makes a room play well (a scene to search in every stage, something
    /// to look at or put together, a cipher, a logic puzzle, few one-tap steps), and keep codes out of the AI's hands.
    /// </summary>
    public static List<string> ShapeErrors(EscapeRoom room)
    {
        var errors = new List<string>();
        if (room.Stages.Count is < 2 or > 4) errors.Add($"Use 3 stages (2 to 4 at most); this room has {room.Stages.Count}.");
        var played = room.Puzzles.Count(p => p.MinDifficulty is not EscapeDifficulty.Hard);
        var min = MinPuzzles(room.TimeLimitMinutes);
        if (played < min || played > MaxPuzzles)
            errors.Add($"A {room.TimeLimitMinutes}-minute room needs {min} to {MaxPuzzles} puzzles, not counting Hard-only ones; this room has {played}.");

        // The variety every room needs.
        if (!room.Puzzles.Any(IsRiddle)) errors.Add("At least one puzzle must be a riddle: kind \"text\", no generator, with its accepted \"answers\".");
        if (!room.Puzzles.Any(p => p.Kind == PuzzleKind.Search)) errors.Add("At least one puzzle must be kind \"search\", solved by searching the spots in its \"finds\".");
        if (!room.Items.Any(i => i.Inspect is not null) && room.Recipes.Count == 0)
            errors.Add("Give the group something to look at closely (an item with \"inspect\") or to put together (a \"recipes\" entry).");
        if (!room.Puzzles.Any(p => p.Generator?.Type == GeneratorType.Cipher)) errors.Add("At least one puzzle must use a \"cipher\" generator.");
        if (!room.Puzzles.Any(p => p.Generator?.Type is GeneratorType.Deduction or GeneratorType.Switches))
            errors.Add("At least one puzzle must be a logic puzzle: a \"deduction\" generator, or kind \"switches\".");
        if (!room.Puzzles.Any(p => p.MinDifficulty == EscapeDifficulty.Hard) && !room.SceneObjects.Any(o => o.MinDifficulty == EscapeDifficulty.Hard))
            errors.Add("Add something for Hard: a puzzle or a spot with \"minDifficulty\": \"hard\" (an extra cipher, a red herring).");

        // One-tap "use" steps: few, and never a freebie.
        var found = room.SceneObjects.Select(o => o.Gives).Concat(room.Items.Select(i => i.InspectGives)).Concat(room.Recipes.Select(r => r.Makes)).OfType<string>().ToHashSet();
        var uses = room.Puzzles.Where(p => p.Kind == PuzzleKind.Use).ToList();
        if (uses.Count > 2) errors.Add($"Use at most 2 puzzles of kind \"use\"; this room has {uses.Count}.");
        foreach (var p in uses.Where(p => !p.Requires.Any(found.Contains)))
            errors.Add($"Puzzle '{p.Id}' is a \"use\" step: it must need an item found in a spot, by a close look, or by putting two things together, not just a puzzle's reward.");

        // A scene in every stage: decoys to rule out, and places to hide clue pieces for small groups.
        foreach (var stage in room.Stages)
        {
            var spots = stage.Scene?.Objects ?? [];
            if (spots.Count is < 5 or > MaxSpotsPerStage) errors.Add($"Stage '{stage.Id}' needs a \"scene\" with 5 to {MaxSpotsPerStage} spots to search; it has {spots.Count}.");
            else
            {
                if (spots.Count(o => o.HidesPieces) < 2) errors.Add($"Stage '{stage.Id}': mark at least 2 spots with \"hidesPieces\": true.");
                if (!spots.Any(o => o is { Gives: null, Clue: null, HidesPieces: false, Requires: null }))
                    errors.Add($"Stage '{stage.Id}': add at least one decoy spot, with only a \"look\" that adds atmosphere.");
            }
        }

        foreach (var p in room.Puzzles)
        {
            if (p.Kind == PuzzleKind.Code && p.Generator is null)
                errors.Add($"Puzzle '{p.Id}' is a code: codes must use a generator (digitFacts, colorDigits, sequence or deduction), never fixed answers.");
            if (p.Generator?.Type == GeneratorType.ColorDigits && !p.Hints.Prepend(p.Prompt).Any(t => t.Contains("{order}")))
                errors.Add($"Puzzle '{p.Id}' uses colorDigits, so its prompt or a hint must contain {{order}} (the colour order).");
            if (room.ContentRating == ContentRating.Family && p.Generator is { Type: GeneratorType.Cipher, Cipher: CipherType.Symbols or CipherType.Morse } && p.MinDifficulty != EscapeDifficulty.Hard)
                errors.Add($"Puzzle '{p.Id}': in a family room, symbols and Morse ciphers are for Hard only (\"minDifficulty\": \"hard\"); use numbers, mirror or shift.");
            if (p.Hints.Count < 2) errors.Add($"Puzzle '{p.Id}' needs 2 or 3 hints, from a gentle nudge to nearly the answer.");
            if (IsRiddle(p) && p.Answers.Count > 0 && EscapeHintGuard.Leaks(string.Join(" ", p.Pieces.Prepend(p.Prompt)), p))
                errors.Add($"Riddle '{p.Id}' gives its own answer away in its prompt or clue pieces.");
        }
        foreach (var (id, word) in EscapeRoomText.CipherWordLeaks(room))
            errors.Add($"Puzzle '{id}': the cipher word \"{word}\" is already written elsewhere in the room, so the answer is on screen before it's decoded. Replace the word, or reword the other text.");
        foreach (var (id, answer) in EscapeRoomText.AnswersOnScreen(room))
            errors.Add($"Riddle '{id}': its answer \"{answer}\" is also the name of a spot or item, which the screens show. Rename the spot or item.");
        return errors;
    }

    /// <summary>A puzzle whose answer the AI wrote (a riddle), as opposed to a code or password from a template.</summary>
    public static bool IsRiddle(EscapePuzzle p) => p.Kind == PuzzleKind.Text && p.Generator is null;

    /// <summary>The backgrounds the screens can draw, picked from the room's (or stage's) sound.</summary>
    private static readonly Dictionary<string, string> Backdrops = new() { ["workshop"] = "workshop", ["carnival"] = "carnival", ["sea"] = "sea", ["space"] = "space", ["haunted"] = "haunted" };

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
        node.Remove("edition");
        // One fixed version: the generators already make every play different.
        // …and one length, the clock the host asked for: the model doesn't pick which puzzles a shorter game skips.
        node.Remove("lengths");
        foreach (var puzzle in node["puzzles"]!.AsArray())
        {
            foreach (var field in new[] { "variants", "minMinutes" }) puzzle!.AsObject().Remove(field);
            // Built from the seed, never written: the light grid, where a cipher's key is, the decoder, the line-up.
            foreach (var field in new[] { "grid", "keyAt", "decoder", "lineup" }) puzzle!.AsObject().Remove(field);
        }
        // The model describes each spot; the server places it, on a grid that never overlaps.
        var roomSound = room.Soundscape is { } rs ? JsonNamingPolicy.CamelCase.ConvertName(rs.ToString()) : "";
        foreach (var stage in node["stages"]?.AsArray() ?? [])
        {
            if (stage?["scene"] is not System.Text.Json.Nodes.JsonObject scene) continue;
            var sound = stage["soundscape"]?.GetValue<string>() ?? roomSound;
            scene["width"] = 1000;
            scene["height"] = 600;
            scene["backdrop"] = Backdrops.GetValueOrDefault(sound, "");
            var spots = scene["objects"]?.AsArray() ?? [];
            for (var i = 0; i < spots.Count; i++)
            {
                var (col, row) = (i % 4, i / 4);
                spots[i]!["x"] = col * 250 + 20;
                spots[i]!["y"] = row * 200 + 20;
                spots[i]!["w"] = 210;
                spots[i]!["h"] = 160;
            }
        }
        return node.Deserialize<EscapeRoom>(GameJson.Options)!;
    }

    /// <summary>
    /// Renames the puzzles and spots to neutral ids ("puzzle-1", "spot-1"…). The model names them after what
    /// they are ("echo-riddle", "loose-brick-with-key"), and those ids reach the browsers, so its names could
    /// give an answer away. Done last, so the repair turns can quote the model's own ids back to it.
    /// </summary>
    public static EscapeRoom Anonymize(EscapeRoom room)
    {
        var ids = room.Puzzles.Select((p, i) => (p.Id, New: $"puzzle-{i + 1}")).ToDictionary(x => x.Id, x => x.New);
        var spots = room.SceneObjects.Select((o, i) => (o.Id, New: $"spot-{i + 1}")).ToDictionary(x => x.Id, x => x.New);
        var node = JsonSerializer.SerializeToNode(room, GameJson.Options)!.AsObject();
        foreach (var puzzle in node["puzzles"]!.AsArray())
        {
            puzzle!["id"] = ids[puzzle["id"]!.GetValue<string>()];
            if (puzzle["finds"] is System.Text.Json.Nodes.JsonArray finds)
                puzzle["finds"] = new System.Text.Json.Nodes.JsonArray(finds.Select(f => (System.Text.Json.Nodes.JsonNode?)spots.GetValueOrDefault(f!.GetValue<string>(), f.GetValue<string>())).ToArray());
        }
        foreach (var stage in node["stages"]!.AsArray())
        {
            stage!["puzzles"] = new System.Text.Json.Nodes.JsonArray(stage["puzzles"]!.AsArray().Select(id => (System.Text.Json.Nodes.JsonNode?)ids[id!.GetValue<string>()]).ToArray());
            foreach (var spot in stage["scene"]?["objects"]?.AsArray() ?? []) spot!["id"] = spots[spot["id"]!.GetValue<string>()];
        }
        // Where a cipher's key is written, "{key:the-cipher}" names the puzzle too.
        RewriteStrings(node, text => RoomVariants.KeyPlaceholder().Replace(text, m => $"{{key:{ids.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)}}}"));
        return node.Deserialize<EscapeRoom>(GameJson.Options)!;
    }

    private static void RewriteStrings(System.Text.Json.Nodes.JsonNode? node, Func<string, string> rewrite)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text)) o[key] = rewrite(text);
                    else RewriteStrings(o[key], rewrite);
                }
                break;
            case System.Text.Json.Nodes.JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text)) a[i] = rewrite(text);
                    else RewriteStrings(a[i], rewrite);
                }
                break;
        }
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string System(EscapeRoomRequest r) => $"""
        You design escape rooms for groups of 1 to 8 friends playing at home. A TV shows the room, the puzzles and the clock;
        everyone holds a phone. Clue pieces are split between the phones, so the group must talk; with fewer players, the
        spare pieces are hidden in the room's spots, so a solo player has to search for them.
        The room is played in {r.Minutes} minutes. Each hint costs time, so hints go from a gentle nudge to nearly the answer.
        {ContentGuidance.For(r.ContentRating)}

        Craft rules:
        - A story in 3 stages (rooms of the place), each opening when every puzzle in the one before is solved.
        - {MinPuzzles(r.Minutes)} to {Math.Min(MaxPuzzles, MinPuzzles(r.Minutes) + 4)} puzzles in total, plus 1 or 2 for Hard only.
        - Every stage is a scene with spots to search: some hold items or clues, some hide spare clue pieces, some are decoys that only add atmosphere.
        - Chain things together: a riddle gives half of a tool, a search gives the other half, a recipe puts them together,
          and the tool unlocks a spot where a cipher's key is written. One-tap "use" steps are few, and always need something found.
        - Every puzzle is fair: a group that reads the TV, searches the room and shares their phones' pieces can solve it.
        - Riddles have one clear answer that fits the theme. Never put the answer in the prompt, the pieces, or a spot's or item's name.
        - You never invent codes, cipher text or logic clues: they come from generators (see the format), made fresh every game.
        - A game master (the villain or host of the room) taunts the group: invent their name and persona.
        - Invent all names; no real people, no copyrighted characters or villains.
        """;

    private static string Instructions(EscapeRoomRequest r) => $$$"""
        TASK: {{{WriteTask}}}
        Write a new escape room on this theme: "{{{r.Theme.Trim()}}}"
        Reply with a single JSON object in exactly this format (camelCase keys):
        {
          "id": "draft", "title": "...", "synopsis": "2 sentences for the shelf", "contentRating": "{{{(r.ContentRating == ContentRating.Family ? "family" : "mature")}}}",
          "artStyle": "a few words describing the look", "soundscape": "the background sound: drone, workshop, carnival, sea, space or haunted", "minPlayers": 2, "maxPlayers": 8, "timeLimitMinutes": {{{r.Minutes}}},
          "intro": "read on the TV when the clock starts, in the game master's voice",
          "escapedText": "read when they escape", "failedText": "read when time runs out",
          "gameMaster": {"name": "...", "persona": "how they talk, 1-2 sentences", "voice": {"accent": "en-GB", "pitch": 1.0, "rate": 1.0, "style": "two or three words"} },
          "stages": [{"id": "short-id", "title": "...", "description": "read when the stage opens; mention the spots", "puzzles": ["puzzle-id", "..."],
            "scene": {"objects": [
              {"id": "loose-brick", "prop": "one of: {{{string.Join(", ", SceneProps.Known.Order())}}}", "label": "loose brick", "look": "what the searcher sees",
               "gives": "item-id (optional)", "clue": "a note for the group's notebook (optional)", "hidesPieces": true,
               "requires": "a tool item, not used up (optional)", "lockedText": "what they see without the tool", "minDifficulty": "hard (optional: only on Hard)"}
            ]}}],
          "puzzles": [
            {"id": "a-riddle", "title": "...", "kind": "text", "prompt": "what the TV shows, including the riddle",
             "answers": ["answer", "other accepted spelling"], "hints": ["nudge", "stronger", "nearly the answer"], "rewards": ["tool-half"], "solvedText": "what happens when it opens"},
            {"id": "a-search", "title": "...", "kind": "search", "prompt": "which spots to search", "finds": ["spot-id", "spot-id"], "rewards": ["other-half"], "hints": ["...", "search the X and the Y"], "solvedText": "..."},
            {"id": "a-cipher", "title": "...", "kind": "text", "prompt": "... {cipher} ...", "hints": ["where the key is", "how to decode", "It says {answer}."], "solvedText": "...",
             "generator": {"type": "cipher", "cipher": "shift", "words": ["six", "themed", "single", "words", "of", "4-10 letters"]}},
            {"id": "a-pattern", "title": "...", "kind": "code", "prompt": "... {sequence} ...", "hints": ["Look at how each number changes.", "The next number is {answer}."], "solvedText": "...", "generator": {"type": "sequence"}},
            {"id": "a-logic", "title": "...", "kind": "code", "prompt": "Five things in a row: {items}. The code is each one's place, far left is 1, in the order listed.",
             "hints": ["...", "...", "The code is {answer}."], "rewards": ["item-id"], "solvedText": "...",
             "generator": {"type": "deduction", "words": ["red jar", "blue jar", "green jar", "gold jar", "silver jar"], "pieceTemplate": "Scratched on the shelf: {clue}"}},
            {"id": "a-panel", "title": "...", "kind": "switches", "prompt": "each light flips its neighbours; turn them all on", "hints": ["Work from the top row down.", "Press {answer}."], "solvedText": "...", "generator": {"type": "switches"}},
            {"id": "a-code", "title": "...", "kind": "code", "prompt": "... Use {order} if you use colorDigits ...", "hints": ["...", "..."], "solvedText": "...",
             "generator": {"type": "digitFacts", "count": 3, "pieceTemplate": "The {ordinal} digit is {fact}." } },
            {"id": "a-password", "title": "...", "kind": "text", "prompt": "...", "requires": ["item-id"], "hints": ["...", "..."], "solvedText": "The word was {answer}!",
             "generator": {"type": "wordSequence", "count": 3, "words": ["ten or more", "themed single words", "..."], "pieceTemplate": "The {ordinal} word is {word}." } },
            {"id": "a-lock", "title": "...", "kind": "use", "prompt": "...", "requires": ["tool-from-a-recipe"], "rewards": ["other-item"], "hints": ["...", "use the X on the Y"], "solvedText": "..."}
          ],
          "items": [{"id": "item-id", "name": "Brass locket", "description": "...", "inspect": "what a close look shows (optional; may hold {key:a-cipher})", "inspectRequires": "tool (optional)", "inspectGives": "item-id (optional)"}],
          "recipes": [{"items": ["tool-half", "other-half"], "makes": "tool", "text": "read when they're put together"}]
        }
        Generators (codes, cipher text and logic clues are made from them every game; you write only the flavour):
        - "digitFacts": a code whose digits are everyday facts ("the number of legs on a spider"), one per phone. "kind": "code". pieceTemplate must contain {ordinal} and {fact}.
        - "colorDigits": a code read from coloured objects in a colour order; each phone sees one colour and its number. "kind": "code".
          Give 5 or more "colors"; pieceTemplate must contain {color} and {digit}; put {order} in the prompt or a hint so the group knows the order.
        - "wordSequence": a password of words in order; each phone remembers one word. "kind": "text". Give 10 or more themed "words";
          pieceTemplate must contain {ordinal} and {word}. Prompts, hints and solvedText may use {answer}.
        - "cipher": a coded word, picked from your "words", shown where the prompt says {cipher}. "kind": "text". "cipher" is one of:
          numbers (A=1…Z=26) or mirror (A↔Z): no key needed; shift, symbols or morse: write "{key:<this puzzle's id>}" in a spot's "look"
          or an item's "inspect" where the group will find it (ideally a spot that needs a tool). {{{(r.ContentRating == ContentRating.Family ? "This is a family room: use numbers, mirror or shift with short words; symbols and morse only on a Hard-only puzzle." : "Symbols and morse make good Hard-only puzzles.")}}}
          Cipher words must not appear anywhere else in the room's text.
        - "sequence": a number pattern shown where the prompt says {sequence}; the group types the next number. "kind": "code".
        - "deduction": a logic puzzle. "words" are 5 different things to line up; the prompt shows them with {items}; each clue piece
          is the pieceTemplate with {clue}. "kind": "code". The code is each thing's place, in the order listed.
        - "switches": a grid of lights. "kind": "switches". Use {answer} only in the last hint.
        Rules the room is checked against:
        - Every puzzle id is in exactly one stage. "kind" is "text", "code", "use", "search" or "switches". Codes always use a generator; never write code answers.
        - At least 1 riddle (text, no generator, with "answers"), 1 search, 1 cipher, and 1 deduction or switches; at least one item with "inspect", or one recipe.
        - At most 2 "use" puzzles, each needing an item found in a spot ("gives"), by a close look ("inspectGives"), or made by a recipe.
        - Every stage has a "scene" with 5 to {{{MaxSpotsPerStage}}} spots: at least 2 with "hidesPieces": true, and at least 1 decoy with only a "look".
          Spot ids are unique in the room; "finds" lists spots in the same stage. Don't give positions: the screens lay the spots out.
        - Something is Hard-only ("minDifficulty": "hard"): an extra cipher whose key is on a Hard-only spot, and a red-herring spot and item that fit nothing.
        - Every item comes from exactly one place: a puzzle's "rewards", a spot's "gives", an item's "inspectGives" or a recipe's "makes". An item is either
          used up (by a puzzle's "requires" or a recipe) or a tool (a spot's "requires" or an item's "inspectRequires"), never both. Every item used is listed in "items".
        - Every item in "requires" is found before it's needed (in an earlier stage, or earlier in the same stage).
        - Every puzzle has 2 or 3 hints. Leave out "variants", "lengths" and "minMinutes".
        Reply with the JSON object only, no commentary.
        """;
}

/// <summary>
/// The fairness test for what the AI wrote: the Inspector sees each riddle and each logic puzzle as the
/// group would, and must answer it. For a riddle that's its prompt, its clue pieces, and everything the
/// stage's spots and the group's items show (a scene riddle's clue can be written on a spot); for a logic
/// puzzle, its line-up and every clue, as one game builds them. Never the answers or the hints.
/// Codes, ciphers, patterns and light panels from generators aren't tested: they're correct by
/// construction, and the validator proves every one can be solved.
/// </summary>
public static class EscapeRoomSolver
{
    /// <summary>The puzzle set the logic puzzles are built from for the test. Any seed would do; a fixed one makes the test repeatable.</summary>
    public const long Seed = 1;

    public sealed record Miss(string PuzzleId, string Guess, bool Logic);

    /// <summary>
    /// What's tested, numbered as in the prompt: the riddles, then the logic puzzles, as one game (<see cref="Seed"/>) builds them.
    /// Chosen from the room as written: a built puzzle no longer says which generator made it.
    /// </summary>
    public static List<(EscapePuzzle Puzzle, bool Logic)> Tested(EscapeRoom room)
    {
        var built = RoomVariants.Build(room, Seed);
        return room.Puzzles.Where(EscapeRoomGenerator.IsRiddle).Select(p => (built.FindPuzzle(p.Id)!, false))
            .Concat(room.Puzzles.Where(p => p.Generator?.Type == GeneratorType.Deduction && p.MinDifficulty is not EscapeDifficulty.Hard).Select(p => (built.FindPuzzle(p.Id)!, true)))
            .ToList();
    }

    public static string Prompt(EscapeRoom room)
    {
        var built = RoomVariants.Build(room, Seed);
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {EscapeRoomGenerator.SolveTask}");
        sb.AppendLine($"You are testing an escape room called \"{room.Title}\". Answer each puzzle below using ONLY what is written there.");
        sb.AppendLine("For a riddle, give the single most likely answer, one or two words. For a logic puzzle, give the code: digits only.");
        // Numbered rather than by id: the model names puzzles after their answers ("echo-riddle").
        foreach (var ((p, logic), i) in Tested(room).Select((t, i) => (t, i)))
        {
            sb.AppendLine();
            sb.AppendLine($"## {(logic ? "Logic puzzle" : "Riddle")} {i + 1}: {p.Title}");
            sb.AppendLine(p.Prompt);
            foreach (var piece in p.Pieces) sb.AppendLine($"- {(logic ? "A clue" : "A clue piece")}: {piece}");
            if (logic) continue;
            // What searching and looking can turn up in this part of the room.
            var stage = built.Stages.FirstOrDefault(st => st.Puzzles.Contains(p.Id));
            foreach (var spot in (stage?.Scene?.Objects ?? []).Where(o => o.MinDifficulty is not EscapeDifficulty.Hard))
                sb.AppendLine($"- Searching the {spot.Label} shows: {spot.Look}{(spot.Clue is { } clue ? $" (noted: {clue})" : "")}");
            foreach (var item in built.Items.Where(it => it.Inspect is not null))
                sb.AppendLine($"- A close look at {item.Name} shows: {item.Inspect}");
        }
        sb.AppendLine();
        sb.AppendLine("Reply with JSON only: {\"answers\": {\"1\": \"<answer to puzzle 1>\", \"2\": \"...\"}}");
        return sb.ToString();
    }

    /// <summary>The puzzles the tester got wrong. Empty when every one was solved, or when the test itself failed.</summary>
    public static async Task<List<Miss>> MissedAsync(AiGateway ai, EscapeRoom room, AiCallContext context, CancellationToken ct)
    {
        var tested = Tested(room);
        if (tested.Count == 0) return [];
        try
        {
            var reply = await ai.CompleteJsonAsync<SolverReply>(AiRole.Inspector, Prompt(room),
                [new ChatMessage(ChatRole.User, "Answer every puzzle.")], context with { Purpose = "escape-room-solve" }, 1_500, ct);
            return tested
                .Select((t, i) => (t.Puzzle, t.Logic, Guess: reply.Answers.GetValueOrDefault($"{i + 1}") ?? ""))
                .Where(x => !Answers.Matches(x.Puzzle, x.Guess))
                .Select(x => new Miss(x.Puzzle.Id, x.Guess, x.Logic))
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
