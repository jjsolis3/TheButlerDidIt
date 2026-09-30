# Writing an escape room

An escape room is one JSON file in `content/escape/`. The app checks every room when it starts, and in the tests: a broken room, or one that can't be escaped, stops the app with a list of what's wrong. See `the-workshop.json` for a complete example (walked through below, in *Worked example*).

## The shape

```jsonc
{
  "id": "the-workshop",              // also the file name
  "title": "The Workshop",
  "synopsis": "Shown on the shelf and in the lobby.",
  "contentRating": "mature",         // "mature" (Adults) or "family"
  "timeLimitMinutes": 45,
  "hintPenaltySeconds": 120,         // each hint takes this off the clock
  "minPlayers": 2, "maxPlayers": 6,
  "intro": "The villain's welcome, shown on the TV as the clock starts.",
  "escapedText": "Shown when the group gets out.",
  "failedText": "Shown when time runs out.",
  "gameMaster": {                    // optional: who the AI plays (default "The Game Master")
    "name": "The Tinkerer",
    "persona": "How it talks, in a sentence or two (under 400 characters).",
    "voice": { "accent": "en-US", "pitch": 0.7, "rate": 0.85, "style": "low, slow, measured" }
  },
  "stages":  [ { "id": "chains", "title": "The Chains", "description": "…", "puzzles": ["tape", "shackles"] } ],
  "puzzles": [ … ],
  "items":   [ { "id": "rusty-key", "name": "Rusty key", "description": "…" } ]
}
```

A stage opens when every puzzle in the stage before it is solved. Solving the last stage is the escape.

## Puzzles

| Field | Meaning |
|---|---|
| `kind` | `code` (digits only), `text` (a word or phrase), `use` (no answer: use the items it needs), `search` (no answer: solved by itself once every spot in `finds` has been searched) or `switches` (a light panel, set by a `switches` generator). |
| `prompt` | What everyone sees on the TV and the phones. |
| `answers` | Accepted answers, for `code` and `text`. Matching ignores case, spaces, punctuation and a leading "a", "an" or "the", so list real alternatives ("footsteps", "footprints"), not spellings. |
| `requires` | Items the group must hold first. They're used up when the puzzle is solved. |
| `rewards` | Items the group gets when it's solved. Each item comes from exactly one puzzle. |
| `pieces` | Clue pieces dealt round the table when the clock starts. Each phone sees only its own, so the group has to talk. |
| `hints` | Revealed one at a time, each costing time. Every puzzle needs at least one. |
| `solvedText` | Shown when it's solved. Good for pointing at what just appeared. |
| `finds` | For `search` puzzles: the ids of the scene spots, in the same stage, that must all be searched. |
| `minMinutes`, `minDifficulty` | Only played in games at least this long, or at least this hard (see below). |

## Scenes: spots to search, items to look at, things to put together

A real escape room is searched, not just solved. A stage can have a **scene**: a picture on a 1000 × 600 canvas with spots on it that players tap to search. What a spot holds stays on the server until someone searches it.

```jsonc
"scene": {
  "backdrop": "workshop",
  "objects": [
    { "id": "crate", "prop": "crate", "x": 40, "y": 380, "w": 160, "h": 160, "label": "crate",
      "look": "Straw, and a glass bulb.", "gives": "bulb" },
    { "id": "poster", "prop": "poster", "x": 440, "y": 60, "w": 160, "h": 220, "label": "poster",
      "look": "Glowing letters appear!", "clue": "On the poster, in UV ink: LOOK TWICE.",
      "requires": "uv-lamp", "lockedText": "Just a faded poster. Maybe in a different light?" },
    { "id": "plant", "prop": "plant", "x": 880, "y": 380, "w": 100, "h": 180, "label": "plant", "look": "A thirsty plant." },
    { "id": "rug", "prop": "rug", "x": 420, "y": 500, "w": 260, "h": 90, "label": "rug", "look": "Dust.", "hidesPieces": true }
  ]
}
```

