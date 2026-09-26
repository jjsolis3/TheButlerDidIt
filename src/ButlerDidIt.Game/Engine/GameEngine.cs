using System.Text.RegularExpressions;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Engine;

/// <summary>
/// The rules of the game, written as a pure function:
///
///     newState = GameEngine.Apply(oldState, scenario, command)
///
/// "Pure" means it has no hidden inputs (no clock, no database, no random seed it
/// picks itself) and no side effects. That gives three benefits:
///   1. Easy tests: build a state, apply a command, check the result.
///   2. Safe saving: the API saves the new state only if Apply succeeds, so a
///      rule violation can never leave a half-updated game in the database.
///   3. Reuse: the AI generator in milestone 2 can simulate a game without a server.
/// </summary>
public static partial class GameEngine
{
    public static readonly IReadOnlyList<(string Id, string Title)> Awards =
    [
        ("performance", "Best Performance"),
        ("costume", "Best Costume"),
    ];

    private const int MaxFeedItems = 40;

    public static GameState NewGame() => new();

    public static GameState Apply(GameState current, Scenario scenario, Command command)
    {
        // Work on a deep copy so the caller's state is untouched if a rule check
        // throws halfway through. A JSON round-trip is the simplest reliable deep
        // copy, and the state is small (a few KB), so the cost is negligible.
        var s = Clone(current);

        // While the AI tailors the mystery to tonight's cast, the cast must not change under it.
        if (s.Tailoring && command is Engine.AddPlayer or RemovePlayer or Engine.ChooseCharacter or AutoAssignCharacters)
            throw new GameRuleException("The host is getting the evening ready. Hold on a moment!");

        switch (command)
        {
            case AddPlayer c: AddPlayer(s, scenario, c); break;
            case RemovePlayer c: RequirePhase(s, Phase.Lobby); s.Players.Remove(RequirePlayer(s, c.SeatId)); break;
            case ChooseCharacter c: ChooseCharacter(s, scenario, c); break;
            case SetReady c: RequirePlayer(s, c.SeatId).Ready = c.Ready; break;
            case AutoAssignCharacters c: RequirePhase(s, Phase.Lobby); AutoAssign(s, scenario, c.Now); break;
            case StartGame c: StartGame(s, scenario, c); break;
            case Advance c: Advance(s, scenario, c.Now); break;
            case DropNextClue c: DropNextClue(s, scenario, c.Now); break;
            case PauseTimer c: PauseTimer(s, c.Now); break;
            case ResumeTimer c: ResumeTimer(s, c.Now); break;
            case ExtendTimer c: ExtendTimer(s, c); break;
            case ConvertToNpc c: ConvertToNpc(s, scenario, c); break;
            case Tick c:
                // A tick with nothing due returns the original object so the
                // caller can skip saving and broadcasting entirely.
                if (!TickDue(s, c.Now)) return current;
                DropAllPending(s, scenario, c.Now);
                break;
            case RevealSecret c: RevealSecret(s, scenario, c); break;
            case ShareClue c: ShareClue(s, scenario, c); break;
            case SolvePuzzle c: SolvePuzzle(s, scenario, c); break;
            case SubmitAccusation c: SubmitAccusation(s, scenario, c); break;
            case CastAwardVote c: CastAwardVote(s, c); break;
            case SetAiFeatures c: s.Ai = c.Features; break;
            case BeginNpcQuestion c: BeginNpcQuestion(s, scenario, c); break;
            case CompleteNpcQuestion c: RequireInterrogation(s, c.Id).Answer = Clip(c.Answer, 2000); break;
            case CancelNpcQuestion c: s.Interrogations.RemoveAll(i => i.Id == c.Id && i.Answer is null); break;
            case BeginHint c: BeginHint(s, c); break;
            case CompleteHint c: RequireHint(s, c.Id).Text = Clip(c.Text, 1000); break;
            case CancelHint c: s.Hints.RemoveAll(h => h.Id == c.Id && h.Text is null); break;
            case SetVerdicts c: SetVerdicts(s, c); break;
            case BeginTailoring: RequirePhase(s, Phase.Lobby); s.Tailoring = true; break;
            case CancelTailoring: s.Tailoring = false; break;
            case SetPartyOptions c: RequirePhase(s, Phase.Lobby); s.Options = c.Options; break;
            case SetSpotlight c: SetSpotlight(s, scenario, c); break;
            case SpinSpotlight c: SpinSpotlight(s, scenario, c.Now); break;
            case Confront c: Confront(s, scenario, c); break;
            case SetSuspicion c: SetSuspicion(s, scenario, c); break;
            case SetPlayerPhoto c: RequirePlayer(s, c.SeatId).PhotoUrl = c.PhotoUrl; break;
            case SetInterrogationAudio c: RequireInterrogation(s, c.Id).AudioUrl = c.AudioUrl; break;
            default: throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Unknown command.");
        }

        s.Version++;
        return s;
    }

