# Writing an escape room

An escape room is one JSON file in `content/escape/`. The app checks every room when it starts, and in the tests: a broken room, or one that can't be escaped, stops the app with a list of what's wrong. See `the-workshop.json` (Adults) and `the-funhouse.json` (Family) for complete examples.

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
  "stages":  [ { "id": "chains", "title": "The Chains", "description": "…", "puzzles": ["tape", "shackles"] } ],
  "puzzles": [ … ],
  "items":   [ { "id": "rusty-key", "name": "Rusty key", "description": "…" } ]
}
```

A stage opens when every puzzle in the stage before it is solved. Solving the last stage is the escape.

## Puzzles

| Field | Meaning |
|---|---|
| `kind` | `code` (digits only), `text` (a word or phrase) or `use` (no answer: use the items it needs). |
| `prompt` | What everyone sees on the TV and the phones. |
| `answers` | Accepted answers, for `code` and `text`. Matching ignores case, spaces, punctuation and a leading "a", "an" or "the", so list real alternatives ("footsteps", "footprints"), not spellings. |
| `requires` | Items the group must hold first. They're used up when the puzzle is solved. |
| `rewards` | Items the group gets when it's solved. Each item comes from exactly one puzzle. |
| `pieces` | Clue pieces dealt round the table when the clock starts. Each phone sees only its own, so the group has to talk. |
| `hints` | Revealed one at a time, each costing time. Every puzzle needs at least one. |
| `solvedText` | Shown when it's solved. Good for pointing at what just appeared. |

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

**Generators** build the clue pieces and the answer from the seed. The prompt, the hints and the solved text can use `{order}`, `{facts}` and `{answer}`.

| `type` | Makes | Piece template must use |
|---|---|---|
| `digitFacts` | A code whose digits are everyday facts ("the number of days in a week"), one fact per phone. The facts come from `FactBank`. | `{ordinal}`, `{fact}` |
| `colorDigits` | A code read from coloured objects in the order a sign gives (`{order}`). | `{color}`, `{digit}` |
| `wordSequence` | A password of words in order, one word per phone. It needs a `words` list. | `{ordinal}`, `{word}` |

```jsonc
"generator": { "type": "digitFacts", "count": 4, "pieceTemplate": "Written on your palm: the {ordinal} digit is {fact}." },
"hints": ["Every phone holds one digit. Read them out in order.", "In order: {facts}."]
```

The validator builds a templated room from 200 puzzle sets and checks each one, including that it can be escaped. The tests check 1,000 more.

## Leaderboards

When a game ends, its result is saved in the same step that ends the game. The **score** is the time taken plus the time each hint cost, and lower is better. The ending screen shows where the group ranked, all time or on today's challenge. Times are public. Team names only ever appear on the host's own escapes.

## Tips

- **Make the phones matter.** Put at least one puzzle with 3–4 `pieces` in each room. Write every piece so it only makes sense together with the others, for example "the SECOND digit is…".
- **Chain the rooms with items.** A key found in stage 1 opens something in stage 2 or 3. The validator proves no item is needed before it can be found.
- **Keep riddles fair.** They should have one clear answer. List every sensible alternative in `answers`.
- **Write hints as a ladder:** first a nudge, then something close to the answer.
- **Family rooms can be spooky, not gruesome.** The validator refuses words like "blood", "dead" or "kill".
