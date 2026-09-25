using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Engine;

/// <summary>
/// Turns the full game state into what one particular screen is allowed to see.
///
/// This is the most security-sensitive code in the project. Browsers are not
/// trusted: anything we send can be read in dev tools. So instead of sending
/// everything and hiding it in the UI, we build each view from scratch here and
/// copy in only permitted fields. The tests in ViewProjectorTests check that no
/// secret, private clue or solution text ever appears where it shouldn't.
/// </summary>
public static class ViewProjector
{
    public static StageView Stage(GameState s, Scenario scenario, DateTimeOffset now)
    {
        var act = s.Phase == Phase.Act ? scenario.Acts[s.ActIndex] : null;
        var cast = BuildCast(s, scenario);

        return new StageView(
            Version: s.Version,
            Phase: s.Phase,
            Scenario: Summary(scenario),
            ActNumber: s.Phase == Phase.Act ? s.ActIndex + 1 : 0,
            ActCount: scenario.Acts.Count,
            ActTitle: act?.Title,
            ActStep: s.ActStep,
            Timer: s.Phase == Phase.Act && s.ActStep == ActStep.Mingle ? Timer(s.Timer, now) : null,
            Cues: CurrentCues(s, scenario, act),
            Prompts: act is not null && s.ActStep == ActStep.Mingle ? act.Prompts : [],
            Cast: cast,
            Players: s.Players.Select(p => new PlayerSummary(
                p.SeatId, p.Name, p.CharacterId, p.IsHost, p.IsLocal, p.Ready, s.Accusations.ContainsKey(p.SeatId))).ToList(),
            Clues: s.DroppedClues
                .Where(d => d.RecipientSeatId is null || d.SharedPublicly)
                .Select(d => ClueFor(s, scenario, d))
                .ToList(),
            RevealedSecrets: s.RevealedSecrets.Select(r =>
            {
                var character = scenario.FindCharacter(r.CharacterId)!;
                var secret = character.Private.Secrets.First(x => x.Id == r.SecretId);
                return new SecretView(character.Id, character.Name, secret.Text);
            }).ToList(),
            PendingClues: s.PendingClueIds.Count,
            Feed: s.Feed.TakeLast(12).ToList(),
            Accusation: s.Phase == Phase.Accusation ? new AccusationProgress(s.Accusations.Count, s.Players.Count) : null,
            Reveal: s.Phase is Phase.Reveal or Phase.Awards or Phase.Finished ? BuildReveal(s, scenario) : null,
            Awards: s.Phase is Phase.Awards or Phase.Finished ? BuildAwards(s, scenario) : null);
    }

    public static PlayerView Player(GameState s, Scenario scenario, Guid seatId, DateTimeOffset now)
    {
        var player = s.FindPlayer(seatId) ?? throw new GameRuleException("You are not seated at this party.");
        var stage = Stage(s, scenario, now);
        var character = player.CharacterId is { } id ? scenario.FindCharacter(id) : null;

        return new PlayerView(
            Version: s.Version,
            SeatId: player.SeatId,
            Name: player.Name,
            IsHost: player.IsHost,
            IsLocal: player.IsLocal,
            Ready: player.Ready,
            Dossier: character is null ? null : BuildDossier(s, scenario, character),
            // Everything this seat can see: public clues plus its own private ones.
            MyClues: s.DroppedClues
                .Where(d => d.RecipientSeatId is null || d.SharedPublicly || d.RecipientSeatId == seatId)
                .Select(d => ClueFor(s, scenario, d))
                .ToList(),
            AccusationForm: s.Phase == Phase.Accusation
                ? new AccusationForm(
                    GameEngine.CharactersInPlay(s, scenario).Select(c => new Option { Id = c.Id, Text = c.Name }).ToList(),
                    scenario.Accusation.Motives,
                    scenario.Accusation.Methods,
                    s.Accusations.GetValueOrDefault(seatId))
                : null,
            AwardBallot: s.Phase == Phase.Awards
                ? new AwardBallot(
                    GameEngine.Awards.Select(a => new AwardOption(a.Id, a.Title)).ToList(),
                    s.Players.Where(p => p.SeatId != seatId)
                        .Select(p => new Option { Id = p.SeatId.ToString(), Text = NameWithCharacter(p, scenario) })
                        .ToList(),
                    s.AwardVotes.GetValueOrDefault(seatId) ?? new Dictionary<string, Guid>())
                : null,
            Stage: stage);
    }

