using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Ai.Generation;

/// <summary>
/// Writes a new version of an existing story in which a given character is the killer: the
/// "AI remix" used when no hand-written version's killer is one of tonight's guests.
///
/// The Storyteller returns a patch in the same format as hand-written versions (see
/// ScenarioVariants), not a whole mystery. That keeps the setting, cast, bios and costumes
/// exactly as the guests saw them in the lobby, and keeps the answer small. The result must
/// pass the same checks as any mystery: allowed keys only, ScenarioValidator (fair clues,
/// Family rules, the killer may be a killer), the right killer, and the blind solve. Errors go
/// back to the model to fix, up to <see cref="MaxRepairs"/> times.
/// </summary>
public sealed class VersionRemixer(AiGateway ai)
{
    public const int MaxRepairs = 3;
    public const string Task = "version-remix";

    /// <summary>What a remix may change. Everything else is shared with the story guests already know.</summary>
    private static readonly HashSet<string> AllowedKeys = ["solution", "characters", "clues", "addClues", "acts", "finale"];
    private static readonly HashSet<string> AllowedCharacterKeys = ["private", "required"];
    private static readonly HashSet<string> AllowedActKeys = ["cues", "prompts"];

    /// <param name="variant">The version's short name, e.g. "ai3f9c2a"; its id becomes "&lt;story&gt;--ai3f9c2a".</param>
    public async Task<GenerationResult> RemixAsync(Scenario original, string killerId, string variant, AiCallContext context,
        IProgress<string>? progress, CancellationToken ct)
    {
        var killer = original.FindCharacter(killerId) ?? throw new ArgumentException($"Unknown character '{killerId}'.", nameof(killerId));
        var warnings = new List<string>();
        var conversation = new List<ChatMessage> { new(ChatRole.User, Instructions(original, killer)) };
        var solverRetried = false;

        progress?.Report("The Storyteller is rewriting tonight's mystery for your cast…");
        for (var attempt = 0; attempt <= MaxRepairs; attempt++)
        {
            var text = await ai.CompleteAsync(AiRole.Storyteller, System(original), conversation,
                context with { Purpose = attempt == 0 ? "remix-version" : "repair-version" }, maxOutputTokens: 16_000, jsonOutput: true, ct);

            var (version, errors) = Check(original, killerId, variant, text);
            if (version is not null && errors.Count == 0)
            {
                progress?.Report("The Inspector is testing whether the new version can be solved…");
                var solved = await BlindSolver.SolvesAsync(ai, version, context, ct);
                if (solved || solverRetried)
                {
                    if (!solved) warnings.Add("The Inspector found this version very hard to solve from the clues alone.");
                    return new GenerationResult(version, warnings);
                }
                solverRetried = true;
                errors.Add($"A detective reading only the public clues could not identify '{killerId}'. " +
                           "Add or sharpen at least two genuine clues that point to them, without making it obvious in act 1.");
            }

            if (attempt == MaxRepairs) break;
            progress?.Report($"Fixing {errors.Count} problem{(errors.Count == 1 ? "" : "s")} in the new version (attempt {attempt + 2})…");
            conversation.Add(new ChatMessage(ChatRole.Assistant, text.Length > 40_000 ? text[..40_000] : text));
            conversation.Add(new ChatMessage(ChatRole.User,
                "Your patch has these problems:\n- " + string.Join("\n- ", errors.Take(25)) + "\n\nReturn the complete corrected patch JSON only."));
        }

        throw new AiCallFailedException("The Storyteller couldn't rewrite this mystery for your cast this time.");
    }