    /// <summary>When the server's ticker should next look at this game (null = nothing scheduled).</summary>
    public static DateTimeOffset? NextDueAt(GameState s) =>
        s.Phase == Phase.Act && s.ActStep == ActStep.Mingle && !s.Timer.IsPaused && s.PendingClueIds.Count > 0
            ? s.Timer.MidwayAt
            : null;

    /// <summary>Number of Advance presses the reveal takes: guesses, unmasking, each explanation paragraph, scores.</summary>
    public static int RevealStepCount(Scenario scenario) => 3 + scenario.Solution.Explanation.Count;

    /// <summary>1-based number of the act in progress; 0 before the first act starts.</summary>
    public static int CurrentActNumber(GameState s) => s.Phase switch
    {
        Phase.Lobby or Phase.CastReveal or Phase.Prologue => 0,
        Phase.Act => s.ActIndex + 1,
        _ => int.MaxValue, // after the acts, everything has unlocked
    };

    /// <summary>Characters taking part: those played by guests plus NPCs.</summary>
    public static IEnumerable<Character> CharactersInPlay(GameState s, Scenario scenario) =>
        scenario.Characters.Where(c => s.PlayerFor(c.Id) is not null || s.NpcCharacterIds.Contains(c.Id));

    public static GameState Clone(GameState s) => GameJson.Deserialize<GameState>(GameJson.Serialize(s));

    // ------------------------------------------------------------------ Lobby