    // ------------------------------------------------------------------ pieces

    private static ScenarioSummary Summary(Scenario s) => new(
        s.Id, s.ThemeSlug, s.Title, s.Synopsis, s.Setting.Place, s.Setting.Era, s.Setting.Description, s.Setting.Image,
        s.Victim.Name, s.Victim.Description, s.Victim.Portrait, s.MinPlayers, s.MaxPlayers);

    private static TimerView Timer(Timer t, DateTimeOffset now) => new(
        now, t.EndsAt, t.IsPaused, t.PausedRemaining is { } r ? (int)Math.Ceiling(r.TotalSeconds) : null);

    private static List<CastMember> BuildCast(GameState s, Scenario scenario)
    {
        // In the lobby everyone sees every character so they can choose.
        // Once the game starts, only characters actually at the party are listed.
        var characters = s.Phase == Phase.Lobby ? scenario.Characters : GameEngine.CharactersInPlay(s, scenario);
        return characters.Select(c => ToCastMember(s, c)).ToList();
    }

    private static CastMember ToCastMember(GameState s, Character c) => new(
        c.Id, c.Name, c.Title, c.Pronouns, c.PublicBio, c.CostumeTips, c.Portrait, c.Required,
        s.PlayerFor(c.Id)?.Name, s.NpcCharacterIds.Contains(c.Id), c.Voice);

    /// <summary>The cinematic the stage should be playing right now, if any.</summary>
    private static List<CueView> CurrentCues(GameState s, Scenario scenario, Act? act)
    {
        IEnumerable<Cue> cues = s.Phase switch
        {
            Phase.Prologue => scenario.Prologue,
            Phase.Act when s.ActStep == ActStep.Cinematic && act is not null => ActCinematic(s, scenario, act),
            Phase.Reveal when s.RevealStep == GameEngine.RevealStepCount(scenario) - 1 => scenario.Finale,
            _ => [],
        };

        // Line cues are only voiced on stage when the speaker is an NPC. When a
        // guest plays that character, the line is on their phone to perform.
        return cues
            .Where(c => c.Type != CueType.Line || (c.Speaker is not null && s.NpcCharacterIds.Contains(c.Speaker)))
            .Select(c =>
            {
                var speaker = c.Speaker is null ? null : scenario.FindCharacter(c.Speaker);
                return new CueView(c.Type, c.Text, c.Src, c.Speaker, speaker?.Name, c.Effect, speaker?.Voice);
            })
            .ToList();
    }

    private static IEnumerable<Cue> ActCinematic(GameState s, Scenario scenario, Act act)
    {
        foreach (var cue in act.Cues) yield return cue;

        // NPCs deliver their scripted lines for this act at the end of the cinematic.
        foreach (var npcId in s.NpcCharacterIds)
        {
            var npc = scenario.FindCharacter(npcId);
            if (npc is null || !npc.Private.Lines.TryGetValue(act.Id, out var lines)) continue;
            foreach (var line in lines)
            {
                yield return new Cue { Type = CueType.Line, Speaker = npcId, Text = line };
            }
        }
    }

    private static ClueView ClueFor(GameState s, Scenario scenario, DroppedClue d)
    {
        var clue = scenario.FindClue(d.ClueId)!;
        var solved = s.SolvedPuzzles.FirstOrDefault(p => p.ClueId == clue.Id);

        // "Found among X's belongings" for private clues that went public because nobody plays X.
        string? foundAmong = null;
        if (clue.Visibility == ClueVisibility.Private && d.RecipientSeatId is null && clue.Recipient is not null)
        {
            foundAmong = scenario.FindCharacter(clue.Recipient)?.Name;
        }

        return new ClueView(
            clue.Id, clue.Title, clue.Text, clue.Image, clue.Act,
            IsPrivate: d.RecipientSeatId is not null,
            SharedPublicly: d.SharedPublicly,
            FoundAmong: foundAmong,
            // Deliberately NOT copied: clue.PointsTo, clue.RedHerring, puzzle answers.
            Puzzle: clue.Puzzle is null ? null : new PuzzleView(
                clue.Puzzle.Prompt,
                clue.Puzzle.Hint,
                solved is not null,
                solved is null ? null : s.FindPlayer(solved.SolvedBySeatId)?.Name,
                solved is null ? null : clue.Puzzle.SolvedText));
    }

