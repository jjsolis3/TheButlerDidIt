using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Engine;

public sealed record ScoreLine(Guid SeatId, string PlayerName, string? CharacterName, int Points, IReadOnlyList<string> Breakdown);

public sealed record AwardResult(string AwardId, string Title, IReadOnlyList<string> Winners, int Votes);

public static class Scoring
{
    public const int CorrectSuspect = 3;
    public const int CorrectMotive = 1;
    public const int CorrectMethod = 1;
    public const int PuzzleSolved = 1;
    public const int EscapedSuspicion = 1;

    /// <summary>Scores, highest first. The top detective earns the "Best Detective" title.</summary>
    public static IReadOnlyList<ScoreLine> Compute(GameState s, Scenario scenario)
    {
        var solution = scenario.Solution;
        var murdererSeat = s.PlayerFor(solution.MurdererId)?.SeatId;
        var lines = new List<ScoreLine>();

        foreach (var player in s.Players)
        {
            var points = 0;
            var why = new List<string>();
            var character = player.CharacterId is { } id ? scenario.FindCharacter(id) : null;

            if (player.SeatId == murdererSeat)
            {
                // The killer scores by fooling people: +1 for every guest who accused someone else.
                var fooled = s.Players.Count(p =>
                    p.SeatId != murdererSeat &&
                    (!s.Accusations.TryGetValue(p.SeatId, out var a) || a.SuspectId != solution.MurdererId));
                if (fooled > 0)
                {
                    points += fooled * EscapedSuspicion;
                    why.Add($"Fooled {fooled} guest{(fooled == 1 ? "" : "s")} (+{fooled * EscapedSuspicion})");
                }
            }
            else if (s.Accusations.TryGetValue(player.SeatId, out var accusation))
            {
                if (accusation.SuspectId == solution.MurdererId) { points += CorrectSuspect; why.Add($"Named the killer (+{CorrectSuspect})"); }
                if (accusation.MotiveId == solution.MotiveId) { points += CorrectMotive; why.Add($"Right motive (+{CorrectMotive})"); }
                if (accusation.MethodId == solution.MethodId) { points += CorrectMethod; why.Add($"Right method (+{CorrectMethod})"); }
            }

            var puzzles = s.SolvedPuzzles.Count(p => p.SolvedBySeatId == player.SeatId);
            if (puzzles > 0)
            {
                points += puzzles * PuzzleSolved;
                why.Add($"Cracked {puzzles} puzzle{(puzzles == 1 ? "" : "s")} (+{puzzles * PuzzleSolved})");
            }

            lines.Add(new ScoreLine(player.SeatId, player.Name, character?.Name, points, why));
        }

        return lines.OrderByDescending(l => l.Points).ThenBy(l => l.PlayerName).ToList();
    }

    public static IReadOnlyList<AwardResult> TallyAwards(GameState s)
    {
        var results = new List<AwardResult>();
        foreach (var (awardId, title) in GameEngine.Awards)
        {
            var counts = s.AwardVotes.Values
                .Where(v => v.ContainsKey(awardId))
                .GroupBy(v => v[awardId])
                .Select(g => (SeatId: g.Key, Votes: g.Count()))
                .ToList();
            if (counts.Count == 0)
            {
                results.Add(new AwardResult(awardId, title, [], 0));
                continue;
            }
            var top = counts.Max(c => c.Votes);
            var winners = counts.Where(c => c.Votes == top)
                .Select(c => s.FindPlayer(c.SeatId)?.Name)
                .OfType<string>()
                .OrderBy(n => n)
                .ToList();
            results.Add(new AwardResult(awardId, title, winners, top));
        }
        return results;
    }
}
