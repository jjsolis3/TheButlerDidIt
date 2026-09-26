using System.Text;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Ai.Prompts;

/// <summary>Prompts for the Inspector: hints during play, and verdicts at the reveal.</summary>
public static class InspectorPrompts
{
    public const string HintTask = "inspector-hint";
    public const string VerdictTask = "inspector-verdict";

    /// <summary>
    /// A hint for one player. The Inspector sees exactly that player's view (built
    /// by ViewProjector, so it holds nothing they aren't allowed to see) plus the
    /// solution, so it can steer them in the right direction. It must not name the
    /// killer, and <see cref="HintSafety"/> double-checks the reply.
    /// </summary>
    public static string Hint(Scenario scenario, PlayerView view, ContentRating level, Tone tone = Tone.Standard)
    {
        var murderer = scenario.FindCharacter(scenario.Solution.MurdererId)!;
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {HintTask}");
        sb.AppendLine($"You are Inspector Graves, a dry-witted detective helping a guest at a murder-mystery party (\"{scenario.Title}\").");
        sb.AppendLine("The guest is stuck and asked for a hint. Below is everything THEY can currently see, then the confidential solution.");
        sb.AppendLine();
        sb.AppendLine("## What the guest can see (JSON)");
        sb.AppendLine(GameJson.Serialize(new
        {
            view.Stage.Scenario,
            view.Stage.ActNumber,
            view.Stage.Cast,
            view.Stage.RevealedSecrets,
            view.Stage.Interrogations,
            Clues = view.MyClues,
            PlayingAs = view.Dossier?.Character.Name,
            PreviousHints = view.MyHints.Select(h => h.Text),
        }));
        sb.AppendLine();
        sb.AppendLine("## Confidential solution (NEVER reveal it)");
        sb.AppendLine($"Murderer: {murderer.Name}. Motive: {scenario.Solution.MotiveId}. Method: {scenario.Solution.MethodId}.");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine($"- Never write the murderer's name or any part of it ({murderer.Name}), and never say who did it, why, or how.");
        sb.AppendLine("- Point the guest at ONE clue they already have, or a question worth asking someone, that moves them closer to the truth.");
        sb.AppendLine("- If the guest is playing the murderer, suggest how to deflect suspicion instead.");
        sb.AppendLine("- One or two sentences, in character, under 50 words. No lists or markdown.");
        sb.AppendLine(ContentGuidance.For(level, tone));
        return sb.ToString();
    }

    public const string HintFallback = "Inspector Graves taps his nose: \"Go back over the clues you already hold. One of them doesn't fit the story you've been told.\"";

    /// <summary>
    /// Verdicts at the reveal, when the solution is public anyway. Players are
    /// referred to by number rather than seat id, because models copy short
    /// numbers far more reliably than GUIDs.
    /// </summary>
    public static (string System, IReadOnlyList<Guid> SeatOrder) Verdicts(Scenario scenario, GameState state, ContentRating level, Tone tone = Tone.Standard)
    {
        var solution = scenario.Solution;
        var seats = state.Players.Select(p => p.SeatId).ToList();
        string Option(List<Option> options, string id) => options.FirstOrDefault(o => o.Id == id)?.Text ?? id;

        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {VerdictTask}");
        sb.AppendLine($"You are Inspector Graves delivering the closing verdicts at a murder-mystery party (\"{scenario.Title}\").");
        sb.AppendLine($"The truth: {scenario.FindCharacter(solution.MurdererId)?.Name} did it. Motive: {Option(scenario.Accusation.Motives, solution.MotiveId)}. " +
                      $"Method: {Option(scenario.Accusation.Methods, solution.MethodId)}.");
        sb.AppendLine(string.Join(" ", solution.Explanation));
        sb.AppendLine();
        sb.AppendLine("Each guest's accusation:");
        for (var i = 0; i < seats.Count; i++)
        {
            var player = state.Players[i];
            var character = player.CharacterId is { } cid ? scenario.FindCharacter(cid)?.Name : null;
            var line = state.Accusations.TryGetValue(player.SeatId, out var a)
                ? $"accused {scenario.FindCharacter(a.SuspectId)?.Name}, motive \"{Option(scenario.Accusation.Motives, a.MotiveId)}\", method \"{Option(scenario.Accusation.Methods, a.MethodId)}\""
                : "made no accusation";
            sb.AppendLine($"{i + 1}. {player.Name}{(character is null ? "" : $" (playing {character})")} {line}.");
        }
        sb.AppendLine();
        sb.AppendLine("For every guest, write one witty sentence (under 35 words) reacting to their accusation: praise sharp deductions, gently tease wild guesses.");
        sb.AppendLine(ContentGuidance.For(level, tone));
        sb.AppendLine("Reply with JSON only, in this shape: {\"verdicts\":[{\"player\":1,\"text\":\"...\"}]}");
        return (sb.ToString(), seats);
    }

    public sealed class VerdictReply
    {
        public List<VerdictItem> Verdicts { get; set; } = [];
    }

    public sealed class VerdictItem
    {
        public int Player { get; set; }
        public string Text { get; set; } = "";
    }
}

public static class HintSafety
{
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "lord", "lady", "mr", "mrs", "miss", "ms", "dr", "sir", "madame", "captain", "the", "of", "and",
    };

    /// <summary>True if the text names the character: full name or any distinctive part of it (first name, surname).</summary>
    public static bool MentionsCharacter(string text, Character character)
    {
        var parts = character.Name.Split([' ', '.', ',', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 3 && !Titles.Contains(p));
        return parts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(p)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}
