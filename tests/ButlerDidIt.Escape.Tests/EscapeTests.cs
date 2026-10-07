using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Tests;

public static class Rooms
{
    private static readonly Lazy<List<EscapeRoom>> All = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content"))) dir = dir.Parent;
        return EscapeLibrary.Load(Path.Combine(dir!.FullName, "content", "escape"));
    });

    public static IReadOnlyList<EscapeRoom> Library => All.Value;
    public static EscapeRoom Get(string id) => Library.Single(r => r.Id == id);
}

public class ContentTests
{
    [Fact]
    public void The_library_has_adult_and_family_rooms()
    {
        Assert.Equal(ContentRating.Mature, Rooms.Get("the-workshop").ContentRating);
        Assert.Equal(ContentRating.Mature, Rooms.Get("the-asylum").ContentRating);
        Assert.Equal(ContentRating.Family, Rooms.Get("the-funhouse").ContentRating);
        Assert.Equal(ContentRating.Family, Rooms.Get("the-toy-factory").ContentRating);
        Assert.Equal(ContentRating.Mature, Rooms.Get("the-bunker").ContentRating);
        Assert.Equal(ContentRating.Family, Rooms.Get("the-wizards-tower").ContentRating);
        Assert.Equal(ContentRating.Family, Rooms.Get("the-pirate-ship").ContentRating);
        Assert.Contains("halloween", Rooms.Get("the-asylum").Seasons);
        Assert.Contains("halloween", Rooms.Get("the-wizards-tower").Seasons);
        Assert.DoesNotContain("halloween", Rooms.Get("the-toy-factory").Seasons);
        Assert.DoesNotContain("halloween", Rooms.Get("the-bunker").Seasons);
    }

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void Every_room_has_a_look_a_sound_a_villain_and_more_than_one_length(string id)
    {
        var room = Rooms.Get(id);
        Assert.False(string.IsNullOrWhiteSpace(room.ArtStyle));
        Assert.NotNull(room.GameMaster);
        Assert.True(room.PlayableLengths.Count > 1, "a host can pick a shorter or longer game");
        // A longer game really is longer: more puzzles at every step up.
        var counts = room.PlayableLengths.Select(m => RoomLengths.Cut(room, m).Puzzles.Count).ToList();
        Assert.Equal(counts.Order(), counts);
        Assert.True(counts[0] < counts[^1]);
    }

    public static TheoryData<string> RoomIds() => new(Rooms.Library.Select(r => r.Id));

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void Every_room_is_valid_and_can_be_escaped(string id) =>
        Assert.Empty(EscapeRoomValidator.Validate(Rooms.Get(id)));

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void Every_room_makes_the_group_share_clues(string id) =>
        Assert.Contains(RoomVariants.Build(Rooms.Get(id), 0).Puzzles, p => p.Pieces.Count >= 3);
}

public class ValidatorTests
{
    private static EscapeRoom Room(Action<List<EscapePuzzle>>? change = null, ContentRating rating = ContentRating.Mature, string intro = "Go.")
    {
        var puzzles = new List<EscapePuzzle>
        {
            new() { Id = "a", Title = "A", Kind = PuzzleKind.Text, Prompt = "?", Answers = ["yes"], Rewards = ["key"], Hints = ["h"], SolvedText = "ok" },
            new() { Id = "b", Title = "B", Kind = PuzzleKind.Use, Prompt = "?", Requires = ["key"], Hints = ["h"], SolvedText = "ok" },
        };
        change?.Invoke(puzzles);
        return new EscapeRoom
        {
            Id = "r", Title = "R", Synopsis = "S", Intro = intro, EscapedText = "E", FailedText = "F", ContentRating = rating,
            Stages = [new EscapeStage { Id = "s", Title = "S", Description = "D", Puzzles = puzzles.Select(p => p.Id).ToList() }],
            Puzzles = puzzles,
            Items = [new EscapeItem { Id = "key", Name = "Key" }],
        };
    }