    private static Dossier BuildDossier(GameState s, Scenario scenario, Character c)
    {
        var started = s.Phase != Phase.Lobby;
        var cast = ToCastMember(s, c);
        if (!started)
        {
            // Before the party: like a printed invitation, only the public bio and costume tips.
            return new Dossier(cast, false, false, null, null, [], [], [], c.Private.Secrets.Count, []);
        }

        var actNumber = GameEngine.CurrentActNumber(s);
        var unlocked = c.Private.Secrets.Where(x => x.UnlockAct <= actNumber).ToList();
        var lines = s.Phase == Phase.Act && c.Private.Lines.TryGetValue(scenario.Acts[s.ActIndex].Id, out var l) ? l : [];

        return new Dossier(
            cast,
            Unlocked: true,
            IsMurderer: scenario.MurdererKnows && scenario.Solution.MurdererId == c.Id,
            c.Private.Backstory,
            c.Private.Alibi,
            c.Private.Objectives,
            c.Private.Knows,
            unlocked.Select(x => new DossierSecret(x.Id, x.Text, s.RevealedSecrets.Any(r => r.SecretId == x.Id))).ToList(),
            LockedSecrets: c.Private.Secrets.Count - unlocked.Count,
            LinesThisAct: lines);
    }

    private static RevealView BuildReveal(GameState s, Scenario scenario)
    {
        var solution = scenario.Solution;
        var stepCount = GameEngine.RevealStepCount(scenario);
        // After the reveal phase everything is shown.
        var step = s.Phase == Phase.Reveal ? s.RevealStep : stepCount - 1;
        var unmasked = step >= 1;
        var final = step == stepCount - 1;

        string? OptionText(List<Option> options, string id) => options.FirstOrDefault(o => o.Id == id)?.Text;

        var guesses = s.Players.Select(p =>
        {
            var character = p.CharacterId is { } id ? scenario.FindCharacter(id)?.Name : null;
            if (!s.Accusations.TryGetValue(p.SeatId, out var a))
                return new GuessView(p.Name, character, null, null, null, null);
            return new GuessView(
                p.Name, character,
                scenario.FindCharacter(a.SuspectId)?.Name,
                OptionText(scenario.Accusation.Motives, a.MotiveId),
                OptionText(scenario.Accusation.Methods, a.MethodId),
                unmasked ? a.SuspectId == solution.MurdererId : null);
        }).ToList();

        // Only copy solution fields in once the reveal has reached them.
        return new RevealView(
            step,
            stepCount,
            guesses,
            unmasked ? solution.MurdererId : null,
            unmasked ? scenario.FindCharacter(solution.MurdererId)?.Name : null,
            unmasked ? OptionText(scenario.Accusation.Motives, solution.MotiveId) : null,
            unmasked ? OptionText(scenario.Accusation.Methods, solution.MethodId) : null,
            solution.Explanation.Take(Math.Max(0, step - 1)).ToList(),
            final ? solution.Timeline : [],
            final ? Scoring.Compute(s, scenario) : []);
    }

    private static AwardsView BuildAwards(GameState s, Scenario scenario)
    {
        var finished = s.Phase == Phase.Finished;
        var scores = Scoring.Compute(s, scenario);
        var murdererSeat = s.PlayerFor(scenario.Solution.MurdererId)?.SeatId;
        var detective = scores.FirstOrDefault(x => x.SeatId != murdererSeat && x.Points > 0);
        return new AwardsView(
            GameEngine.Awards.Select(a => new AwardOption(a.Id, a.Title)).ToList(),
            VotesCast: s.AwardVotes.Count,
            Voters: s.Players.Count,
            // Votes stay hidden until the host closes voting, so nobody can campaign.
            Results: finished ? Scoring.TallyAwards(s) : null,
            BestDetective: finished ? detective : null);
    }

    private static string NameWithCharacter(PlayerState p, Scenario scenario) =>
        p.CharacterId is { } id && scenario.FindCharacter(id) is { } c ? $"{p.Name} ({c.Name})" : p.Name;
}