| Spot field | Meaning |
|---|---|
| `id` | Unique in the whole room. |
| `prop` | What the screens draw: rug, painting, crate, pipe, bookshelf, clock, chest, barrel, lamp, window, desk, vent, poster, door, safe, plant, mirror, shelf, box, table, cabinet, statue, drawer, bed, sign or machine. |
| `x`, `y`, `w`, `h` | Where it is on the canvas. It must fit inside. |
| `label` | What everyone sees ("the rug"). |
| `look` | What the searcher finds. Only shown once searched. |
| `gives` | An item found there. |
| `clue` | A line written into the group's shared **notebook**. |
| `requires` | A tool needed to find anything (the UV lamp). Without it, the player sees `lockedText` and can come back later. Tools are never used up. |
| `hidesPieces` | A hiding place for clue pieces (see *Playing solo* below). |
| `minDifficulty` | Only in the scene at this difficulty or harder: `"hard"` adds decoys and red herrings for experts. |

A spot with only a `look` is a **decoy**. It's harmless on Easy and Normal, but on Hard searching it costs 10 seconds. So put anything useful in `gives` or `clue`, not only in the look.

**Items** can hide more:
- `inspect` is what a closer look shows ("Numbers are scratched inside the lid"). It goes into the notebook.
- `inspectRequires` is a tool needed for the closer look (a magnifying glass).
- `inspectGives` is an item found that way (the photo inside the locket).

**Recipes** (on the room) put two items together, and both are used up:

```jsonc
"recipes": [ { "items": ["bulb", "lamp-body"], "makes": "uv-lamp", "text": "The bulb screws in and the lamp glows violet." } ]
```

Each item comes from exactly one place: a puzzle's rewards, a spot, a closer look or a recipe. An item is either **used up**, by one puzzle or one recipe, or it's a **tool**, needed to search a spot or look at an item. It can't be both. That way no order of play can leave the group stuck, and the validator's play-through is exact.

## Replays: variants and generators

A room plays differently every time. When a party is created, the server picks a **puzzle set**, a number used as the seed. `RoomVariants.Build(room, seed)` then fixes every puzzle for that game:
- The same room and seed always build exactly the same puzzles.
- Every game ends by showing its puzzle-set number, so a group can replay it or challenge friends with it.
- "Today's challenge" gives every group the same set that day.

There are two ways to make a puzzle vary:

**Variants** are hand-written alternatives. One is picked per game, and fields a variant leaves out keep the puzzle's own values. `{}` means "the puzzle as written".

```jsonc
"variants": [
  {},
  { "prompt": "…'The more you take away from me, the bigger I get.'", "answers": ["hole"], "hints": ["Think about digging.", "You dig one in the ground."] }
]
```

**Generators** build the clue pieces and the answer from the seed. The prompt, the hints and the solved text can use `{order}`, `{facts}` and `{answer}`, and the newer generators add their own placeholders.

| `type` | Kind | Makes | Needs |
|---|---|---|---|
| `digitFacts` | code | A code whose digits are everyday facts ("the number of days in a week"), one fact per phone. The facts come from `FactBank`. | A piece template with `{ordinal}` and `{fact}` |
| `colorDigits` | code | A code read from coloured objects in the order a sign gives (`{order}`). | A piece template with `{color}` and `{digit}` |
| `wordSequence` | text | A password of words in order, one word per phone. | `words`, and a piece template with `{ordinal}` and `{word}` |
| `cipher` | text | A word from `words` in code, shown with `{cipher}` in the prompt. `cipher` is `shift`, `symbols`, `morse`, `numbers` (A=1…Z=26) or `mirror` (A↔Z). | For `shift`, `symbols` and `morse`, write `{key:<puzzle id>}` somewhere the group has to find it: a spot's look or clue, an item's description or closer look, or another puzzle's prompt or piece. The validator checks it can be found in time. |
| `sequence` | code | A number pattern, shown with `{sequence}` ("3, 7, 11, 15, 19, ?"). The code is the next number. | Nothing else |
| `deduction` | code | A logic puzzle: the things in `words` stand in a row, and each clue piece says something about where they are ("The red jar is right next to the gold jar"). There's exactly one arrangement, and every clue is needed. The code is each thing's spot (1 = far left), in the order `{items}` lists them. | `{items}` in the prompt, at least 3 `words`, and a piece template with `{clue}` (optional) |
| `switches` | switches | A light panel: pressing a light flips it and its neighbours, and every light must be on. It's made by pressing lights from all-on, so it can always be solved. `{answer}` names the lights to press, for a last hint. | Nothing else |

