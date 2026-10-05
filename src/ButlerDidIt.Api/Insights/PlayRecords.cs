using ButlerDidIt.Api.Data;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Api.Insights;

/// <summary>The breakdown in <see cref="PlayRecord.Details"/>.</summary>
/// <param name="Accused">Mysteries: how many guests accused each character (by id).</param>
/// <param name="Puzzles">Escape rooms: every puzzle the game played, in order.</param>
public sealed record PlayDetails(Dictionary<string, int>? Accused = null, List<PuzzleTime>? Puzzles = null);

/// <summary>
/// One escape puzzle in one game. <paramref name="Seconds"/> runs from its stage opening to its solving (the time it
/// stood between the group and the next door); null when it was never solved. <paramref name="Stuck"/>: the clock
/// ran out with it unsolved in front of the group.
/// </summary>
public sealed record PuzzleTime(string PuzzleId, string StageId, bool Solved, int? Seconds, int Hints, bool Stuck);

/// <summary>
/// Builds the record of a finished game (#130). Pure, apart from the clock the caller passes, so the tests can check it
/// directly. Everything in it is what the reveal or the ending already showed the table.
/// </summary>
public static class PlayRecords
{
    /// <summary>A mystery at its reveal: who accused whom, and how many named the killer.</summary>
    public static PlayRecord ForMystery(GameState s, Scenario scenario, Party party)
    {
        var accused = s.Accusations.Values.GroupBy(a => a.SuspectId).ToDictionary(g => g.Key, g => g.Count());
        return new PlayRecord
        {
            PartyId = party.Id,
            Kind = GameKind.Mystery,
            ContentId = party.ScenarioId,
            HostUserId = party.HostUserId,
            FinishedAt = party.UpdatedAt,
            PlayerCount = s.Players.Count,
            DurationSeconds = s.StartedAt is { } started ? Math.Max(0, (int)(party.UpdatedAt - started).TotalSeconds) : 0,
            Accusers = s.Accusations.Count,
            Correct = s.Accusations.Values.Count(a => a.SuspectId == scenario.Solution.MurdererId),
            Details = GameJson.Serialize(new PlayDetails(Accused: accused)),
        };
    }

    /// <summary>An escape game when the clock stops: escaped or not, and how long each puzzle held the group up.</summary>
    public static PlayRecord ForEscape(EscapeState s, EscapeRoom template, Party party)
    {
        var played = EscapeEngine.RoomFor(s, template);
        var solvedAt = s.Solved.ToDictionary(x => x.PuzzleId, x => x.At);
        var puzzles = new List<PuzzleTime>();
        var opened = s.StartedAt!.Value;
        for (var i = 0; i < played.Stages.Count; i++)
        {
            var stage = played.Stages[i];
            foreach (var id in stage.Puzzles)
            {
                var solved = solvedAt.TryGetValue(id, out var at);
                puzzles.Add(new PuzzleTime(id, stage.Id, solved, solved ? Math.Max(0, (int)(at - opened).TotalSeconds) : null,
                    s.HintsShown.GetValueOrDefault(id), Stuck: !solved && s.Phase == EscapePhase.Failed && i == s.StageIndex));
            }
            // The next stage opened when this one's last puzzle was solved. A stage never reached ends the list.
            var times = stage.Puzzles.Where(solvedAt.ContainsKey).Select(id => solvedAt[id]).ToList();
            if (times.Count < stage.Puzzles.Count) break;
            if (times.Count > 0) opened = times.Max();
        }
        return new PlayRecord
        {
            PartyId = party.Id,
            Kind = GameKind.EscapeRoom,
            ContentId = template.Id,
            HostUserId = party.HostUserId,
            FinishedAt = s.EndedAt ?? party.UpdatedAt,
            PlayerCount = s.Players.Count,
            DurationSeconds = EscapeEngine.ElapsedSeconds(s),
            Escaped = s.Phase == EscapePhase.Escaped,
            Difficulty = s.Level,
            Minutes = s.Minutes ?? template.TimeLimitMinutes,
            HintsUsed = s.HintsUsed,
            Details = GameJson.Serialize(new PlayDetails(Puzzles: puzzles)),
        };
    }
}
