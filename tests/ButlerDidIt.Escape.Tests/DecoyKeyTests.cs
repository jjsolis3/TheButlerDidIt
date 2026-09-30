using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// Decoy keys: when a room writes a cipher's key in several places, one holds the real key (picked by the seed
/// among the places shown at that difficulty) and the rest hold decoys, so the group has to find them all and work
/// out which reads right. The Laboratory writes the blackboard's shift in 3 places (1 on Easy, 2 on Normal, 3 on Hard)
/// and the Hard-only telegraph's Morse card in 3.
/// </summary>
public class DecoyKeyTests
{
    private static EscapeRoom Room => Lab.Room;
    private static readonly string[] SignalDecoys = ["PLANET", "CASTLE", "GARDEN", "WINTER", "LEADER", "HEATER"];

    private static EscapeRoom Played(long seed, EscapeDifficulty difficulty) => RoomLengths.Cut(RoomVariants.Build(Room, seed, difficulty), null, difficulty);

    [Theory]
    [InlineData(EscapeDifficulty.Easy, 1)]
    [InlineData(EscapeDifficulty.Normal, 2)]
    [InlineData(EscapeDifficulty.Hard, 3)]
    public void Each_difficulty_shows_its_number_of_keys(EscapeDifficulty difficulty, int keys)
    {
        var room = Played(5, difficulty);
        Assert.Equal(keys, EscapeRoomValidator.VisibleKeyPlaces(room, room.FindPuzzle("formula")!).Count);
    }

    [Fact]
    public void One_key_is_real_and_the_seed_moves_it()
    {
        var realPlaces = new HashSet<string>();
        for (var seed = 0; seed < 60; seed++)
        {
            var room = Played(seed, EscapeDifficulty.Hard);
            var formula = room.FindPuzzle("formula")!;
            var d = formula.Decoder!;
            var shown = d.Candidates!.Where(c => EscapeRoomValidator.VisibleKeyPlaces(room, formula).Contains(c.Place)).ToList();
            var real = Assert.Single(shown, c => c.Real);
            realPlaces.Add(real.Place);
            Assert.Equal(d.Key, real.Key);
            Assert.Equal(formula.Answers[0], PuzzleGenerators.Decode(d.Type, d.Encoded, real.Key), ignoreCase: true);
            // A wrong amount reads as something else, and no two places show the same amount.
            Assert.All(shown.Where(c => !c.Real), c => Assert.NotEqual(formula.Answers[0].ToUpperInvariant(), PuzzleGenerators.Decode(d.Type, d.Encoded, c.Key)));
            Assert.Equal(shown.Count, shown.Select(c => c.Key).Distinct().Count());
        }
        Assert.True(realPlaces.Count > 1, "the real key isn't always in the same place");
    }

    [Fact]
    public void Each_place_shows_the_key_written_there()
    {
        var room = Played(9, EscapeDifficulty.Hard);
        foreach (var c in room.FindPuzzle("formula")!.Decoder!.Candidates!.Where(c => c.Place.StartsWith("object:")))
            Assert.Contains(c.Key, room.SceneObjects.Single(o => $"object:{o.Id}" == c.Place).Look);
    }

    [Fact]
    public void Wrong_morse_cards_read_real_decoy_words()
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var signal = Played(seed, EscapeDifficulty.Hard).FindPuzzle("signal")!;
            var d = signal.Decoder!;
            Assert.Equal(3, d.Candidates!.Count);
            foreach (var c in d.Candidates!)
            {
                var reads = PuzzleGenerators.Decode(d.Type, d.Encoded, c.Key);
                if (c.Real) Assert.Equal(signal.Answers[0], reads, ignoreCase: true);
                else Assert.Contains(reads, SignalDecoys);
                // Every card lists the message's codes plus two spares, so a decoy card looks like the real one.
                Assert.Equal(d.Encoded.Split(' ').Distinct().Count() + 2, PuzzleGenerators.KeyTable(d.Type, c.Key).Count);
            }
        }
    }

    [Fact]
    public void The_phones_list_every_key_found_and_a_decoy_word_is_just_a_wrong_answer()
    {
        var s = Lab.Started(EscapeDifficulty.Hard, 21);
        var t0 = Lab.T0;
        foreach (var spot in new[] { "painting", "ledge", "notebook" }) s = s.Do(new ExamineSpot(t0, Lab.Ada, spot));
        var view = EscapeProjector.Stage(s, Room, t0).Puzzles.Single(p => p.Id == "formula").Cipher!;
        Assert.Equal(["portrait", "chalk ledge", "notebook"], view.Keys.Select(k => k.From).OrderBy(f => f == "portrait" ? 0 : f == "chalk ledge" ? 1 : 2));
        Assert.All(view.Keys, k => Assert.NotNull(k.Shift));

        var formula = EscapeEngine.RoomFor(s, Room).FindPuzzle("formula")!;
        var decoy = formula.Decoder!.Candidates!.First(c => !c.Real);
        var before = s.WrongAttempts;
        s = s.Do(new SubmitAnswer(t0, Lab.Ada, "formula", PuzzleGenerators.Decode(formula.Decoder.Type, formula.Decoder.Encoded, decoy.Key)));
        Assert.False(s.IsSolved("formula"));
        Assert.Equal(before + 1, s.WrongAttempts);
    }

    [Fact]
    public void Ciphers_that_need_no_key_still_get_their_decoder_on_the_phone()
    {
        // The Workshop's mirror note: no key anywhere, so the phone shows the folded alphabet from the start.
        var workshop = Rooms.Get("the-workshop");
        var mirror = RoomVariants.Build(workshop, 5).FindPuzzle("mirror-note")!;
        Assert.Equal(CipherType.Mirror, mirror.Decoder?.Type);
        Assert.Empty(mirror.KeyAt);
        Assert.Null(mirror.Decoder!.Candidates);
    }
}
