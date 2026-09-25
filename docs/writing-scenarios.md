# Writing a mystery

A mystery is one JSON file in `content/themes/<theme-slug>/scenarios/`. Use `death-at-blackwood-manor.json` as a model. When the app starts (and in `dotnet test`), every file is checked by `ScenarioValidator`, and errors are listed by name.

## Shape of a scenario

| Field | What it is |
|---|---|
| `id`, `themeSlug`, `title`, `synopsis` | Identity and the blurb shown when choosing a mystery |
| `contentRating` | `mature` or `family` |
| `minPlayers`, `maxPlayers` | Guests needed / allowed. `maxPlayers` can't exceed the number of characters. |
| `murdererKnows` | If `true`, the murderer's phone tells them. If `false`, even the killer is in the dark. |
| `setting`, `victim` | The scene and the body |
| `characters[]` | The suspects (see below) |
| `clues[]` | Evidence released during the acts |
| `prologue[]` | Cues played after the cast is introduced |
| `acts[]` | Each act has a cinematic (`cues`), a mingle timer (`mingleMinutes`) and conversation `prompts` |
| `accusation` | The multiple-choice `motives` and `methods` players pick from |
| `solution` | `murdererId`, `motiveId`, `methodId`, the `explanation` paragraphs revealed one by one, and a `timeline` |
| `finale[]` | Cues played at the very end |

## Characters

```json
{
  "id": "hargrove",
  "name": "Mr. Alistair Hargrove",
  "pronouns": "he/him",
  "title": "The Butler",
  "publicBio": "What everyone knows.",
  "costumeTips": "What to wear.",
  "voice": { "accent": "en-GB", "pitch": 0.8, "rate": 0.9, "style": "dry" },
  "required": true,
  "private": {
    "backstory": "Only this player reads this.",
    "secrets": [{ "id": "hargrove-brother", "text": "…", "unlockAct": 0 }],
    "objectives": ["Goals for the evening"],
    "knows": ["Facts they witnessed"],
    "alibi": "Where they claim to have been",
    "lines": { "act1": ["A line to say aloud in act 1"] }
  }
}
```

- **`required`**: essential characters. If no guest takes one, it becomes an **NPC** and the narrator speaks its `lines` at the end of each act's cinematic. Optional characters are simply left out. **The murderer must be required.**
- **`unlockAct`**: `0` means known from the start; `2` means the secret appears on their phone when act 2 begins.
- **`lines`** are keyed by act id. Write them as things said aloud *to the room*: they're shown to the player, or voiced on stage for NPCs.

## Clues

```json
{
  "id": "medical-bag",
  "title": "The Doctor's Bag",
  "text": "What the clue says.",
  "visibility": "public",
  "act": 2,
  "wave": "start",
  "pointsTo": ["finch"],
  "redHerring": false
}
```

- **`visibility`**: `public` appears on the stage. `private` goes only to the `recipient` character's phone, and they choose whether to share it.
- **`wave`**: `start` drops when the act's mingle begins; `midway` drops halfway through the timer (the host can drop them early).
- **`pointsTo`** / **`redHerring`**: never shown to players. The validator uses them to prove the mystery is fair.
- **Write private clues so they still make sense publicly.** If the recipient isn't being played, the clue goes public as "found among X's belongings". Write "Mrs. O'Malley's ledger: …" rather than "You saw…".
- **Puzzles**: add `"puzzle": { "prompt", "answers": [...], "hint", "solvedText" }`. Answers are compared ignoring case and punctuation. `solvedText` is revealed once someone cracks it.

## Cues (cinematics)

| `type` | Uses | Notes |
|---|---|---|
| `image` | `src`, `text` (caption), `effect: "kenburns"` | With no `src`, a candlelit placeholder shows the caption |
| `narration` | `text`, optional `src` (audio file) | Without `src`, the browser's speech synthesis reads it |
| `line` | `speaker` (character id), `text` | Only played when that character is an NPC |
| `music` | `src` | Loops quietly under the scene |
| `sfx` | `src` | One-shot sound |
| `video` | `src` | Plays full-screen; the scene continues when it ends |
| `toast` | `text`, `alternative` | A drinking-game moment, shown only when the host turned on toast prompts (never in Family parties). Always give a non-alcoholic `alternative`. One per act is plenty. |

**You don't have to supply media.** If an AI Voice or Illustrator is set up, empty `portrait`, image `src` and narration/line audio are generated automatically when a party is created. Anything you do supply is always used instead.

Media paths are served from the theme's `media/` folder:
`"src": "/media/themes/the-butler-did-it/scenes/study.jpg"` → `content/themes/the-butler-did-it/media/scenes/study.jpg`.

## Cocktails (theme.json)

Each theme can suggest drinks, shown in the lobby when toast prompts are on:

```json
"cocktails": [
  { "name": "The Butler's Revenge", "recipe": "Gin, lemon, honey, a dash of bitters", "mocktail": "Lemon, honey and tonic" }
]
```

## Fairness rules the validator enforces

- Every id reference points to something real (characters, acts, motives, methods, clue recipients, line speakers).
- At least **3 genuine clues** (not red herrings) point at the murderer.
- Clues cast suspicion on at least **2 innocent characters**.
- Every character has a public bio and an alibi. The solution has an explanation.

## Tips for a good evening

- Give every character at least one secret that makes them look guilty.
- Put the decisive evidence in act 3 and the misleading evidence early.
- Keep `lines` short and fun to perform. They're what gets people acting.
- Around 20–25 minutes of mingling per act works well for 6–8 players.