    [Fact]
    public void A_simple_room_is_valid() => Assert.Empty(EscapeRoomValidator.Validate(Room()));

    [Fact]
    public void A_room_that_needs_an_item_nobody_gives_out_cannot_be_escaped()
    {
        var errors = EscapeRoomValidator.Validate(Room(p => p[0] = new EscapePuzzle
        {
            Id = "a", Title = "A", Kind = PuzzleKind.Text, Prompt = "?", Answers = ["yes"], Hints = ["h"], SolvedText = "ok",
        }));
        Assert.Contains(errors, e => e.Contains("can't be finished") && e.Contains("'key'"));
    }

    [Fact]
    public void Codes_must_be_digits_and_use_puzzles_need_items()
    {
        var errors = EscapeRoomValidator.Validate(Room(p =>
        {
            p[0] = new EscapePuzzle { Id = "a", Title = "A", Kind = PuzzleKind.Code, Prompt = "?", Answers = ["12a"], Rewards = ["key"], Hints = ["h"], SolvedText = "ok" };
            p.Add(new EscapePuzzle { Id = "c", Title = "C", Kind = PuzzleKind.Use, Prompt = "?", Hints = ["h"], SolvedText = "ok" });
        }));
        Assert.Contains(errors, e => e.Contains("digits only"));
        Assert.Contains(errors, e => e.Contains("must require at least one item"));
    }

    [Fact]
    public void Family_rooms_stay_spooky_not_gruesome() =>
        Assert.Contains(EscapeRoomValidator.Validate(Room(rating: ContentRating.Family, intro: "There's blood on the floor.")), e => e.Contains("'blood'"));
}

