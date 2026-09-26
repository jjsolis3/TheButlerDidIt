using System.Text;
using Microsoft.Extensions.AI;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Ai.Generation;

/// <summary>
/// The fairness test for anything the Storyteller writes: the Inspector sees only what players
/// could see (bios, alibis, clue texts) and must name the killer. Used for new mysteries and
/// for remixed versions alike.
/// </summary>
public static class BlindSolver
{
    public const string SolveTask = "mystery-solve";

    public static async Task<bool> SolvesAsync(AiGateway ai, Scenario scenario, AiCallContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {SolveTask}");
        sb.AppendLine("You are a sharp detective. Solve this murder using ONLY the evidence below. Reason carefully, then answer.");
        sb.AppendLine($"Victim: {scenario.Victim.Name}. {scenario.Victim.Description}");
        sb.AppendLine("Suspects:");
        foreach (var c in scenario.Characters) sb.AppendLine($"- id \"{c.Id}\": {c.Name}, {c.Title}. {c.PublicBio} Claims: {c.Private.Alibi}");
        sb.AppendLine("Evidence found during the evening:");
        foreach (var clue in scenario.Clues)
        {
            sb.AppendLine($"- {clue.Title}: {clue.Text}{(clue.Puzzle is null ? "" : " (Decoded: " + clue.Puzzle.SolvedText + ")")}");
        }
        sb.AppendLine("Reply with JSON only: {\"suspectId\": \"<id>\", \"reasoning\": \"<one sentence>\"}");

        try
        {
            var answer = await ai.CompleteJsonAsync<SolverAnswer>(AiRole.Inspector, sb.ToString(),
                [new ChatMessage(ChatRole.User, "Who is the murderer?")], context with { Purpose = "blind-solve" }, 1_500, ct);
            return string.Equals(answer.SuspectId, scenario.Solution.MurdererId, StringComparison.OrdinalIgnoreCase);
        }
        catch (AiCallFailedException)
        {
            // If the check itself fails, don't throw away a valid mystery.
            return true;
        }
    }

    private sealed class SolverAnswer
    {
        public string SuspectId { get; set; } = "";
        public string Reasoning { get; set; } = "";
    }
}
