using System.Text;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;

namespace ButlerDidIt.Ai.Prompts;

/// <summary>
/// Prompts for an escape room's AI game master: its lines on the TV, and its hints.
///
/// Neither prompt ever holds an answer. The narration is built only from the TV's public view,
/// and the hint only from what the group has in front of it (the puzzle, the clue pieces on their
/// phones, the items, the spots they've searched and what they've written in their notebook, their
/// wrong tries): never what's in a spot nobody has searched, or a clue piece still hidden. The engine checks every hint again before showing it (EscapeHintGuard).
/// </summary>
public static class EscapePrompts
{
    public const string NarrationTask = "escape-narration";
    public const string HintTask = "escape-hint";

    /// <summary>The line the game master says out loud when <paramref name="cue"/> happens.</summary>
    public static string Narration(EscapeRoom template, EscapeState state, EscapeCue cue, DateTimeOffset now)
    {
        var view = EscapeProjector.Stage(state, template, now);
        var host = template.Host;
        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {NarrationTask}");
        sb.AppendLine($"You are {host.Name}, the unseen game master of an escape room called \"{view.RoomTitle}\", played by a group of friends at a party.");
        if (host.Persona.Length > 0) sb.AppendLine($"Your character: {host.Persona}");
        sb.AppendLine($"The room: {view.Synopsis}");
        sb.AppendLine("You watch them through a hidden camera, and your voice comes out of the TV. React to what just happened.");
        sb.AppendLine();
        sb.AppendLine("## Where they are");
        sb.AppendLine($"Players: {string.Join(", ", view.Players.Select(p => p.Name))}.");
        sb.AppendLine($"Stage {view.StageNumber} of {view.StageCount}{(view.Stage is { } stage ? $": {stage.Title}" : "")}. Puzzles solved: {view.SolvedCount} of {view.PuzzleCount}.");
        if (view.Deadline is { } deadline && view.Phase == EscapePhase.Playing)
            sb.AppendLine($"Time left: about {Math.Max(0, (int)Math.Round((deadline - now).TotalMinutes))} minutes of {view.TimeLimitMinutes}.");
        sb.AppendLine($"Hints used: {view.HintsUsed}. Wrong answers so far: {view.WrongAttempts}.");
        sb.AppendLine();
        sb.AppendLine($"MOMENT: {cue.Kind}");
        sb.AppendLine(Describe(cue));
        var said = state.Cues.Where(c => c.Text is not null).TakeLast(3).Select(c => c.Text).ToList();
        if (said.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What you said most recently (don't repeat yourself):");
            foreach (var line in said) sb.AppendLine($"- {line}");
        }
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("- One or two short sentences, under 30 words, spoken in character. No stage directions, lists or markdown.");
        sb.AppendLine("- Never give away how to solve anything, and never say a code, a password or an answer.");
        sb.AppendLine("- Talk to the group, and use a player's name when the moment is about them.");
        sb.AppendLine(ContentGuidance.For(template.ContentRating));
        return sb.ToString();
    }

    private static string Describe(EscapeCue cue) => cue.Kind switch
    {
        CueKind.Start => $"The clock has just started. They're looking around the first part of the room{Of(cue.StageTitle)}. Welcome them to your game.",
        CueKind.StageOpened => $"{cue.PlayerName} solved {cue.PuzzleTitle}, and that opened the next part of the room{Of(cue.StageTitle)}.",
        CueKind.Solved => $"{cue.PlayerName} just solved {cue.PuzzleTitle}.",
        CueKind.WrongStreak => $"Three wrong answers in a row; the latest was {cue.PlayerName}'s on {cue.PuzzleTitle}. Tease them a little.",
        CueKind.LowTime => "Only five minutes are left on the clock. Turn up the pressure.",
        CueKind.Escaped => $"They escaped! {cue.PlayerName} solved the last puzzle, {cue.PuzzleTitle}. Concede, in character.",
        CueKind.Failed => "Time ran out before they escaped. Gloat, in character, but invite them to try again.",
        CueKind.Found => $"{cue.PlayerName} searched and found something useful: {cue.Thing}. Don't say what it's for.",
        CueKind.Decoy => $"{cue.PlayerName} searched the {cue.Thing} and found nothing at all, and on Hard that cost them time. Tease them for it, without hinting where to look instead.",
        _ => "",
    };

    private static string Of(string? stage) => stage is null ? "" : $": {stage}";

    /// <summary>
    /// A hint for one puzzle, in the game master's voice. <paramref name="step"/> is the hint step
    /// being paid for; the room's written hint for that step is the direction to nudge in.
    /// </summary>
    public static string Hint(EscapeRoom template, EscapeState state, string puzzleId, int step, DateTimeOffset now)
    {
        var room = EscapeEngine.RoomFor(state, template);
        var puzzle = room.FindPuzzle(puzzleId) ?? throw new ArgumentException($"Unknown puzzle '{puzzleId}'.", nameof(puzzleId));
        var host = room.Host;
        string Item(string id) => room.FindItem(id)?.Name ?? id;
        string Holder(Guid? seat) => seat is { } s ? state.FindPlayer(s)?.Name ?? "someone" : "someone";
        var stage = room.Stages[Math.Min(state.StageIndex, room.Stages.Count - 1)];

        var sb = new StringBuilder();
        sb.AppendLine($"TASK: {HintTask}");
        sb.AppendLine($"You are {host.Name}, the game master of an escape room called \"{room.Title}\". {host.Persona}");
        sb.AppendLine("The group is stuck on one puzzle and paid for a hint. Below is everything they have to work with. You don't know the answer, and you must not try to work it out for them.");
        sb.AppendLine();
        sb.AppendLine("## The puzzle");
        sb.AppendLine(GameJson.Serialize(new
        {
            puzzle.Title,
            // A built puzzle no longer says which generator made it; the room as written does.
            Kind = (template.FindPuzzle(puzzle.Id)?.Generator?.Type, puzzle.Kind) switch
            {
                (GeneratorType.Cipher, _) => "a coded word: decode it with its key, then type the word",
                (GeneratorType.Sequence, _) => "a number pattern: type the number that comes next",
                (GeneratorType.Deduction, _) => "a logic puzzle: line the things up so every clue is true; the code is each one's place, in the order listed",
                (_, PuzzleKind.Code) => "a number keypad",
                (_, PuzzleKind.Text) => "a word or phrase to type",
                (_, PuzzleKind.Search) => "opens once the right spots in the room have been searched",
                (_, PuzzleKind.Switches) => "a grid of lights; pressing one flips it and its neighbours; every light must be on",
                _ => "a lock opened with the right item",
            },
            puzzle.Prompt,
            // Whether they've found the cipher's key yet (never the key itself: if they have it, it's in their notebook).
            CipherKeyFound = puzzle.Decoder is null ? (bool?)null : EscapeEngine.KeyFound(state, puzzle),
            Needs = puzzle.Requires.Select(Item),
            // What a close look at each item showed, only for the ones someone has looked at.
            Holding = state.Inventory.Select(id => room.FindItem(id)).OfType<EscapeItem>()
                .Select(i => new { i.Name, CloseLook = state.Inspected.Contains(i.Id) ? i.Inspect : null }),
            // Pieces are already on the group's phones: who holds which, so the hint can send them to the right person.
            // A piece still hidden in the room is only counted: nobody has read it yet.
            CluePieces = state.Pieces.Where(p => p.PuzzleId == puzzle.Id && !p.IsHidden).Select(p => new { HeldBy = Holder(p.SeatId), Text = puzzle.Pieces[p.Index] }),
            CluePiecesStillHidden = state.Pieces.Count(p => p.PuzzleId == puzzle.Id && p.IsHidden),
            // The room in front of them: every spot, but what was there only for the ones someone searched.
            Spots = (stage.Scene?.Objects ?? []).Select(o => new { o.Label, Searched = state.Examined.Contains(o.Id), Found = state.Examined.Contains(o.Id) ? o.Look : null }),
            SpotsThisPuzzleNeeds = puzzle.Finds.Count == 0 ? null : new { Searched = puzzle.Finds.Count(state.Examined.Contains), Of = puzzle.Finds.Count },
            LightsOn = puzzle.Grid is { } grid ? $"{EscapeEngine.LitNow(state, puzzle).Count} of {grid.Size * grid.Size}" : null,
            Notebook = state.Notebook.Select(n => $"{n.Source}: {n.Text}"),
            WrongTries = state.RecentWrong.GetValueOrDefault(puzzle.Id) ?? [],
            HintsAlreadyGiven = EscapeProjector.Stage(state, template, now).Puzzles
                .FirstOrDefault(p => p.Id == puzzle.Id)?.Hints ?? [],
        }));
        sb.AppendLine();
        if (step >= 0 && step < puzzle.Hints.Count)
        {
            sb.AppendLine("## The room author's hint for this step (nudge in this direction, in your own words)");
            sb.AppendLine(puzzle.Hints[step]);
            sb.AppendLine();
        }
        sb.AppendLine("## Rules");
        sb.AppendLine("- Give ONE nudge that fits where they seem stuck (look at their wrong tries), without solving it for them.");
        sb.AppendLine("- Never write a code, a number from a code, a password or an answer, not even as a guess.");
        sb.AppendLine("- One or two sentences, under 50 words, in character. No lists or markdown.");
        if (state.Level == EscapeDifficulty.Hard)
            sb.AppendLine("- They chose Hard: only nudge. Don't name the spot to search, the item to use, or who holds which clue.");
        sb.AppendLine(ContentGuidance.For(room.ContentRating));
        return sb.ToString();
    }
}