public class EngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid(), Ben = Guid.NewGuid(), Cy = Guid.NewGuid();
    private static EscapeRoom Workshop => Rooms.Get("the-workshop");

    private static EscapeState Started(EscapeRoom room, params Guid[] seats) => Started(room, 0, seats);

    private static EscapeState Started(EscapeRoom room, long seed, params Guid[] seats) => Started(room, seed, null, seats);

    private static EscapeState Started(EscapeRoom room, long seed, int? minutes, params Guid[] seats)
    {
        var s = EscapeEngine.NewGame(seed, minutes: minutes);
        var names = new[] { "Ada", "Ben", "Cy", "Dee" };
        for (var i = 0; i < seats.Length; i++) s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, seats[i], names[i], i == 0, false));
        return EscapeEngine.Apply(s, room, new StartEscape(T0));
    }

    /// <summary>Solves whatever is open, the way a group that knows the answers would.</summary>
    private static EscapeState SolveNext(EscapeState s, EscapeRoom template, DateTimeOffset at, Guid seat)
    {
        // Searching, looking closely and combining as needed, until one more puzzle opens.
        var solved = s.Solved.Count;
        while (s.Phase == EscapePhase.Playing && s.Solved.Count == solved)
            s = EscapeEngine.Apply(s, template, EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, template), seat, at));
        return s;
    }

    /// <summary>Every room at every length it offers, with two puzzle sets each.</summary>
    public static TheoryData<string, long, int> RoomsAndLengths()
    {
        var data = new TheoryData<string, long, int>();
        foreach (var room in Rooms.Library)
            foreach (var minutes in room.PlayableLengths)
                foreach (var seed in new long[] { 0, 20261031 })
                    data.Add(room.Id, seed, minutes);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoomsAndLengths))]
    public void A_group_that_solves_everything_escapes(string id, long seed, int minutes)
    {
        var room = Rooms.Get(id);
        var s = Started(room, seed, minutes, Ada, Ben, Cy);
        Assert.Equal(minutes, (s.Deadline!.Value - T0).TotalMinutes); // the clock follows the length
        for (var i = 0; s.Phase == EscapePhase.Playing; i++) s = SolveNext(s, room, T0.AddMinutes(i + 1), Ben);

        Assert.Equal(EscapePhase.Escaped, s.Phase);
        // Every puzzle of the game as played: a shorter game leaves some of the room's puzzles out.
        Assert.Equal(EscapeEngine.RoomFor(s, room).Puzzles.Count, s.Solved.Count);
        Assert.Equal(room.EscapedText, EscapeProjector.Stage(s, room, T0).EndText);
        Assert.Null(EscapeEngine.NextDueAt(s)); // the ticker can stop watching
    }

    [Fact]
    public void Clue_pieces_are_dealt_so_every_phone_holds_some_and_nobody_holds_a_whole_puzzle()
    {
        var s = Started(Workshop, Ada, Ben, Cy);
        Assert.All(new[] { Ada, Ben, Cy }, seat => Assert.Contains(s.Pieces, p => p.SeatId == seat));
        foreach (var puzzle in EscapeEngine.RoomFor(s, Workshop).Puzzles.Where(p => p.Pieces.Count >= 3))
            Assert.True(s.Pieces.Where(p => p.PuzzleId == puzzle.Id).Select(p => p.SeatId).Distinct().Count() >= 3);
    }

    [Fact]
    public void Answers_are_forgiving_about_case_spacing_and_articles()
    {
        var s = Started(Workshop, Ada, Ben);
        s = EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0, Ada, "tape", "  The CLOCK!  "));
        Assert.True(s.IsSolved("tape"));
        Assert.Contains("rusty-key", s.Inventory);
    }

    [Fact]
    public void A_wrong_answer_locks_the_puzzle_for_a_moment_so_codes_cannot_be_brute_forced()
    {
        var s = Started(Workshop, Ada, Ben);
        s = EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0, Ada, "tape", "a kettle"));
        Assert.False(s.IsSolved("tape"));
        Assert.Equal(1, s.WrongAttempts);
        Assert.Contains("kettle", s.Feed[^1].Text);

        var ex = Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0.AddSeconds(1), Ben, "tape", "clock")));
        Assert.Contains("resetting", ex.Message);
        s = EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0.AddSeconds(4), Ben, "tape", "clock"));
        Assert.True(s.IsSolved("tape"));
    }

    [Fact]
    public void Locked_puzzles_say_what_they_need_and_later_stages_stay_closed()
    {
        var s = Started(Workshop, Ada, Ben);
        var ex = Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new UseItems(T0, Ada, "shackles")));
        Assert.Contains("Oiled key", ex.Message);
        ex = Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0, Ada, "toolbox", "3728")));
        Assert.Contains("isn't in this part", ex.Message);
    }

    [Fact]
    public void Hints_are_revealed_one_at_a_time_and_cost_time()
    {
        var s = Started(Workshop, Ada, Ben);
        var deadline = s.Deadline!.Value;
        s = EscapeEngine.Apply(s, Workshop, new RequestEscapeHint(T0, Ada, "tape"));
        Assert.Equal(deadline.AddSeconds(-Workshop.HintPenaltySeconds), s.Deadline);
        var view = EscapeProjector.Stage(s, Workshop, T0).Puzzles.Single(p => p.Id == "tape");
        Assert.Equal([Workshop.FindPuzzle("tape")!.Hints[0]], view.Hints);
        Assert.Equal(1, view.HintsLeft);

        s = EscapeEngine.Apply(s, Workshop, new RequestEscapeHint(T0, null, "tape")); // the host, from the TV
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new RequestEscapeHint(T0, Ada, "tape")));
        Assert.Equal(2, s.HintsUsed);
    }

    [Fact]
    public void When_the_clock_runs_out_the_group_is_trapped()
    {
        var s = Started(Workshop, Ada, Ben);
        Assert.Same(s, EscapeEngine.Apply(s, Workshop, new EscapeTick(T0.AddMinutes(44)))); // nothing yet: same state, nothing saved
        Assert.Equal(s.Deadline, EscapeEngine.NextDueAt(s));

        s = EscapeEngine.Apply(s, Workshop, new EscapeTick(T0.AddMinutes(45)));
        Assert.Equal(EscapePhase.Failed, s.Phase);
        Assert.Equal(Workshop.FailedText, EscapeProjector.Stage(s, Workshop, T0).EndText);
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0.AddMinutes(46), Ada, "tape", "clock")));
    }

    [Fact]
    public void Nobody_joins_once_the_clock_is_running_and_a_leaver_hands_on_their_clues()
    {
        var s = Started(Workshop, Ada, Ben, Cy);
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new AddEscapePlayer(T0, Guid.NewGuid(), "Late", false, false)));

        var cyPieces = s.Pieces.Count(p => p.SeatId == Cy);
        s = EscapeEngine.Apply(s, Workshop, new RemoveEscapePlayer(T0, Cy));
        Assert.DoesNotContain(s.Pieces, p => p.SeatId == Cy);
        Assert.Equal(EscapeEngine.RoomFor(s, Workshop).Puzzles.Sum(p => p.Pieces.Count), s.Pieces.Count);
        Assert.True(cyPieces > 0);
    }
}