    private static void AddPlayer(GameState s, Scenario scenario, AddPlayer c)
    {
        RequirePhase(s, Phase.Lobby);
        var name = c.Name.Trim();
        if (name.Length is < 1 or > 30) throw new GameRuleException("Names must be 1 to 30 characters.");
        if (s.FindPlayer(c.SeatId) is not null) throw new GameRuleException("That seat is already in the game.");
        if (s.Players.Count >= scenario.MaxPlayers)
            throw new GameRuleException($"This mystery holds at most {scenario.MaxPlayers} players.");
        if (s.Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new GameRuleException($"Someone called {name} has already joined. Try a nickname.");

        s.Players.Add(new PlayerState { SeatId = c.SeatId, Name = name, IsHost = c.IsHost, IsLocal = c.IsLocal });
        AddFeed(s, c.Now, $"{name} has arrived.");
    }

    private static void ChooseCharacter(GameState s, Scenario scenario, ChooseCharacter c)
    {
        RequirePhase(s, Phase.Lobby);
        var player = RequirePlayer(s, c.SeatId);
        if (c.CharacterId is null)
        {
            player.CharacterId = null;
            return;
        }
        if (scenario.FindCharacter(c.CharacterId) is null) throw new GameRuleException("No such character.");
        var holder = s.PlayerFor(c.CharacterId);
        if (holder is not null && holder.SeatId != c.SeatId)
            throw new GameRuleException($"{holder.Name} is already playing that character.");
        player.CharacterId = c.CharacterId;
    }

    private static void AutoAssign(GameState s, Scenario scenario, DateTimeOffset now)
    {
        // Fill required characters first so as few as possible end up as NPCs,
        // then shuffle the optional ones so repeat games feel different. The
        // random seed comes from the command's timestamp to keep Apply pure.
        var random = new Random((int)(now.UtcTicks % int.MaxValue));
        var taken = s.Players.Where(p => p.CharacterId is not null).Select(p => p.CharacterId!).ToHashSet();
        var free = scenario.Characters.Where(ch => !taken.Contains(ch.Id)).ToList();
        var required = free.Where(ch => ch.Required).OrderBy(_ => random.Next());
        var optional = free.Where(ch => !ch.Required).OrderBy(_ => random.Next());
        var queue = new Queue<Character>(required.Concat(optional));

        foreach (var player in s.Players.Where(p => p.CharacterId is null).OrderBy(_ => random.Next()))
        {
            if (queue.Count == 0) break;
            player.CharacterId = queue.Dequeue().Id;
        }
    }

    private static void StartGame(GameState s, Scenario scenario, StartGame c)
    {
        RequirePhase(s, Phase.Lobby);
        if (s.Players.Count < scenario.MinPlayers)
            throw new GameRuleException($"This mystery needs at least {scenario.MinPlayers} players.");

        AutoAssign(s, scenario, c.Now);

        var played = s.Players.Select(p => p.CharacterId).ToHashSet();
        s.NpcCharacterIds = scenario.Characters.Where(ch => ch.Required && !played.Contains(ch.Id)).Select(ch => ch.Id).ToList();
        s.Phase = Phase.CastReveal;
        s.Tailoring = false;
        s.StartedAt = c.Now;
        AddFeed(s, c.Now, "The evening begins. Read your dossier, and trust no one.");
    }

    // ------------------------------------------------------------------ Flow

    /// <summary>How long a turn in the spotlight lasts: a guide for the room, not enforced.</summary>
    public static readonly TimeSpan SpeakerTurn = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ConfrontationTurn = TimeSpan.FromSeconds(60);

    private static void RequireSpotlightTime(GameState s)
    {
        if (s.Phase is not (Phase.CastReveal or Phase.Act))
            throw new GameRuleException("The spotlight is for introductions and the investigation.");
    }

    private static void SetSpotlight(GameState s, Scenario scenario, SetSpotlight c)
    {
        RequireSpotlightTime(s);
        if (c.CharacterId is null)
        {
            ClearSpotlight(s);
            return;
        }
        if (!CharactersInPlay(s, scenario).Any(ch => ch.Id == c.CharacterId)) throw new GameRuleException("That character isn't at the party.");
        GiveTheFloor(s, c.CharacterId, c.Now, SpeakerTurn);
    }

    /// <summary>
    /// A random pick among characters who haven't spoken in this scene (everyone again once all have).
    /// The seed comes from the command's timestamp, like AutoAssign, so Apply stays pure.
    /// </summary>
    private static void SpinSpotlight(GameState s, Scenario scenario, DateTimeOffset now)
    {
        RequireSpotlightTime(s);
        var inPlay = CharactersInPlay(s, scenario).Select(ch => ch.Id).ToList();
        if (inPlay.Count == 0) throw new GameRuleException("There's nobody to spotlight yet.");
        var waiting = inPlay.Where(id => !s.SpotlightSpoken.Contains(id) && id != s.SpotlightCharacterId).ToList();
        if (waiting.Count == 0)
        {
            s.SpotlightSpoken.Clear();
            waiting = inPlay.Where(id => id != s.SpotlightCharacterId).ToList();
            if (waiting.Count == 0) waiting = inPlay;
        }
        var random = new Random((int)(now.UtcTicks % int.MaxValue));
        GiveTheFloor(s, waiting[random.Next(waiting.Count)], now, SpeakerTurn);
    }

    private static void GiveTheFloor(GameState s, string characterId, DateTimeOffset now, TimeSpan turn)
    {
        s.SpotlightCharacterId = characterId;
        s.SpotlightEndsAt = now + turn;
        s.SpotlightTurns++;
        s.Confrontation = null;
        if (!s.SpotlightSpoken.Contains(characterId)) s.SpotlightSpoken.Add(characterId);
    }

    private static void ClearSpotlight(GameState s)
    {
        s.SpotlightCharacterId = null;
        s.SpotlightEndsAt = null;
        s.Confrontation = null;
    }

    private static void Advance(GameState s, Scenario scenario, DateTimeOffset now)
    {
        // A new scene starts with nobody in the spotlight, and everyone waiting for a turn.
        ClearSpotlight(s);
        s.SpotlightSpoken.Clear();
        switch (s.Phase)
        {
            case Phase.CastReveal:
                s.Phase = Phase.Prologue;
                break;

            case Phase.Prologue:
                EnterAct(s, 0);
                break;

            case Phase.Act when s.ActStep == ActStep.Cinematic:
                StartMingle(s, scenario, now);
                break;

            case Phase.Act:
                // Never lose a clue: anything still pending drops before moving on.
                DropAllPending(s, scenario, now);
                s.Timer = new Timer();
                if (s.ActIndex + 1 < scenario.Acts.Count)
                {
                    EnterAct(s, s.ActIndex + 1);
                }
                else
                {
                    s.Phase = Phase.Accusation;
                    s.ActStep = ActStep.Cinematic;
                    AddFeed(s, now, "Time to name the killer. Lock in your accusation.");
                }
                break;

            case Phase.Accusation:
                s.Phase = Phase.Reveal;
                s.RevealStep = 0;
                break;

            case Phase.Reveal when s.RevealStep < RevealStepCount(scenario) - 1:
                s.RevealStep++;
                break;

            case Phase.Reveal:
                s.Phase = Phase.Awards;
                break;

            case Phase.Awards:
                s.Phase = Phase.Finished;
                AddFeed(s, now, "Thank you for a killer evening.");
                break;

            default:
                throw new GameRuleException(s.Phase == Phase.Lobby ? "Start the game first." : "The game is over.");
        }
    }

    private static void EnterAct(GameState s, int index)
    {
        s.Phase = Phase.Act;
        s.ActIndex = index;
        s.ActStep = ActStep.Cinematic;
        s.Timer = new Timer();
        s.PendingClueIds = [];
    }

    private static void StartMingle(GameState s, Scenario scenario, DateTimeOffset now)
    {
        var act = scenario.Acts[s.ActIndex];
        var actNumber = s.ActIndex + 1;
        var duration = TimeSpan.FromMinutes(Math.Max(1, act.MingleMinutes));

        s.ActStep = ActStep.Mingle;
        s.Timer = new Timer { EndsAt = now + duration, MidwayAt = now + duration / 2 };

        foreach (var clue in scenario.Clues.Where(c => c.Act == actNumber && c.Wave == ClueWave.Start))
        {
            DropClue(s, scenario, clue, now);
        }
        s.PendingClueIds = scenario.Clues.Where(c => c.Act == actNumber && c.Wave == ClueWave.Midway).Select(c => c.Id).ToList();
    }

    // ------------------------------------------------------------------ Clues & timer

    private static bool TickDue(GameState s, DateTimeOffset now) =>
        NextDueAt(s) is { } due && due <= now;

    private static void DropNextClue(GameState s, Scenario scenario, DateTimeOffset now)
    {
        RequireMingle(s);
        if (s.PendingClueIds.Count == 0) throw new GameRuleException("All of this act's clues are already out.");
        var clue = scenario.FindClue(s.PendingClueIds[0])!;
        s.PendingClueIds.RemoveAt(0);
        DropClue(s, scenario, clue, now);
        if (s.PendingClueIds.Count == 0) s.Timer.MidwayAt = null;
    }

    private static void DropAllPending(GameState s, Scenario scenario, DateTimeOffset now)
    {
        foreach (var id in s.PendingClueIds)
        {
            DropClue(s, scenario, scenario.FindClue(id)!, now);
        }
        s.PendingClueIds = [];
        s.Timer.MidwayAt = null;
        s.Timer.PausedMidwayRemaining = null;
    }

    private static void DropClue(GameState s, Scenario scenario, Clue clue, DateTimeOffset now)
    {
        if (s.DroppedClues.Any(d => d.ClueId == clue.Id)) return;

        // A private clue addressed to a character nobody is playing would vanish,
        // so it goes public instead, framed as found among their belongings.
        var recipient = clue.Visibility == ClueVisibility.Private && clue.Recipient is not null
            ? s.PlayerFor(clue.Recipient)
            : null;

        s.DroppedClues.Add(new DroppedClue { ClueId = clue.Id, At = now, RecipientSeatId = recipient?.SeatId });

        if (recipient is not null)
        {
            AddFeed(s, now, "A clue has been slipped to someone in the room…");
        }
        else if (clue.Visibility == ClueVisibility.Private && clue.Recipient is not null)
        {
            var owner = scenario.FindCharacter(clue.Recipient)!;
            AddFeed(s, now, $"New clue, found among {owner.Name}'s belongings: {clue.Title}");
        }
        else
        {
            AddFeed(s, now, $"New clue: {clue.Title}");
        }
    }

    private static void PauseTimer(GameState s, DateTimeOffset now)
    {
        RequireMingle(s);
        if (s.Timer.IsPaused) return;
        var t = s.Timer;
        t.PausedRemaining = Max(TimeSpan.Zero, (t.EndsAt ?? now) - now);
        t.PausedMidwayRemaining = t.MidwayAt is { } m ? Max(TimeSpan.Zero, m - now) : null;
        t.EndsAt = null;
        t.MidwayAt = null;
    }

    private static void ResumeTimer(GameState s, DateTimeOffset now)
    {
        RequireMingle(s);
        var t = s.Timer;
        if (!t.IsPaused) return;
        t.EndsAt = now + t.PausedRemaining!.Value;
        t.MidwayAt = t.PausedMidwayRemaining is { } m ? now + m : null;
        t.PausedRemaining = null;
        t.PausedMidwayRemaining = null;
    }

    private static void ExtendTimer(GameState s, ExtendTimer c)
    {
        RequireMingle(s);
        if (c.Minutes is < 1 or > 60) throw new GameRuleException("Extend by 1 to 60 minutes.");
        var extra = TimeSpan.FromMinutes(c.Minutes);
        var t = s.Timer;
        if (t.IsPaused)
        {
            t.PausedRemaining += extra;
        }
        else
        {
            // If time had already run out, count from now rather than from the past.
            var baseline = t.EndsAt is { } end && end > c.Now ? end : c.Now;
            t.EndsAt = baseline + extra;
        }
    }

    private static void ConvertToNpc(GameState s, Scenario scenario, ConvertToNpc c)
    {
        if (s.Phase == Phase.Lobby) throw new GameRuleException("In the lobby, just remove the player instead.");
        var player = RequirePlayer(s, c.SeatId);
        if (player.CharacterId is { } characterId)
        {
            if (!s.NpcCharacterIds.Contains(characterId)) s.NpcCharacterIds.Add(characterId);

            // Their private clues would otherwise be lost with them.
            foreach (var clue in s.DroppedClues.Where(d => d.RecipientSeatId == c.SeatId))
            {
                clue.RecipientSeatId = null;
            }
            var name = scenario.FindCharacter(characterId)?.Name ?? player.Name;
            AddFeed(s, c.Now, $"{player.Name} has left. The narrator will speak for {name}.");
        }
        s.Players.Remove(player);
        s.Accusations.Remove(c.SeatId);
        s.AwardVotes.Remove(c.SeatId);
        s.Suspicions.Remove(c.SeatId);
    }

    // ------------------------------------------------------------------ Player actions

    private static void RevealSecret(GameState s, Scenario scenario, RevealSecret c)
    {
        if (s.Phase is Phase.Lobby or Phase.Reveal or Phase.Awards or Phase.Finished)
            throw new GameRuleException("Secrets can only be revealed during the investigation.");
        var player = RequirePlayer(s, c.SeatId);
        var character = RequireCharacter(scenario, player);
        var secret = character.Private.Secrets.FirstOrDefault(x => x.Id == c.SecretId)
            ?? throw new GameRuleException("That is not one of your secrets.");
        if (secret.UnlockAct > CurrentActNumber(s)) throw new GameRuleException("You don't know that secret yet.");
        if (s.RevealedSecrets.Any(r => r.SecretId == secret.Id)) return;

        s.RevealedSecrets.Add(new RevealedSecret { CharacterId = character.Id, SecretId = secret.Id, At = c.Now });
        AddFeed(s, c.Now, $"{character.Name} has revealed a secret!");
    }

    private static void ShareClue(GameState s, Scenario scenario, ShareClue c)
    {
        var player = RequirePlayer(s, c.SeatId);
        var dropped = s.DroppedClues.FirstOrDefault(d => d.ClueId == c.ClueId && d.RecipientSeatId == c.SeatId)
            ?? throw new GameRuleException("You don't hold that clue.");
        if (dropped.SharedPublicly) return;
        dropped.SharedPublicly = true;
        var clue = scenario.FindClue(c.ClueId)!;
        var who = player.CharacterId is { } id ? scenario.FindCharacter(id)?.Name ?? player.Name : player.Name;
        AddFeed(s, c.Now, $"{who} shared a clue: {clue.Title}");
    }

    private static void Confront(GameState s, Scenario scenario, Confront c)
    {
        RequireMingle(s);
        var player = RequirePlayer(s, c.SeatId);
        if (player.CharacterId is null) throw new GameRuleException("Choose a character first.");
        if (c.SuspectId == player.CharacterId) throw new GameRuleException("You can't confront yourself. Well, you can, but not here.");
        var suspect = CharactersInPlay(s, scenario).FirstOrDefault(ch => ch.Id == c.SuspectId)
            ?? throw new GameRuleException("That character isn't at the party.");
        if (!CanSeeClue(s, c.SeatId, c.ClueId)) throw new GameRuleException("You haven't found that clue.");
        var act = CurrentActNumber(s);
        if (s.ConfrontedInAct.TryGetValue(c.SeatId, out var last) && last == act)
            throw new GameRuleException("You've already confronted someone this act. Save your evidence for the next one.");

        // Evidence is shown to the whole room: a private clue used this way is shared.
        var dropped = s.DroppedClues.First(d => d.ClueId == c.ClueId);
        if (dropped.RecipientSeatId is not null) dropped.SharedPublicly = true;

        s.ConfrontedInAct[c.SeatId] = act;
        GiveTheFloor(s, suspect.Id, c.Now, ConfrontationTurn);
        s.Confrontation = new Confrontation { AccuserSeatId = c.SeatId, ClueId = c.ClueId };
        var accuser = scenario.FindCharacter(player.CharacterId)?.Name ?? player.Name;
        AddFeed(s, c.Now, $"{accuser} confronts {suspect.Name} with “{scenario.FindClue(c.ClueId)!.Title}”!");
    }

    private static void SetSuspicion(GameState s, Scenario scenario, SetSuspicion c)
    {
        if (s.Phase != Phase.Act) throw new GameRuleException("You can point the finger during the acts.");
        var player = RequirePlayer(s, c.SeatId);
        if (c.CharacterId is null)
        {
            s.Suspicions.Remove(c.SeatId);
            return;
        }
        if (c.CharacterId == player.CharacterId) throw new GameRuleException("Suspecting yourself? Very modern. Pick someone else.");
        if (!CharactersInPlay(s, scenario).Any(ch => ch.Id == c.CharacterId)) throw new GameRuleException("That character isn't at the party.");
        s.Suspicions[c.SeatId] = c.CharacterId;
    }

    private static void SolvePuzzle(GameState s, Scenario scenario, SolvePuzzle c)
    {
        var player = RequirePlayer(s, c.SeatId);
        var clue = scenario.FindClue(c.ClueId);
        if (clue?.Puzzle is null) throw new GameRuleException("There is no puzzle on that clue.");
        if (!CanSeeClue(s, c.SeatId, c.ClueId)) throw new GameRuleException("You haven't found that clue.");
        if (s.SolvedPuzzles.Any(p => p.ClueId == c.ClueId)) return;

        var answer = Normalize(c.Answer);
        if (!clue.Puzzle.Answers.Any(a => Normalize(a) == answer))
            throw new GameRuleException("That's not it. Keep thinking…");

        s.SolvedPuzzles.Add(new SolvedPuzzle { ClueId = c.ClueId, SolvedBySeatId = c.SeatId, At = c.Now });
        AddFeed(s, c.Now, $"{player.Name} cracked the puzzle: {clue.Title}!");
    }

    private static void SubmitAccusation(GameState s, Scenario scenario, SubmitAccusation c)
    {
        RequirePhase(s, Phase.Accusation);
        RequirePlayer(s, c.SeatId);
        if (!CharactersInPlay(s, scenario).Any(ch => ch.Id == c.SuspectId)) throw new GameRuleException("Pick one of the suspects.");
        if (scenario.Accusation.Motives.All(o => o.Id != c.MotiveId)) throw new GameRuleException("Pick a motive.");
        if (scenario.Accusation.Methods.All(o => o.Id != c.MethodId)) throw new GameRuleException("Pick a method.");

        // Players may change their mind until the host moves on to the reveal.
        s.Accusations[c.SeatId] = new AccusationEntry
        {
            SuspectId = c.SuspectId, MotiveId = c.MotiveId, MethodId = c.MethodId, At = c.Now,
        };
    }

    private static void CastAwardVote(GameState s, CastAwardVote c)
    {
        RequirePhase(s, Phase.Awards);
        RequirePlayer(s, c.SeatId);
        if (Awards.All(a => a.Id != c.AwardId)) throw new GameRuleException("Unknown award.");
        if (s.FindPlayer(c.NomineeSeatId) is null) throw new GameRuleException("Vote for someone at the party.");
        if (c.NomineeSeatId == c.SeatId) throw new GameRuleException("Nice try. You can't vote for yourself.");

        if (!s.AwardVotes.TryGetValue(c.SeatId, out var votes))
        {
            votes = [];
            s.AwardVotes[c.SeatId] = votes;
        }
        votes[c.AwardId] = c.NomineeSeatId;
    }

    // ------------------------------------------------------------------ Helpers

    // ------------------------------------------------------------------ AI-assisted actions

    public const int MaxQuestionLength = 300;

    /// <summary>How many questions this seat has left to ask NPCs in the current act.</summary>
    public static int QuestionsLeft(GameState s, Guid seatId) =>
        !s.Ai.NpcQuestions || s.Phase != Phase.Act
            ? 0
            : Math.Max(0, s.Ai.QuestionsPerAct - s.Interrogations.Count(i => i.SeatId == seatId && i.Act == CurrentActNumber(s)));

    public static int HintsLeft(GameState s, Guid seatId) =>
        !s.Ai.Hints || s.Phase != Phase.Act
            ? 0
            : Math.Max(0, s.Ai.HintsPerAct - s.Hints.Count(h => h.SeatId == seatId && h.Act == CurrentActNumber(s)));

    private static void BeginNpcQuestion(GameState s, Scenario scenario, BeginNpcQuestion c)
    {
        if (!s.Ai.NpcQuestions) throw new GameRuleException("Questioning characters isn't switched on for this party.");
        if (s.Phase != Phase.Act) throw new GameRuleException("You can only question characters during an act.");
        var player = RequirePlayer(s, c.SeatId);
        if (!s.NpcCharacterIds.Contains(c.CharacterId))
            throw new GameRuleException("You can only question characters played by the narrator. Ask real guests in person!");
        var question = c.Question.Trim();
        if (question.Length is < 3 or > MaxQuestionLength)
            throw new GameRuleException($"Questions must be 3 to {MaxQuestionLength} characters.");
        if (QuestionsLeft(s, c.SeatId) == 0)
            throw new GameRuleException("You've used all your questions for this act.");
        if (s.Interrogations.Any(i => i.CharacterId == c.CharacterId && i.Answer is null))
            throw new GameRuleException($"{scenario.FindCharacter(c.CharacterId)?.Name} is still answering the last question.");

        s.Interrogations.Add(new Interrogation
        {
            Id = c.Id, At = c.Now, Act = CurrentActNumber(s), SeatId = c.SeatId,
            AskerName = player.Name, CharacterId = c.CharacterId, Question = question,
        });
    }

    private static void BeginHint(GameState s, BeginHint c)
    {
        if (!s.Ai.Hints) throw new GameRuleException("Hints aren't switched on for this party.");
        if (s.Phase != Phase.Act) throw new GameRuleException("Hints are only available during an act.");
        RequirePlayer(s, c.SeatId);
        if (HintsLeft(s, c.SeatId) == 0) throw new GameRuleException("You've had your hint for this act.");
        s.Hints.Add(new HintEntry { Id = c.Id, At = c.Now, Act = CurrentActNumber(s), SeatId = c.SeatId });
    }

    private static void SetVerdicts(GameState s, SetVerdicts c)
    {
        if (s.Phase is not (Phase.Reveal or Phase.Awards or Phase.Finished))
            throw new GameRuleException("Verdicts are only given at the reveal.");
        foreach (var (seat, text) in c.Verdicts)
        {
            if (s.FindPlayer(seat) is not null) s.Verdicts[seat] = Clip(text, 600);
        }
    }

    private static Interrogation RequireInterrogation(GameState s, Guid id) =>
        s.Interrogations.FirstOrDefault(i => i.Id == id) ?? throw new GameRuleException("That question no longer exists.");

    private static HintEntry RequireHint(GameState s, Guid id) =>
        s.Hints.FirstOrDefault(h => h.Id == id) ?? throw new GameRuleException("That hint no longer exists.");

    /// <summary>AI output is untrusted text: cap its length so one runaway reply can't bloat the saved game state.</summary>
    private static string Clip(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    public static bool CanSeeClue(GameState s, Guid seatId, string clueId) =>
        s.DroppedClues.Any(d => d.ClueId == clueId && (d.RecipientSeatId is null || d.SharedPublicly || d.RecipientSeatId == seatId));

    private static void RequirePhase(GameState s, Phase phase)
    {
        if (s.Phase != phase) throw new GameRuleException($"That can only be done during the {phase} phase.");
    }

    private static void RequireMingle(GameState s)
    {
        if (s.Phase != Phase.Act || s.ActStep != ActStep.Mingle)
            throw new GameRuleException("That only works while guests are mingling.");
    }

    private static PlayerState RequirePlayer(GameState s, Guid seatId) =>
        s.FindPlayer(seatId) ?? throw new GameRuleException("You are not seated at this party.");

    private static Character RequireCharacter(Scenario scenario, PlayerState player) =>
        player.CharacterId is { } id && scenario.FindCharacter(id) is { } ch
            ? ch
            : throw new GameRuleException("You don't have a character yet.");

    private static void AddFeed(GameState s, DateTimeOffset now, string text)
    {
        s.Feed.Add(new FeedItem { At = now, Text = text });
        if (s.Feed.Count > MaxFeedItems) s.Feed.RemoveRange(0, s.Feed.Count - MaxFeedItems);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>"The Clock!" and "the clock" should both count as right answers.</summary>
    private static string Normalize(string text) => NonAlphanumeric().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