    /// <summary>Parses the patch, rejects changes to anything guests have already seen, applies it and validates the result.</summary>
    public static (Scenario? Version, List<string> Errors) Check(Scenario original, string killerId, string variant, string text)
    {
        var errors = new List<string>();
        JsonObject patch;
        try
        {
            patch = JsonNode.Parse(JsonExtraction.JsonPart(text)) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (Exception ex) when (ex is JsonException or AiCallFailedException)
        {
            return (null, ["Reply with a single JSON object: the patch."]);
        }

        foreach (var (key, _) in patch)
        {
            if (!AllowedKeys.Contains(key) && key is not ("variantOf" or "variant"))
                errors.Add($"Don't change \"{key}\". Guests already know the setting, the victim, the cast and the prologue; " +
                           $"only {string.Join(", ", AllowedKeys)} may change.");
        }
        if (patch["characters"] is JsonObject characters)
        {
            foreach (var (id, change) in characters)
            {
                foreach (var (key, _) in change as JsonObject ?? [])
                {
                    if (!AllowedCharacterKeys.Contains(key))
                        errors.Add($"Don't change character '{id}'s \"{key}\": guests have already read it. Change only \"private\" (and \"required\").");
                }
            }
        }
        if (patch["acts"] is JsonObject acts)
        {
            foreach (var (id, change) in acts)
            {
                foreach (var (key, _) in change as JsonObject ?? [])
                {
                    if (!AllowedActKeys.Contains(key)) errors.Add($"In act '{id}' change only \"cues\" and \"prompts\", not \"{key}\".");
                }
            }
        }
        if (errors.Count > 0) return (null, errors);

        patch["variantOf"] = original.Id;
        patch["variant"] = variant;
        Scenario version;
        try
        {
            version = ScenarioVariants.Apply(original, patch);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            return (null, [ex.Message]);
        }

        errors.AddRange(ScenarioValidator.Validate(version));
        if (version.Solution.MurdererId != killerId)
            errors.Add($"The solution's murdererId must be \"{killerId}\", not \"{version.Solution.MurdererId}\".");
        return (version, errors);
    }

    private static string System(Scenario s) => $"""
        You are an award-winning writer of murder-mystery party games, like the boxed kits people play at dinner parties.
        You are writing a new version of an existing mystery: the same place, victim and suspects, but a different killer,
        so a group can play the story again without knowing the answer.
        {ContentGuidance.For(s.ContentRating)}
        """;

    private static string Instructions(Scenario original, Character killer)
    {
        var oldKiller = original.FindCharacter(original.Solution.MurdererId);
        return $$"""
            TASK: {{Task}}
            TARGET: {{killer.Id}}
            Rewrite this mystery so that {{killer.Name}} (id "{{killer.Id}}") is the murderer instead of {{oldKiller?.Name ?? original.Solution.MurdererId}}.
            The guests have already chosen characters and read the public bios, so everything they have seen must stay the same.

            Reply with a PATCH, a JSON object holding only what changes:
            {
              "solution": { ...the complete new solution: murdererId "{{killer.Id}}", motiveId, methodId, explanation, timeline... },
              "characters": { "<character id>": { "private": { ...fields to change... }, "required": true } },
              "clues": { "<clue id>": { ...fields to change... }, "<clue id to remove>": null },
              "addClues": [ { ...a complete new clue... } ],
              "acts": { "<act id>": { "cues": [ ...the act's complete new cue list... ] } },
              "finale": [ ...the complete new finale cues... ]
            }
            Rules:
            - Allowed top-level keys: solution, characters, clues, addClues, acts, finale. Never change the setting, victim,
              title, prologue, or any character's name, title, public bio or costume.
            - Characters, clues and acts are keyed by their existing ids. Objects merge field by field; a list (secrets,
              lines, pointsTo, cues) replaces the original's whole list; null removes a clue.
            - {{killer.Name}}: rewrite their "private" sheet. The backstory starts with "YOU ARE THE MURDERER." and explains
              exactly what they did, when and how. Give them a motive, a false alibi, secrets and lines for each act.
              {{(killer.Required ? "" : "Also set \"required\": true for them.")}}
            - {{oldKiller?.Name ?? "The old killer"}} is now innocent: rewrite their backstory (no "YOU ARE THE MURDERER"), but leave
              them something that looks suspicious.
            - Clues must be FAIR: at least {{ScenarioValidator.MinCluesAgainstMurderer}} genuine clues ("redHerring": false) point to "{{killer.Id}}",
              spread over the acts, with the decisive one in the final act. Clues that pointed to the old killer must be
              rewritten, re-aimed or turned into red herrings. Keep suspicion on at least 2 innocent suspects.
            - Use only existing motive and method ids from "accusation" for the solution's motiveId and methodId.
            - Rewrite any cue, line or finale narration that names or implies the old killer.
            - New ids must be unique. Reply with the JSON patch only, no commentary.

            ORIGINAL STORY (JSON):
            {{GameJson.Serialize(original)}}
            END OF ORIGINAL STORY
            """;
    }
}