/// <summary>"No leak" tests: views are serialized exactly as a browser receives them and searched for what must never be there.</summary>
public class PrivacyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, int> RoomsAndLengths()
    {
        var data = new TheoryData<string, int>();
        foreach (var room in Rooms.Library)
            foreach (var minutes in room.PlayableLengths) data.Add(room.Id, minutes);
        return data;
    }

    /// <summary>Every file a stage can have: its picture, video, background sound and the game master's reading of it.</summary>
    private static string[] StageMedia(EscapeStage st) =>
        [EscapeArt.Stage(st.Id), EscapeArt.StageVideo(st.Id), EscapeArt.StageAmbience(st.Id), EscapeArt.StageVoice(st.Id)];

    [Theory]
    [MemberData(nameof(RoomsAndLengths))]
    public void Screens_never_see_answers_unpaid_hints_later_stages_or_other_players_clues(string id, int minutes)
    {
        var template = Rooms.Get(id);
        const long seed = 987654321; // a number that appears nowhere else, so the test can check it never leaks
        var seats = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var s = EscapeEngine.NewGame(seed, minutes: minutes);
        // Every puzzle of the room, including ones this length leaves out: those must never show either.
        var room = RoomVariants.Build(template, seed);
        var played = EscapeEngine.RoomFor(s, template);
        for (var i = 0; i < seats.Length; i++) s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, seats[i], $"P{i}", i == 0, false));
        s = EscapeEngine.Apply(s, template, new StartEscape(T0));
        // A picture, a video, a sound and a reading for every stage, each with its own address: a later stage's must never be sent.
        var art = new Dictionary<string, string>
        {
            [EscapeArt.Cover] = $"/media/{Guid.NewGuid()}",
            [EscapeArt.IntroVideo] = $"/media/{Guid.NewGuid()}",
            [EscapeArt.IntroVoice] = $"/media/{Guid.NewGuid()}",
            [EscapeArt.Ambience] = $"/media/{Guid.NewGuid()}",
        };
        foreach (var key in template.Stages.SelectMany(StageMedia))
            art[key] = $"/media/{Guid.NewGuid()}";

        for (var step = 0; s.Phase == EscapePhase.Playing; step++)
        {
            var current = played.Stages[s.StageIndex];
            var shown = EscapeProjector.Stage(s, template, T0, art);
            Assert.Equal(art[EscapeArt.StageVideo(current.Id)], shown.StageVideoUrl);
            Assert.Equal(art[EscapeArt.StageAmbience(current.Id)], shown.AmbienceUrl);
            Assert.Equal(art[EscapeArt.StageVoice(current.Id)], shown.StageVoiceUrl);
            var hidden = template.Stages.Where(st => st.Id != current.Id).SelectMany(StageMedia).Select(key => art[key]).ToList();
            foreach (var raw in seats.Select(seat => GameJson.Serialize(EscapeProjector.Player(s, template, seat, T0, art))).Prepend(GameJson.Serialize(shown)))
                foreach (var url in hidden) Assert.DoesNotContain(url, raw);

            var stageRaw = GameJson.Serialize(EscapeProjector.Stage(s, template, T0));
            Assert.DoesNotContain(seed.ToString(), stageRaw); // the puzzle set would let someone work out the answers (a number, so the raw JSON)
            var stageJson = ViewText.Decoded(stageRaw);
            foreach (var p in room.Puzzles)
            {
                foreach (var answer in p.Answers.Where(a => a.Length >= 3))
                    Assert.False(stageJson.Contains($"\n{answer}\n", StringComparison.OrdinalIgnoreCase), $"answer of {p.Id} leaked");
                foreach (var hint in p.Hints) Assert.DoesNotContain(hint, stageJson); // nobody paid for any
                foreach (var piece in p.Pieces) Assert.DoesNotContain(piece, stageJson);
                if (!current.Puzzles.Contains(p.Id)) Assert.DoesNotContain(p.Prompt, stageJson);
            }
            // A puzzle the group hasn't found yet (#134) isn't on any screen: not its title, its prompt or its pieces. And a
            // final lock's code, built for this game, never shows either.
            var phones = seats.Select(seat => ViewText.Decoded(GameJson.Serialize(EscapeProjector.Player(s, template, seat, T0)))).ToList();
            foreach (var p in current.Puzzles.Select(played.FindPuzzle).OfType<EscapePuzzle>().Where(p => !EscapeEngine.Visible(s, p)))
                foreach (var screen in phones.Prepend(stageJson))
                {
                    Assert.False(screen.Contains($"\n{p.Title}\n"), $"{p.Id} is out of sight, but its title was sent");
                    Assert.DoesNotContain(p.Prompt, screen);
                    foreach (var piece in p.Pieces) Assert.DoesNotContain(piece, screen);
                }
            foreach (var code in played.Puzzles.Where(p => p.Final is not null).SelectMany(p => p.Answers))
                Assert.False(stageJson.Contains($"\n{code}\n"), "a final lock's code leaked");

            foreach (var seat in seats)
            {
                var mine = s.Pieces.Where(h => h.SeatId == seat).Select(h => room.FindPuzzle(h.PuzzleId)!.Pieces[h.Index]).ToHashSet();
                var phoneRaw = GameJson.Serialize(EscapeProjector.Player(s, template, seat, T0));
                Assert.DoesNotContain(seed.ToString(), phoneRaw);
                var phoneJson = ViewText.Decoded(phoneRaw);
                foreach (var piece in room.Puzzles.SelectMany(p => p.Pieces).Where(piece => !mine.Contains(piece)))
                    Assert.DoesNotContain(piece, phoneJson);
            }

            // Make the next move (search, look, combine or solve) and look again.
            s = EscapeEngine.Apply(s, template, EscapeBot.NextMove(s, played, seats[step % seats.Length], T0.AddMinutes(step)));
        }
        Assert.Equal(seed, EscapeProjector.Stage(s, template, T0).PuzzleSet); // shown once it's over, to replay or share
    }
}

