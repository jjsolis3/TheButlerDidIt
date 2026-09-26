# Writing a mystery

A mystery is one JSON file in `content/themes/<theme-slug>/scenarios/`. Use `death-at-blackwood-manor.json` as a model. When the app starts (and in `dotnet test`), every file is checked by `ScenarioValidator`, and errors are listed by name.

## Editing in the browser

You don't have to edit JSON by hand. Signed-in hosts have **My mysteries** on the home page:

- **Edit** a mystery written by AI, or your own copy. There are forms for the story, characters, clues, acts and scenes, and the solution, plus a **JSON** tab for everything else (voices, puzzles, sound). The server re-checks the mystery as you type, with the same rules as below, and only saves it when it's playable.
- **Duplicate** makes an editable copy. Hand-written mysteries (from `content/`) can only be copied, because they're reloaded from the content folder at every start-up, which would undo an in-place edit.
- **Play-test** starts a pass-and-play party with the mystery on this device.
- **Delete** removes a mystery. If parties have already played it, it's hidden instead, so their recaps keep working. It's blocked while a party is using it.

When you change words that have a generated voice or picture, only those are made again; everything else keeps its media.

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
- **`killerEligible`** (optional, default `true`): set it to `false` for a character who must never be the killer in any version, like the child in a Family mystery. The validator enforces it, and the AI remix never picks them.
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

## Versions: a new killer for the same story

A story can have several versions. Each has the same place, victim and suspects, but a different killer, so a group can play it again. A version is a small file next to the original that holds only what changes:

```
content/themes/the-butler-did-it/scenarios/
  death-at-blackwood-manor.json      ← the original (Version A)
  death-at-blackwood-manor.b.json    ← Version B
  death-at-blackwood-manor.c.json    ← Version C
```

```json
{
  "variantOf": "death-at-blackwood-manor",
  "variant": "B",
  "solution": { "murdererId": "hargrove", "motiveId": "inheritance", "methodId": "candlestick", "explanation": [...], "timeline": [...] },
  "characters": { "hargrove": { "private": { "backstory": "YOU ARE THE MURDERER...", "alibi": "..." } } },
  "clues": { "candlestick": { "redHerring": false, "pointsTo": ["hargrove"] }, "some-old-clue": null },
  "addClues": [ { "id": "cellar-door", "title": "...", "text": "...", "act": 1, "pointsTo": ["hargrove"] } ],
  "acts": { "act3": { "cues": [...] } },
  "finale": [...]
}
```

- `characters`, `clues` and `acts` are keyed by id: name only the ones that change.
- Objects merge field by field, so `"private": { "alibi": "..." }` changes just the alibi. Lists (secrets, cues, lines, pointsTo…) are replaced whole.
- `null` removes a clue (or a field), and `addClues` appends new clues.
- Every version is expanded and checked by the same validator as a full mystery. The killer must be an essential character, with 3 or more genuine clues against them.
- **Checklist for a new version:**
  - Rewrite the new killer's backstory, goals and alibi.
  - Make the old killer innocent but still suspicious, and fix any lines of theirs that only made sense as the killer.
  - Aim enough clues at the new truth, and turn the old ones into red herrings.
  - Rewrite the solution, the timeline and any narration that names the killer.
  - Read it through once as a player.

Players never see which version they're playing: the title is shared, and screens show the original's id.

### How "Surprise me" deals a version

With **🎲 Surprise me** (the default), the version is dealt when the host presses **Begin the evening**, not when the party is created. By then everyone has a character, so the dealer:

1. runs Auto-assign, so the cast is final;
2. prefers a version this host has **never played**;
3. among those, prefers one whose **killer is a guest** tonight rather than the narrator;
4. otherwise picks at random.

This is only safe because versions never change anything guests see in the lobby: public bios, costumes, the setting and the prologue. Keep it that way when you write a version.

If no unplayed version's killer is a guest, and the host ticked **"let the AI write one"**, the AI Storyteller writes a new version as a patch, with a randomly chosen guest's character as the killer (see [ai-setup.md](ai-setup.md)). It must pass the same validator, and it's saved as one of that host's versions of the story (shown only to them) for later replays. Otherwise the narrator plays the killer.

A "Surprise me" party has no printed booklets or clue cards before it begins, since the killer isn't dealt yet (the phones carry everything). To print them, pick a specific version.

## Family or Adults?

Every mystery sits on one shelf, set by `contentRating`:

| | `family` | `mature` |
|---|---|---|
| Who | All ages, kids and teens included | Grown-ups |
| The crime | A murder that happens offstage, Cluedo-style: never gruesome | Described violence is fine, nothing graphic |
| Romance | A crush at most | Affairs and scandal, nothing explicit |
| Drink | None at all: toasts use lemonade, and drinking prompts are always off | Wine and cocktails, with a non-alcoholic alternative on every toast |
| Language | No swearing | Salty, not crude |

A test checks that no Family mystery mentions alcohol, so keep "rum", "wine" and friends out of pirate stories too.

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
- The murderer is `required` and not marked `killerEligible: false`.
- A **Family** mystery never mentions alcohol (whole words, so "ginger" is fine). This applies to text written in the editor or by the AI too.

## Tips for a good evening

- Give every character at least one secret that makes them look guilty.
- Put the decisive evidence in act 3 and the misleading evidence early.
- Keep `lines` short and fun to perform. They're what gets people acting.
- Around 20–25 minutes of mingling per act works well for 6–8 players.