```jsonc
"generator": { "type": "digitFacts", "count": 4, "pieceTemplate": "Written on your palm: the {ordinal} digit is {fact}." },
"hints": ["Every phone holds one digit. Read them out in order.", "In order: {facts}."]
```

The validator builds a templated room from 200 puzzle sets and checks each one, including that it can be escaped. The tests check 1,000 more, and prove every generator fair over 1,000 seeds: each cipher decodes with its key as written, each pattern follows its rule, each logic puzzle has exactly one answer, and each light panel can be solved.

## How it plays

What players see and tap for each of the newer pieces:
- **A scene** is the stage's picture on the TV and on every phone, with each spot drawn from its `prop` over the stage's painted art (or a backdrop for `backdrop`). On a phone, tapping a spot searches it, and the phone says what was there. A spot that needs a tool shows the room's `lockedText`, and a decoy on Hard shows the 10-second penalty. Phones can zoom the picture 2× or 3×. The TV lists what has been found under the picture.
- **Items** are buttons in "The group is carrying". Tap one to read it, **Look closer** (when there's more to see), or try it with each of the other items. Dragging one item onto another does the same.
- **The notebook** shows on the TV and the phones, newest first.
- **Search** puzzles show how many of their spots have been searched. **Light panels** are a grid of buttons on the phones, and the TV shows the lights as they are.
- **Ciphers** get a decoder on the phones once their key has been found: a letter wheel for `shift` (turn it by the number the group found), a key card for `symbols` and `morse`, and an alphabet strip for `mirror` and `numbers`. The shift amount itself is never sent: finding it is the puzzle.
- **Logic puzzles** get a grid to mark ✓ and ✗, and a line-up that turns an order into the code. Both are a scratch pad on that phone only.
- **Number patterns** show their terms large above the keypad.
- **Hidden clue pieces:** the TV counts the ones still hidden, and a found piece says where it was found.

## Difficulty

A host picks **Easy, Normal or Hard**, and each has its own leaderboard. **Normal plays the room exactly as written.**

| | Easy | Hard |
|---|---|---|
| `digitFacts`, `colorDigits`, `wordSequence` | One piece fewer (never below 2) | One piece more (while there's enough to choose from) |
| `cipher` | The shorter half of `words` | The longer half of `words` |
| `sequence` | Steady steps | Growing steps, two patterns woven together, or each term the sum of the two before |
| `deduction` | 3 things | 5 things (Normal: 4) |
| `switches` | 3 × 3, two presses away | 4 × 4, six presses away (Normal: 3 × 3, four) |
| Hints | Cost half the time | The last step of every ladder with two or more is dropped, so hints only nudge |
| Decoy spots | Free | Cost 10 seconds |

**`minDifficulty`** on a puzzle keeps it only at that difficulty or harder: `"hard"` makes it an extra for experts. The validator plays every length at every difficulty.

## Playing solo: hiding spots

With fewer players than a puzzle has pieces, pieces used to double up on phones: a solo player held them all. Now, if the puzzle's stage has spots marked `hidesPieces`, the pieces nobody would have held are **hidden in those spots** instead, one piece per spot, and the seed picks which. Whoever searches the spot gets the piece on their phone, and the TV shows how many are still hidden.

The validator checks there are enough hiding spots for a solo player: at least one per piece beyond the first, for every puzzle in the stage (remember Hard adds a piece). A stage with no hiding spots deals every piece to a phone, as before.

## Lengths and seasons

- **`lengths`** lists the game lengths a host can pick: `[30, 45, 60]`, or any of those that include the room's `timeLimitMinutes` (its standard game). Leave it out and the room has one length.
- **`minMinutes`** on a puzzle keeps it only for games at least that long. For example, `45` leaves it out of a 30-minute game, and `60` makes it an extra for the extended cut. A shorter game is a shorter room, with the clock set to its length. A stage left with no puzzles is skipped.
- **The validator plays every length through**, 200 puzzle sets each. So a quicker game can never need a key that only a left-out puzzle gives. Cut puzzles in pairs, with the puzzle that gives an item and the one that uses it. The shortest game must keep at least 4 puzzles.
- Each length has its own leaderboard, because a 30-minute game plays fewer puzzles.
- **`seasons`**: `["halloween"]` puts the room under the 🎃 Halloween filter on its shelf (Adults or Family, from `contentRating`), just like a mystery theme's `seasons`.
- Don't mention the clock in the room's texts ("you have forty-five minutes"): the length is the host's choice.

## Sound and pictures

- **`soundscape`** (on the room, and optionally on a stage) sets the background sound on the TV: `drone` (the default), `workshop`, `carnival`, `sea`, `space`, `haunted` or `silence`. The sound is made live in the browser, so there are no audio files to add. The TV also plays short sounds when a lock opens, a new room opens, a code is wrong or a hint is bought, then a gong and a heartbeat in the final minute.
- **`artStyle`** describes the look. With an image model set up, the room's cover and each stage are painted once, from the title, synopsis and stage descriptions only. So write stage descriptions that paint a picture, and never put an answer in them unless you mean it to be hidden in plain sight.

## The AI game master

When the host keeps **Use the AI game master** on (and an admin has set up the AI), the room's `gameMaster` comes alive:
- It **reacts out loud on the TV** to the start, solves, new rooms, a run of wrong answers, the last five minutes, and the ending. It speaks in its own voice if a Voice model is set up; otherwise the TV's browser reads the line.
- It **writes the hints**. It sees the puzzle, the clue pieces on the group's phones, the spots they've searched and what they found, the items they've looked at closely, their notebook and their wrong tries (never an unsearched spot or a hidden piece), plus your written hint for that step as the direction to nudge in. It never sees the answer. The app also checks every AI hint, and shows your written hint instead if the AI's one gives the answer away or doesn't arrive. So keep writing a good hint ladder: it is both the AI's guide and the fallback.

The AI only writes words. It never changes a puzzle, an answer or the clock.

## Rooms written by AI

With a Storyteller model set up, a host can type a theme on the escape shelf ("a haunted lighthouse") and get a new room in this same format, on their own shelf only. The AI writes the story, the riddles and the villain, and it picks which generators fill the codes and passwords. So every code comes from the same proven templates as yours, and nothing it writes is saved unless the validator passes. A tester AI also has to crack each riddle from its prompt and pieces alone. A good hand-written room is still the best model: the AI is shown this format and follows the same rules. For now the AI writes only codes, riddles and use puzzles; scenes, ciphers, patterns, logic puzzles and light panels are hand-written until it's taught them (#86).

## Editions

`edition` (default 1) says which version of the room this is. When you rebuild a room so that old times no longer compare (new puzzles, a different number of them), bump it: each edition gets its own leaderboards. Old results stay in the database; they just drop off the boards and the shelf's best time. Small fixes (a typo, a better hint) don't need a new edition.

## Worked example: the Workshop, rebuilt

The Workshop (edition 2) keeps its story, villain and riddles, and wraps them in things to search, look at and put together. Here's its first stage, step by step:

1. **A kept riddle** (the tape recorder, with its variants) gives a **rusty key**. It's rusted solid.
2. **A search puzzle**, *The Floor*, needs the drain, the loose tiles and the crate searched. It gives an **oil can**.
3. **A recipe** puts the rusty key and the oil can together: the **oiled key**.
4. **The shackles** are the stage's one `use` step, gated on the oiled key, so they're never a one-tap freebie.
5. **A logic puzzle**, *The Oil Drums* (`deduction`), deals its clues to the phones, or hides them in the stage's `hidesPieces` spots when fewer players join. It gives a **UV torch**, a tool that's never used up.
6. **A shift cipher**, *The Tape Label* (`minMinutes: 45`), has its key on the cassette recorder: `"look": "…'Wind me back {key:rewind}.'"`.

The torch comes back later. In stage 2, the photo halves from the workbench and the drawer make a photograph, and its closer look (`inspectRequires: "uv-torch"`) holds the key to the symbols cipher. On Hard, a notebook spot that only appears on Hard (`minDifficulty: "hard"`, `requires: "uv-torch"`) holds the key to an extra Morse puzzle. A Hard-only coat hook holds a broken key that fits nothing: a red herring.

The scene is laid out on the 1000 × 600 canvas with spots that don't overlap:
- two decoys, the pipes and the bare bulb, whose looks add atmosphere;
- five hiding places, enough for a solo player's extra clue pieces at every difficulty;
- the spots that matter.

Across lengths and difficulties it plays 9 puzzles at 30 minutes, 13 at 45 and 15 at 60, plus Hard's extra. The validator plays every one of those through, 200 puzzle sets each, and the content tests check the room clears the bar for shipped rooms (`ContentBarTests`), including that no cipher word is already written somewhere in the room.

The Family rooms (edition 2) follow the same shape with gentler parts: the Pirate Ship's apple-barrel riddle gives a ladle, the ladle fishes the galley key out of the stew pot, and a spyglass put together from a lens (a logic puzzle about the cook's pots) and a tube (a search) reads the shift cipher's key off a buoy far out at sea. Their ciphers are numbers (A = 1), mirror and shift with short words; symbols and Morse only turn up on Hard. Easy deals smaller logic puzzles and number patterns.

## Leaderboards

When a game ends, its result is saved in the same step that ends the game. The **score** is the time taken plus the time each hint cost, and lower is better. The ending screen shows where the group ranked, all time or on today's challenge. Times are public. Team names only ever appear on the host's own escapes.

## Tips

- **Keep cipher words out of the room's own text.** If "shelter" is in the synopsis, a cipher that spells SHELTER is answered before it's decoded (and the AI's prompts would carry it too). The content tests catch this.
- **Keep answers out of the screens' words.** A riddle whose answer is "clock" can't have a spot drawn as a `clock` prop, and one answered "table" collides with the `"table"` a cipher's view carries. Nor can a hint repeat a prompt word for word. The privacy tests catch all three.
- **Make them search.** A scene with a few decoys, a tool that reveals something, and a key written somewhere unexpected feels like a real room. Codes the group has to work out (a cipher, a pattern, a logic puzzle) beat codes read off a phone.
- **Make the phones matter.** Put at least one puzzle with 3–4 `pieces` in each room. Write every piece so it only makes sense together with the others, for example "the SECOND digit is…".
- **Chain the rooms with items.** A key found in stage 1 opens something in stage 2 or 3. The validator proves no item is needed before it can be found.
- **Keep riddles fair.** They should have one clear answer. List every sensible alternative in `answers`.
- **Write hints as a ladder:** first a nudge, then something close to the answer.
- **Family rooms can be spooky, not gruesome.** The validator refuses words like "blood", "dead" or "kill".