/// <summary>Replays: the same room plays differently with every puzzle set, and the same set always plays the same.</summary>
public class ReplayTests
{
    public static TheoryData<string> RoomIds() => new(Rooms.Library.Select(r => r.Id));

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void A_thousand_puzzle_sets_per_room_are_all_valid_and_escapable(string id)
    {
        var room = Rooms.Get(id);
        for (var seed = 1000; seed < 2000; seed++)
            Assert.Empty(EscapeRoomValidator.ValidateGame(room, seed));
    }

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void The_same_puzzle_set_always_builds_the_same_room(string id)
    {
        var room = Rooms.Get(id);
        Assert.Equal(GameJson.Serialize(RoomVariants.Build(room, 31337)), GameJson.Serialize(RoomVariants.Build(room, 31337)));
        // A fresh copy of the room (as after a server restart) builds the identical puzzles too.
        var copy = GameJson.Deserialize<EscapeRoom>(GameJson.Serialize(room));
        Assert.Equal(GameJson.Serialize(RoomVariants.Build(room, 31337)), GameJson.Serialize(RoomVariants.Build(copy, 31337)));
    }

    [Fact]
    public void Different_puzzle_sets_give_different_codes_riddles_and_passwords()
    {
        string Answer(string room, string puzzle, long seed) => RoomVariants.Build(Rooms.Get(room), seed).FindPuzzle(puzzle)!.Answers[0];
        var seeds = Enumerable.Range(0, 60).Select(i => (long)i).ToList();
        Assert.True(seeds.Select(s => Answer("the-workshop", "toolbox", s)).Distinct().Count() > 40, "the toolbox code varies");
        Assert.True(seeds.Select(s => Answer("the-funhouse", "duck-pond", s)).Distinct().Count() > 40, "the duck-pond code varies");
        Assert.True(seeds.Select(s => Answer("the-funhouse", "carousel-animals", s)).Distinct().Count() > 40, "the carousel password varies");
        Assert.Equal(3, seeds.Select(s => Answer("the-workshop", "cabinet", s)).Distinct().Count()); // every hand-written riddle comes up
        Assert.Equal(3, seeds.Select(s => Answer("the-workshop", "trapdoor", s)).Distinct().Count());
        // A final lock's code is built for each game, from the puzzles it plays (#134).
        string Final(string room, string puzzle, long seed) => EscapeEngine.RoomFor(EscapeEngine.NewGame(seed), Rooms.Get(room)).FindPuzzle(puzzle)!.Answers[0];
        Assert.True(seeds.Select(s => Final("the-workshop", "exit-door", s)).Distinct().Count() > 40, "the door's final code varies");
    }

