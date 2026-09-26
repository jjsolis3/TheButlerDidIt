using System.Text;
using System.Text.Json;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Ai.Generation;

public enum MysteryLength
{
    /// <summary>2 acts, about an hour.</summary>
    Short,

    /// <summary>3 acts, about two hours.</summary>
    Standard,

    /// <summary>4 acts, a long evening.</summary>
    Long,
}

public sealed record GenerationRequest(
    ThemeDefinition Theme,
    int Players,
    ContentRating ContentRating,
    MysteryLength Length,
    string? Twist);

public sealed record GenerationResult(Scenario Scenario, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a brand-new mystery with the Storyteller agent, then proves it works.
///
///   1. Outline: victim, killer, motive, method, cast. A short plan first gives
///      the model a coherent plot before it writes 10,000+ words of detail.
///   2. Full scenario JSON in the same format as hand-written mysteries.
///   3. ScenarioValidator: broken references or unfair clues are sent back to the
///      model with the exact errors, and it fixes them (up to MaxRepairs times).
///   4. Blind solver: the Inspector sees only what players could see and must
///      name the killer. If it can't, the Storyteller strengthens the clues once.
///
/// Nothing is saved unless the scenario passes validation, so a bad generation
/// can never become a broken party.
/// </summary>
public sealed class MysteryGenerator(AiGateway ai)
{
    public const int MaxRepairs = 3;
    public const string OutlineTask = "mystery-outline";
    public const string ScenarioTask = "mystery-scenario";
    public const string SolveTask = BlindSolver.SolveTask;

    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, AiCallContext context, IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var characterCount = Math.Clamp(request.Players + 2, 5, 10);
        var acts = request.Length switch { MysteryLength.Short => 2, MysteryLength.Long => 4, _ => 3 };
        var system = StorytellerSystem(request, characterCount, acts);

        progress?.Report("Plotting the crime…");
        var outline = await ai.CompleteAsync(AiRole.Storyteller, system,
            [new ChatMessage(ChatRole.User, OutlineInstructions(request, characterCount, acts))],
            context with { Purpose = "generate-outline" }, maxOutputTokens: 6_000, jsonOutput: true, ct);

        progress?.Report("Writing the characters, clues and scenes…");
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, OutlineInstructions(request, characterCount, acts)),
            new(ChatRole.Assistant, outline),
            new(ChatRole.User, ScenarioInstructions(request, characterCount, acts)),
        };

        Scenario? scenario = null;
        var solverRetried = false;
        for (var attempt = 0; attempt <= MaxRepairs; attempt++)
        {
            var text = await ai.CompleteAsync(AiRole.Storyteller, system, conversation,
                context with { Purpose = attempt == 0 ? "generate-scenario" : "repair-scenario" }, maxOutputTokens: 32_000, jsonOutput: true, ct);

            var errors = new List<string>();
            try
            {
                scenario = Normalize(JsonExtraction.Parse<Scenario>(text), request);
                errors.AddRange(ScenarioValidator.Validate(scenario));
            }
            catch (AiCallFailedException ex)
            {
                errors.Add(ex.Message);
                scenario = null;
            }

            if (errors.Count == 0 && scenario is not null)
            {
                progress?.Report("The Inspector is testing whether the mystery can be solved…");
                var solved = await BlindSolver.SolvesAsync(ai, scenario, context, ct);
                if (solved || solverRetried)
                {
                    if (!solved) warnings.Add("The Inspector found this mystery very hard to solve from the clues alone. Expect a challenge!");
                    return new GenerationResult(scenario, warnings);
                }
                solverRetried = true;
                errors.Add("A detective reading only the public clues and private clue texts could not identify the murderer. " +
                           $"Add or sharpen at least two genuine clues that point to '{scenario.Solution.MurdererId}' so the solution is deducible, " +
                           "without making it obvious in act 1.");
            }

            if (attempt == MaxRepairs) break;
            progress?.Report($"Fixing {errors.Count} problem{(errors.Count == 1 ? "" : "s")} in the draft (attempt {attempt + 2})…");
            conversation.Add(new ChatMessage(ChatRole.Assistant, text.Length > 60_000 ? text[..60_000] : text));
            conversation.Add(new ChatMessage(ChatRole.User,
                "Your scenario has these problems:\n- " + string.Join("\n- ", errors.Take(25)) +
                "\n\nReturn the complete corrected scenario JSON only."));
        }

        throw new AiCallFailedException("The Storyteller couldn't produce a valid mystery this time. Please try again, or try a different model.");
    }

    /// <summary>Server-side facts the model doesn't get to choose.</summary>
    private static Scenario Normalize(Scenario s, GenerationRequest request)
    {
        var slug = new string(s.Title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        // Round-trip through JSON with overrides: Scenario uses init-only properties.
        var node = JsonSerializer.SerializeToNode(s, GameJson.Options)!.AsObject();
        node["id"] = $"ai-{request.Theme.Slug}-{(slug.Length > 0 ? slug + "-" : "")}{Guid.NewGuid().ToString("N")[..6]}";
        node["themeSlug"] = request.Theme.Slug;
        node["contentRating"] = JsonSerializer.SerializeToNode(request.ContentRating, GameJson.Options);
        var characters = s.Characters.Count;
        node["maxPlayers"] = Math.Min(Math.Max(s.MaxPlayers, request.Players), characters);
        node["minPlayers"] = Math.Clamp(s.MinPlayers, 2, Math.Min(request.Players, characters));
        return node.Deserialize<Scenario>(GameJson.Options)!;
    }

    private static string StorytellerSystem(GenerationRequest r, int characters, int acts) => $$"""
        You are an award-winning writer of murder-mystery party games, like the boxed kits people play at dinner parties.
        Players each play a suspect. They read private dossiers, mingle in character, find clues act by act, and finally accuse the killer.

        Theme: {{r.Theme.Name}}: {{r.Theme.Tagline}}
        Era and setting: {{r.Theme.Era}}. {{r.Theme.Description}}
        {{ContentGuidance.For(r.ContentRating)}}

        Craft rules:
        - Exactly {{characters}} suspects and {{acts}} acts. Every suspect needs a motive, at least one juicy secret, and something that makes them look guilty.
        - The mystery must be FAIR: a careful player can deduce the murderer, motive and method from the clues. At least 3 genuine clues point to the killer, spread over the acts, with the decisive one in the final act.
        - Include red herrings that cast suspicion on at least 2 innocent suspects.
        - Mark as "required" the murderer and the suspects essential to the plot (at least 3). Optional suspects can be left out if fewer guests come.
        - Private clues must still read naturally if shown publicly ("Mrs X's diary: ..." rather than "You saw ...").
        - Lines are short, fun, performable sentences a guest says aloud in that act.
        - Invent all names; no real people or copyrighted characters.
        """;

    private static string OutlineInstructions(GenerationRequest r, int characters, int acts) => $$"""
        TASK: {{OutlineTask}}
        Plan a new mystery for {{r.Players}} guests ({{characters}} suspects, {{acts}} acts).{{(string.IsNullOrWhiteSpace(r.Twist) ? "" : $"\nThe host asked for this twist or flavour: \"{r.Twist.Trim()}\"")}}
        Reply with JSON only:
        {"title": "...", "synopsis": "...", "victim": "...", "murderer": "<suspect name>", "motive": "...", "method": "...",
         "suspects": [{"name": "...", "role": "...", "secret": "...", "whyTheyLookGuilty": "..."}],
         "keyClues": ["clue that points to the killer", "..."], "redHerrings": ["..."], "timeline": ["time: event", "..."]}
        """;

    private static string ScenarioInstructions(GenerationRequest r, int characters, int acts) => $$"""
        TASK: {{ScenarioTask}}
        Now write the complete scenario as a single JSON object, following your outline, in exactly this format (camelCase keys):
        {
          "id": "draft", "themeSlug": "{{r.Theme.Slug}}", "title": "...", "synopsis": "2-3 sentence hook",
          "contentRating": "{{(r.ContentRating == ContentRating.Family ? "family" : "mature")}}", "minPlayers": 3, "maxPlayers": {{characters}}, "estimatedMinutes": {{acts * 35 + 15}}, "murdererKnows": true,
          "setting": {"place": "...", "era": "...", "description": "..."},
          "victim": {"name": "...", "description": "..."},
          "characters": [{
            "id": "short-lowercase-id", "name": "...", "pronouns": "she/her", "title": "The ...", "publicBio": "what everyone knows",
            "costumeTips": "...", "voice": {"accent": "en-GB", "pitch": 1.0, "rate": 1.0, "style": "two or three words"}, "required": true,
            "private": {
              "backstory": "second person, what only this player reads. For the murderer start with 'YOU ARE THE MURDERER.' and explain exactly what they did.",
              "secrets": [{"id": "unique-id", "text": "first person", "unlockAct": 0}],
              "objectives": ["..."], "knows": ["things they witnessed"], "alibi": "first person claim",
              "lines": {"act1": ["a line to say aloud"], "act2": ["..."]}
            }
          }],
          "clues": [{"id": "unique-id", "title": "...", "text": "...", "visibility": "public", "act": 1, "wave": "start",
                     "pointsTo": ["character-id"], "redHerring": false}],
          "prologue": [{"type": "image", "text": "scene caption", "effect": "kenburns"}, {"type": "narration", "text": "..."}],
          "acts": [{"id": "act1", "title": "Act One: ...", "mingleMinutes": 20,
                    "cues": [{"type": "narration", "text": "..."}], "prompts": ["conversation starter for the table"]}],
          "accusation": {"motives": [{"id": "...", "text": "..."}], "methods": [{"id": "...", "text": "..."}]},
          "solution": {"murdererId": "character-id", "motiveId": "...", "methodId": "...",
                       "explanation": ["3-5 paragraphs revealed one at a time"], "timeline": [{"time": "...", "event": "..."}]},
          "finale": [{"type": "narration", "text": "..."}]
        }
        Rules the result is checked against:
        - Act ids are "act1".."act{{acts}}"; clue "act" numbers are 1..{{acts}}; secret unlockAct is 0..{{acts}}; "lines" keys are act ids.
        - "visibility" is "public" or "private"; private clues need a "recipient" character id. "wave" is "start" or "midway".
        - Clue "pointsTo" lists character ids. At least 3 clues with "redHerring": false point to the murderer; clues point to at least 2 innocent characters too.
        - Aim for {{acts * 5}} clues in total, including 1 private clue per act and optionally one clue with a riddle:
          "puzzle": {"prompt": "riddle", "answers": ["accepted answer"], "hint": "...", "solvedText": "what it reveals"}.
        - 4-6 motives and 4-5 methods; the solution's motiveId and methodId must be among them.
        - Every character has a non-empty publicBio and alibi. The murderer has "required": true.
        - Cue "type" is one of: narration, image, line (with "speaker" = character id), music, sfx, video, toast. Leave out "src" (media is added later).
        - Add one playful "toast" cue per act for parties that switch on drinking games, e.g. {"type": "toast", "text": "Raise a glass to...", "alternative": "the non-alcoholic version"}.{{(r.ContentRating == ContentRating.Family ? " For this family-friendly mystery, keep toasts about lemonade and cake, never alcohol." : "")}}
        Reply with the JSON object only, no commentary.
        """;
}