    [Fact]
    public void Generated_clues_add_up_to_the_answer()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var toolbox = RoomVariants.Build(Rooms.Get("the-workshop"), seed).FindPuzzle("toolbox")!;
            var digits = toolbox.Pieces.Select(piece => FactBank.Facts.Single(f => piece.Contains(f.Text)).Value);
            Assert.Equal(string.Concat(digits), toolbox.Answers[0]);
            Assert.DoesNotContain("{", toolbox.Hints[1]);

            var gate = RoomVariants.Build(Rooms.Get("the-funhouse"), seed).FindPuzzle("carousel-animals")!;
            Assert.Equal(gate.Answers[0].ToUpperInvariant(), gate.SolvedText[(gate.SolvedText.IndexOf(": ") + 2)..^1]);
            var ducks = RoomVariants.Build(Rooms.Get("the-funhouse"), seed).FindPuzzle("duck-pond")!;
            Assert.DoesNotContain("{order}", ducks.Prompt);
            Assert.Equal(3, ducks.Pieces.Count);
        }
    }

    /// <summary>
    /// A colour code's order names the room's own things: the Pirate Ship's chest used to ask for "Red duck, then Blue
    /// duck" while the phones counted gems (#136). With no thing named, the order is just the colours.
    /// </summary>
    [Fact]
    public void A_colour_order_names_what_the_room_counts()
    {
        foreach (var room in Rooms.Library)
        {
            foreach (var template in room.Puzzles.Where(p => p.Generator?.Type == GeneratorType.ColorDigits))
            {
                var thing = template.Generator!.Thing;
                Assert.False(string.IsNullOrWhiteSpace(thing), $"{room.Id}: name what '{template.Id}' counts");
                var built = RoomVariants.Build(room, 7).FindPuzzle(template.Id)!;
                var text = string.Join("\n", built.Hints.Prepend(built.Prompt));
                Assert.Contains($" {thing}, then ", text);
                if (thing != "duck") Assert.DoesNotContain("duck", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        var gems = Rooms.Get("the-pirate-ship");
        var chest = gems.FindPuzzle("treasure-chest")!;
        var plain = new PuzzleGenerator { Type = GeneratorType.ColorDigits, Count = 2, Colors = ["red", "blue"], PieceTemplate = chest.Generator!.PieceTemplate };
        var unnamed = gems with { Puzzles = gems.Puzzles.Select(p => p.Id == chest.Id ? p with { Generator = plain } : p).ToList() };
        Assert.Matches(@"'(Red, then Blue|Blue, then Red), or no treasure", RoomVariants.Build(unnamed, 7).FindPuzzle(chest.Id)!.Prompt);
    }

    /// <summary>
    /// A code or password dealt across the phones is a digit or word longer on Hard and shorter on Easy, so its text says
    /// {count}: a chest that promised a "3-digit lock" took four digits on Hard.
    /// </summary>
    [Fact]
    public void Codes_dealt_across_the_phones_say_how_long_they_are_at_every_difficulty()
    {
        var count = new System.Text.RegularExpressions.Regex(@"\b(\d|two|three|four|five|six|seven|eight|nine|ten)\b[- ](\w+-)?(digit|words?|phones|flag|code words|carousel|favourite|star-words)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string[] words = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
        foreach (var room in Rooms.Library)
        {
            foreach (var template in room.Puzzles.Where(p => p.Generator?.Type is GeneratorType.DigitFacts or GeneratorType.ColorDigits or GeneratorType.WordSequence))
            {
                foreach (var text in template.Hints.Prepend(template.Prompt).Append(template.SolvedText))
                    Assert.False(count.IsMatch(text), $"{room.Id}/{template.Id} says how long it is; write {{count}}: {text}");
                foreach (var level in Enum.GetValues<EscapeDifficulty>())
                {
                    var built = RoomVariants.Build(room, 3, level).FindPuzzle(template.Id)!;
                    var dealt = words[built.Pieces.Count];
                    foreach (var text in built.Hints.Prepend(built.Prompt))
                    {
                        Assert.DoesNotContain("{count}", text);
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\b(\w+)-digit"))
                            Assert.Equal(dealt, m.Groups[1].Value);
                    }
                }
            }
        }
        var chest = RoomVariants.Build(Rooms.Get("the-pirate-ship"), 3, EscapeDifficulty.Hard).FindPuzzle("treasure-chest")!;
        Assert.Contains("has a four-digit lock", chest.Prompt);
        Assert.Equal(4, chest.Answers[0].Length);
    }

    [Fact]
    public void Broken_generators_are_caught_before_anyone_plays()
    {
        var room = GameJson.Deserialize<EscapeRoom>(GameJson.Serialize(Rooms.Get("the-funhouse")));
        var gate = room.Puzzles.Single(p => p.Id == "exit-gate");
        room.Puzzles[room.Puzzles.IndexOf(gate)] = new EscapePuzzle
        {
            Id = gate.Id, Title = gate.Title, Kind = PuzzleKind.Code, Prompt = gate.Prompt, Requires = gate.Requires, Hints = gate.Hints, SolvedText = gate.SolvedText,
            Generator = new PuzzleGenerator { Type = GeneratorType.WordSequence, Count = 5, Words = ["horse", "lion"], PieceTemplate = "An animal." },
        };
        var errors = EscapeRoomValidator.Validate(room);
        Assert.Contains(errors, e => e.Contains("must be a Text puzzle"));
        Assert.Contains(errors, e => e.Contains("has only 2"));
        Assert.Contains(errors, e => e.Contains("{word}"));
    }
}
