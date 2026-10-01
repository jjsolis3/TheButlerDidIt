# Architecture: how it works and why

This guide walks through the design decisions, so you can extend the game with confidence. File references point to where each idea lives in the code.

## The big picture

```
 Phones (dossiers)        TV / shared screen (stage)
        │  SignalR WebSocket           │
        └───────────────┬──────────────┘
                        ▼
            ASP.NET Core  ── PartyHub (real-time)
                        │    REST endpoints (join, create party, auth)
                        ▼
                  PartyService ──► GameEngine.Apply (pure rules)
                        │          ViewProjector (what each screen may see)
                        ▼
                   PostgreSQL (EF Core)
```

One Docker container runs the API, the real-time hub and the built React app. PostgreSQL runs next to it.

## 1. The server is the only source of truth

Every rule is enforced on the server: whose turn, which clues have dropped, who the murderer is. Browsers only display what they're sent and ask the server to do things.

**Why:** anything sent to a browser can be read in its developer tools. If the phone received all the secrets and merely *hid* the ones you shouldn't see, anyone could cheat. So the server builds a separate **view** for each screen:

- `ViewProjector.Stage(...)` returns the public `StageView` (TV screen).
- `ViewProjector.Player(...)` returns a `PlayerView` for one seat: the public view plus *only that seat's* secrets.

Views are separate types (`Views.cs`) rather than filtered copies of the scenario. A view can only contain fields someone deliberately copied into it, so adding a field to the scenario later can't leak it by accident. `ViewProjectorTests` serializes every view in every phase and fails if forbidden text appears.

## 2. The rules are a pure function

```csharp
GameState next = GameEngine.Apply(current, scenario, command);
```

`GameEngine` (in `src/ButlerDidIt.Game/Engine`) has no database, no web code and no clock. Commands carry the current time (`Now`) as data.

**Why:**

- **Testable.** A test builds a state, applies a command and checks the result, in microseconds and with no mocks (`GameEngineTests`).
- **Safe.** `Apply` works on a deep copy. If a rule check throws halfway through, the original state is untouched and nothing is saved.
- **Reusable.** In milestone 2 the AI generator can simulate a whole game to check a mystery is solvable, with no server running.

Rule violations throw `GameRuleException` with a message that is safe to show players ("Pick a motive."). The API turns these into HTTP 400s and SignalR `HubException`s.

## 3. Saving state: a JSON document plus relational tables

| Data | Stored as | Why |
|---|---|---|
| Users, seats, notes | Normal tables | Looked up individually (login, seat token), need indexes and constraints |
| Scenario | `jsonb` document | One nested document, always read whole, the same shape for hand-written and AI-generated mysteries |
| Game state | `jsonb` on the `Parties` row | The engine produces a whole new state per command; saving it in one column is atomic |

`jsonb` is still queryable with SQL, e.g. `SELECT "Code", "State"->>'phase' FROM "Parties";`.

## 4. Concurrency: two taps, one change

If the host double-taps **Next** on slow Wi-Fi, two commands arrive together. Without protection both would read act 1 and both would write act 2, losing a step or dropping clues twice. Two layers prevent that (`PartyService.cs`):

1. **A lock per party** (`PartyLocks`, a `SemaphoreSlim` per party id): commands for one party run one at a time; different parties never wait on each other. With several servers (`Scale:MultiInstance`), the command also takes the party's `ClusterLock`, a Postgres advisory lock, so a tap that reaches another server waits too (see section 13).
2. **Optimistic concurrency** (`Party.Version` mapped to Postgres' `xmin`): if two saves ever do race, the second fails instead of silently overwriting.

## 5. Real-time updates with SignalR

`PartyHub` is the real-time channel. SignalR picks the best transport (WebSockets, falling back to long polling), and organises connections into **groups**:

- `stage:{partyId}`: every screen watching the stage
- `seat:{seatId}`: one guest's phone(s)
- `user:{userId}`: a host's pages that are following a background job (`WatchMyJobs`)

After every command, `PartyService.BroadcastAsync` sends a **complete snapshot** (not a diff) to each group. Snapshots are a few KB, and a phone that missed messages while asleep fixes itself with the next one.

Two lighter messages sit beside the snapshots:
- `npcTyping {interrogationId, text}`: an NPC's answer so far, while the AI is still writing it, sent to the stage and every seat at most every 150 ms. It isn't saved; the finished answer arrives in the next snapshot, and screens show `answer ?? typing[id]`, so the finished answer always wins.
- `jobs`: sent to `user:{userId}` whenever one of that host's mystery or media jobs changes. It carries no data: the page re-fetches the job through its normal, access-checked endpoint, and still polls every 15 seconds in case a signal is lost.

On the client (`src/web/src/lib/hub.ts`):

- Automatic reconnect retries forever with growing delays.
- After a reconnect the server sees a *new* connection that belongs to no groups, so the client calls `WatchParty` / `JoinSeat` again, which also returns a fresh snapshot.
- Every view has a `version`; older messages that arrive late are ignored.

**Timers** aren't broadcast every second. The server sends when the timer ends (`EndsAt`) plus its own clock (`ServerNow`); each device counts down locally, corrected for its own clock being wrong (`lib/clock.ts`). The server only needs to wake up for the midway clue drop, which `PartyTicker` handles by querying the indexed `NextDueAt` column every 2 seconds.

## 6. Two kinds of identity

| Who | How they prove it | Where |
|---|---|---|
| **Host** | Email + password → encrypted, HttpOnly auth cookie (ASP.NET Core Identity) | `AuthEndpoints.cs` |
| **Guest** | A random 256-bit **seat token** issued on joining, saved in the phone's `localStorage` | `SeatTokens.cs` |

Guests don't need accounts. The 6-letter party code only gets you to the join page (and joins are rate-limited). The seat token is what proves you are "Bob" afterwards. The database stores only a SHA-256 hash of each token, like a password. Because the token lives in `localStorage`, a phone that sleeps, refreshes or drops off Wi-Fi rejoins the same seat.

SignalR sends the token as `Authorization: Bearer …`. For WebSockets, browsers can't set headers, so it goes in the `access_token` query string, which the server only accepts on `/hubs` paths.

The hub accepts both identities at once (`AuthPolicies.PartyMember`), so the host's device can be the stage *and* a pass-and-play seat.

**The host's own account** (`AccountEndpoints.cs`, #98). The header's account menu (`AccountMenu`, on every page but the pass-and-play screen) leads to `/account`:
- **Every endpoint acts on the signed-in host.** No request carries a user id, so there's nothing to change to reach someone else's account.
- **The current password guards what could lock the owner out** (email, password, deleting). It's checked with lockout on (`CheckPasswordSignInAsync`), so someone with a stolen session can't guess it faster than on the sign-in page.
- **A new email address must confirm itself** with a link sent there (`GenerateChangeEmailTokenAsync`), and the old address is told. The email and the sign-in name change together in one transaction. A server without email changes it at once, because there's nothing to confirm with.
- **Other devices are signed out** by a new security stamp: a new password, a new email or "Sign out everywhere else". Each browser re-checks its stamp every `Auth:SessionCheckSeconds` (60, rather than Identity's default 30 minutes), and the device that made the change is signed in again (`RefreshSignInAsync`).
- **Download my data** is a JSON file of the account, its parties, mysteries, rooms, escapes and monthly AI use. It leaves out guests' names and notes, which belong to the guests.
- **Deleting** an account removes its parties (with seats, notes and selfie files, via `RetentionWorker.DeletePartyAsync`), its own mysteries and rooms, and their art. Leaderboard times and AI costs are kept without the name. The admin account can't be deleted.

**Invites** (`InviteEndpoints.cs`, #97). With `Auth:AllowRegistration=false`, a new host needs an invite link from the admin (`/login?invite=…`):
- **Stored like seat tokens.** The link carries a random 256-bit token, and the database keeps only its SHA-256 hash. The admin's list never shows a link again, and a copy of the database can't be used to sign up.
- **Used once, in the sign-up's own transaction.** `POST /api/auth/register` claims the invite with one `UPDATE … WHERE UsedAt IS NULL` inside the transaction that creates the account. The UPDATE locks the row, so if two people use one link at the same moment, the second waits, then finds it used. If creating the account fails (a weak password, an email already taken), the rollback leaves the invite unused.
- **Optional limits:** an invite can be for one email address only (compared the way Identity normalises emails), and it expires after 1 to 30 days.
- **No separate "closed" mode.** An invite works whether or not sign-ups are open. An admin who wants no new hosts simply makes no invites.

## 7. Content: themes and scenarios

`content/themes/<slug>/` holds `theme.json` (palette, era, art style), `scenarios/*.json` (the mysteries) and `media/` (images, audio, video). On startup, `ContentCatalog.SeedAsync` validates every scenario (`ScenarioValidator`) and upserts them into the database. A broken mystery fails the deploy, not a party.

Only `media/` folders are served over HTTP (`MediaEndpoints.cs`), with a path check against `../` tricks. The scenario JSON sits next door and contains the solution.

## 8. The front end

- **Pages** (`src/web/src/pages`): `Home`, `Login`, `NewParty`, `Join`, `Stage` (TV + host controls), `Play` (phone), `PassAndPlay`.
- **Front doors** (#99): `/` (`Home`) offers both games, with a card for each in that game's colours, plus "Your parties". Each game has its own page for visitors who haven't signed in: `/mystery` (`MysteryLanding`: the themes) and `/escape` (`EscapeLanding`: how it works, then the room shelf with filters and each room's leaderboards). Each also has a printable how-to-play sheet, `/how-to-play` and `/how-to-play/escape`. "Host" links go through sign-in with `?next=`, so a visitor lands on the host page with the room they picked.
- **`PlayerScreen`** is the whole phone experience. Pass-and-play reuses it for each local seat.
- **`CuePlayer`** plays cinematics: a list of cues (image, narration, NPC line, music, sound, video). Narration uses an audio file when the cue has one, otherwise the browser's built-in speech synthesis. Browsers block sound until the user interacts, which is why the stage starts with a "Tap to begin the evening" button.
- **Theming**: colours are CSS variables that `useThemePalette` swaps per theme; Tailwind utilities (`bg-surface`, `text-accent`) read those variables. A page can also set a fixed palette with `usePalette`: the escape pages use `ESCAPE_PALETTE` (steel and exit-sign green), so the two games look different. It's a layout effect, so the page never flashes in the default gold first.

## 9. Deployment shape

One image (`Dockerfile`, three stages: Node builds the SPA, the .NET SDK publishes the API, and the small ASP.NET runtime image runs it) plus Postgres in `docker-compose.yml`. The API serves the SPA from `wwwroot` and falls back to `index.html` for client-side routes, so everything shares one origin: no CORS, and cookies and WebSockets just work behind Coolify's proxy. See [deploy-coolify.md](deploy-coolify.md).

## 10. The AI game master (Phase 2)

The AI lives in its own project, `src/ButlerDidIt.Ai`. It depends on the game rules, but the rules know nothing about AI.

```
 Hub / endpoint ──► AiGameService / MysteryGenerator
                            │ builds prompts (Prompts/*)
                            ▼
                        AiGateway ── budget check (IAiBudget)
                            │       usage log  (IAiUsageSink)
                            ▼
                  IChatClient (Microsoft.Extensions.AI)
          ┌─────────────┬───────────┬────────────┬────────┐
       Anthropic      OpenAI     Gemini       Ollama     Fake
```

**Why `IChatClient`:** it's .NET's standard interface for chat models, and each vendor ships an adapter. `ChatClientFactory` is the only code that knows vendors exist; supporting a new provider means one new `case`. The admin assigns a provider and model to each **role** (Storyteller, Actor, Inspector), so you can mix a strong model for writing with a cheap one for chatting.

**Why the engine never calls the AI:** AI calls are slow, cost money and can fail, while `GameEngine.Apply` must stay pure and instant. So every AI action is three commands:

1. `BeginNpcQuestion` checks the rules (right phase, an NPC, questions left) and reserves a slot. Everyone immediately sees "Colonel Mustardseed is thinking…".
2. The server calls the AI **outside** the party lock, so the game never freezes. The answer is streamed, and each piece is pushed to the screens as `npcTyping`, so guests watch it being "typed".
3. `CompleteNpcQuestion` stores the answer, or `CancelNpcQuestion` gives the slot back if the AI failed.

**Why prompts are built from what a character knows:** `NpcPrompt` only includes that character's own sheet, public facts and clues already found. The model can't leak the solution because it was never given it. That's far more reliable than telling a model "don't reveal X". Hints are built from the player's own `PlayerView`, which is already filtered by `ViewProjector`. Tests in `ButlerDidIt.Ai.Tests/AiTests.cs` check both.

**Generated mysteries** go through `MysteryGenerator`:
1. An outline.
2. The full scenario JSON.
3. `ScenarioValidator`. The model is shown its own errors and fixes them, up to 3 times.
4. A **blind solver**: the Inspector sees only the evidence and must name the killer.

Only a mystery that passes is saved, as a `ScenarioEntity` with `Source = AiGenerated`. It belongs to the host who generated it. It runs as a background job (`GenerationWorker`) because it takes longer than a web request should stay open.

**Verdicts** are queued in memory when the reveal starts (`VerdictQueue`). So that a restart during the reveal doesn't lose them, `VerdictWorker` first looks for recent parties that are at the reveal with verdicts switched on and none written yet, and queues them again.

**Keys and costs:**
- API keys are encrypted with ASP.NET Data Protection (`AiKeyProtector`) before they reach the database.
- Every call is logged in `AiUsage` with tokens and an estimated cost from the admin's price list.
- `DbAiBudget` refuses new calls once a host's monthly budget is used.

**Tests without an API key:** the `Fake` provider (`FakeChatClient`) returns canned answers, keyed on the `TASK:` line every prompt starts with. It is only allowed when `Ai:AllowFakeProvider=true`, which the test suites and Playwright set.

Setup instructions: [ai-setup.md](ai-setup.md).

## 11. Voices, pictures and the party kit (Phase 3)

```
 create party ──► MediaWorker.EnqueueAsync ──► MediaJobs table
                                                   │ (background)
 MediaPlan.For(scenario)  ── what's needed ────────┤
                                                   ▼
 MediaService ── cache by SHA-256(kind|model|voice|text) ──► MediaAssets + files in /data/media
      │                                                          │
      ▼                                                          ▼
 MediaGateway ── budget + usage log              ScenarioMedia (scenario, key → asset)
      │                                                          │
 ITextToSpeech / IImageGenerator                   MediaOverlay.Apply ── fills URLs into the
 (OpenAI or Fake)                                  scenario when ContentCatalog loads it
```

**Why prepare everything up front:** voice clips and pictures take seconds each. Making them while the guests watch would stall every scene. Instead, creating a party queues a job that makes every portrait, the victim and setting pictures, a picture for each clue card, all narration and every NPC line. When it finishes, every screen of every party using that mystery is refreshed.

**Why a cache keyed by a hash:** the same sentence in the same voice from the same model always sounds the same, so it's only paid for once. The hash of the request is the `MediaAssets.ContentHash` (unique), which also stops two workers from saving duplicates at the same time.

**Why an overlay instead of editing the scenario:** hand-written scenario JSON stays exactly as written, and `ScenarioMedia` maps keys like `portrait/finch` or `line/finch/act1/0` to files. `MediaOverlay` (pure, in `ButlerDidIt.Game`) fills them in only where the author left `src`, `portrait` or `image` empty, so hand-made art always wins.

**Why clue pictures use only the title:** a clue's text (and whether it's a red herring) can change between versions of a story, so the prompt is just "an evidence photograph of: *title*". The key, `clue/{id}/{hash of title}`, changes if the clue is renamed, so a renamed clue gets a new picture instead of the wrong one. Views only carry the clues a screen may see, so a private clue's picture reaches only its recipient.

**Why background jobs claim work atomically:** `MediaWorker` and `GenerationWorker` move a job from `Queued` to `Running` with a single `UPDATE … WHERE Status = 'Queued'`. Only one worker can win, so a job never runs twice, even with several app instances. A media job interrupted by a restart goes back to `Queued` and skips what's already done.

**Voiced NPC answers** are made on demand, after the answer text is stored: the stage shows the text immediately and plays the clip when it arrives (it waits a few seconds for it, then falls back to the browser's voice).

**Costume selfies** (`POST /api/seat/photo`, seat token) are decoded and re-encoded with SkiaSharp. That drops every bit of metadata, including GPS location; the photo is turned upright and shrunk to 640 px. Only the URL goes into the game state (`SetPlayerPhoto`).

**The printable kit** (`/api/parties/{code}/kit/*.pdf`, host only) is drawn with QuestPDF: invitations with a QR code (QRCoder), name tags, character booklets (with secrets) and clue cards with a sealed solution.

**Toast prompts** are a `toast` cue type. `ViewProjector` drops them unless the host switched on drinking prompts, which is always off for Family parties. Every toast carries a non-alcoholic alternative.

## 12. Clean-up (retention)

`RetentionWorker` runs every few hours:
- It deletes abandoned lobbies and games.
- It prunes finished parties: their seats (so old seat tokens stop working), private notes and selfies go, but the `Parties` row and its game state stay for the recap page.
- It sweeps selfies whose party is gone.

Selfies record their `PartyId`, deliberately without a foreign key: removing the database row must also remove the file on disk, which only application code can do. A replaced or removed selfie is deleted immediately. Generated voices and pictures are shared between parties, so they are never deleted with a party.

Clearing selfie URLs from a finished game goes through the engine (`SetPlayerPhoto`) like any other state change, so the saved state never points at a deleted file.

## 13. Running more than one server

Everything here is off by default: one server behaves exactly as described above. Each piece is switched on with a setting (see "Running more than one server" in [deploy-coolify.md](deploy-coolify.md)).

| Problem with two servers | What handles it |
|---|---|
| SignalR groups live in one process: the TV on server A wouldn't hear a guest's move on server B | A **Redis backplane** (`Scale:Redis`) relays every message to every server |
| Two taps on different servers could run the same party's command at once | `PartyLocks` also takes a **cluster lock** per party (`Scale:MultiInstance`) |
| Every server would drop the midway clues, run clean-up, or write verdicts | Each round, only the server that gets the `ticker`, `retention` or `verdicts:{party}` lock does the work |
| A restarting server would fail or re-queue jobs another server is running | Only jobs with no progress for 10 minutes count as interrupted |
| Each server would make its own sign-in keys, so a cookie from one fails on another | Keys are stored in the database (`DataProtection:Store=Database`). Existing key files are copied in the first time, so saved AI keys still decrypt |
| Two servers starting together would both seed content, and migrations should be a deploy step | Startup takes a `startup` lock; `--migrate` migrates and exits; `Database:MigrateOnStartup=false` skips it on the servers |
| Files on one server's disk aren't on the other | `IMediaStore` has a local store and an **S3** store (`Media:Storage=S3`) |

**Why Postgres advisory locks rather than Redis locks:** every server already has a database connection, and an advisory lock belongs to the connection that holds it. If a server crashes mid-command, its connection drops and Postgres releases the lock, so there's no lock expiry to tune. `ClusterLock` uses transaction-level locks (`pg_advisory_xact_lock`) on a dedicated connection, so a lock can't outlive its transaction even when the connection goes back to the pool.

**Why files still go through the app with S3:** `/media/assets/{id}` reads the file from the store and streams it. Asset URLs stay the same whichever store is used, and costume selfies are never exposed as public bucket links.

## 14. Game kinds: one platform, several games

A party has a `GameKind`: `Mystery` or `EscapeRoom` (#67). Everything around the game is shared, and only the rules and screens differ per kind:

| Shared by every game (the platform) | Per game |
|---|---|
| Host accounts, join codes, seats and seat tokens, the hub and its groups, reconnects | The pure rules engine (`ButlerDidIt.Game` for mysteries, `ButlerDidIt.Escape` for escape rooms) |
| `PartyRuntime`: the party lock, load, save (state, status, next wake-up), broadcast | Its views and their privacy rules (`ViewProjector` for mysteries) |
| Joining, removing a seat, costume selfies, the ticker, retention | Its hub actions and pages (the mystery's `PartyService`, dealer, AI game master, kit, recap) |
| AI gateway, media pipeline, scaling, backups | Its content (scenarios, and later escape rooms) |

**How a game plugs in:**
- **The module:** a game is an `IGameModule` (`src/ButlerDidIt.Api/Games/`). It loads a party's row into a `GameSession`, which gives the platform what it needs:
  - the seats, status and next wake-up;
  - the stage and player views;
  - the four commands every game supports: add or remove a player, set a player's photo, and tick.
- **Registration:** `GameModules` finds the module by the party's kind. A kind with no module is a friendly "can't run … games yet", not a crash.
- **The mystery:** `MysteryModule` wraps the existing engine. `PartyService` keeps its mystery-typed API (`ExecuteAsync`, `ChangeAsync`, `PartySnapshot`) on top of `PartyRuntime`, and refuses parties of another kind. So a mystery action can never run against another game.
- **The front end:**
  - `PartyInfo.kind` tells the pages which screens to show.
  - `useParty` is generic over its view types, so a new game reuses the connection, reconnects and version checks.

**Why the seam is shaped this way:** the platform already did the hard, shared work (locks, reconnects, privacy, scaling). A new game should only have to bring its rules and screens. Tests use a stand-in escape module (`GameKindTests`) to prove joining, seats, live views and the ticker work for a game that isn't a mystery.

### Escape rooms

`ButlerDidIt.Escape` follows the mystery's rules for rules: pure functions, a state saved as JSON, commands that return a new state, and views copied field by field.

- **A room** (`content/escape/*.json`) is a list of stages. A stage's puzzles must all be solved to open the next one. A puzzle can be:
  - a **code** (digits),
  - a **text** answer,
  - a **use** puzzle, which needs items such as a key or a fuse.

  Puzzles can require items and give items as rewards.
- **Split clues:** a puzzle's `pieces` are dealt round the table when the clock starts. Each phone sees only its own pieces, so the group has to talk. If a player leaves, their pieces pass to someone still playing.
- **The clock:**
  - it's a `Deadline`, and each hint moves it earlier;
  - `EscapeEngine.NextDueAt` hands the deadline to the platform's ticker, which ends the game when time runs out;
  - a wrong answer locks that puzzle for 3 seconds, so a code can't be brute-forced from a script.
- **Answers are checked on the server**, and forgivingly: case, spacing, punctuation and a leading "a", "an" or "the" don't matter. They never reach a browser. `PrivacyTests` walk every room through every stage and search the serialized views for answers, unpaid hints, other players' clue pieces and later stages' puzzles.
- **`EscapeRoomValidator`** checks references and answer formats, keeps Family rooms free of gruesome words, and **proves each room can be escaped**: it plays the room greedily, solving any puzzle whose items are in hand, and reports the first stage that gets stuck. The app refuses to start with a broken room.
- **In the API:**
  - `EscapeModule`/`EscapeSession` plug the engine into `PartyRuntime`;
  - `EscapeService` runs escape commands (and refuses other kinds of party);
  - the hub's escape actions are in `PartyHub.Escape.cs`;
  - rooms are listed at `/api/escape-rooms`, and a party is created with `POST /api/parties/escape`.
- **Front end:** `src/web/src/escape/` holds the shelf, the TV (`EscapeStage`) and the phone (`EscapePhone`). The game routes pick them by `PartyInfo.kind`. The shelf (`RoomShelf`, `useRoomShelf`) is shared by the landing page and the host page:
  - Each card shows the room's cover picture once the media pipeline has painted one (`EscapeRoomSummary.CoverUrl`, read in one query for the whole shelf). Until then it shows a backdrop and an icon for the room's mood, keyed by its soundscape (`moods.ts`, shared with `SceneView`), so a room the AI wrote gets one too.
  - Covers are safe to show anyone: they're drawn only from text the TV shows before the game starts.

**Replays** (`RoomVariants`):
- **The seed:** `EscapeState.Seed` is the puzzle set. The server picks it when the party is created: random, today's date for the daily challenge, or a number the host types in. The engine takes it as given, so it stays free of randomness.
- **Building the room:** the engine and the projector build `RoomFor(state, room)` from it on every command. Variants are picked and generators run with SplitMix64, whose sequence never changes between .NET versions, and each puzzle gets its own seed (seed xor a hash of its id).
- **Caching:** the built room is cached per room and seed.
- **Privacy:** the seed is withheld from views until the game ends, because the content is public and the answers could be worked out from it.

**Leaderboards:**
- `GameSession.OnSaving` runs inside the runtime's save. The escape session uses it to add an `EscapeResult` row in the same transaction that ends the game, so a result is never lost or counted twice (one per party, enforced by a unique index).
- `GET /api/escape-rooms/{id}/leaderboard` ranks escapes by score, all time or today's.
- Team names are returned only for the caller's own parties.

**The AI game master** (optional; `EscapeAiFeatures` on the state, set when the party is created from the roles an admin has configured):
- **Moments (cues):** with `GameMaster` on, the engine records an `EscapeCue` for each moment worth reacting to: the start, a solve, a stage opening, three wrong answers in a row, five minutes left, the escape and the failure. There is one cue per moment (an escape is not also a solve). "Five minutes left" is a tick: `NextDueAt` returns that moment first, then the deadline.
- **Lines:** the engine never calls the AI. After every committed save, `PartyRuntime` calls each `IPartySavedHandler`. `NarrationTrigger` queues the party when a new cue appears, which covers the ticker's changes too. `NarrationWorker` then has `EscapeGameMaster.NarrateAsync` write a line for the newest cue (Actor role, `TASK: escape-narration`) and record it (Voice role). Older waiting cues are passed over (`SkipCues`), and cues older than a minute are never narrated. The results come back as `SetCueNarration`/`SetCueAudio`. A failed call is logged and passed over, because the ticker still says what happened. A `ClusterLock` stops two servers speaking for one party.
- **Hints:** with `Hints` on, a hint is `BeginEscapeHint`, which pays the time and reserves the step exactly like a written hint. `EscapeGameMaster.RequestHintAsync` then asks the AI outside the lock (Inspector role, `TASK: escape-hint`) and returns `CompleteEscapeHint`, or `CancelEscapeHint` on failure. The **engine** checks the text with `EscapeHintGuard` against the answers of the puzzle set being played. A hint that spells out an answer (digits in any form, words in any case, with or without accents or a plural) is dropped, and the written hint for that step shows instead. So does a hint still missing after `AiHintTimeout`, so a server restart mid-call never leaves a puzzle stuck "thinking".
- **Privacy:** the narration prompt is built from the TV's public view. The hint prompt holds the puzzle, its clue pieces with who holds them, the items, the recent wrong tries, the hints already shown and the author's written hint for the step, but never the answers. `EscapePromptTests` play every room over 200 puzzle sets and check that no prompt holds an answer the group can't already see.

**Lengths:**
- A room offers `lengths` (30, 45 or 60 minutes), and a puzzle can have `minMinutes`. The host picks a length, and it's kept on the state (`EscapeState.Minutes`).
- `EscapeEngine.RoomFor` is `RoomLengths.Cut(RoomVariants.Build(room, seed), minutes)`: the chosen puzzle set, minus the puzzles kept for longer games (and any stage left empty), with the clock at the chosen length. Both steps are cached. Everything downstream (rules, projector, prompts, art) sees the cut room, so a shorter game needs no special cases.
- The validator checks every length over 200 puzzle sets. Results record their length, and the leaderboard ranks each length apart. Results from before lengths existed count as the room's standard length.
- `EscapeRoom`, `EscapeStage` and `EscapePuzzle` are records, so a built or cut room is made with `with { … }` and every setting carries over. Hand-copying had already dropped a setting once.

**Harder rooms** (#82):
- **Scenes:** a stage can have a `scene` of spots. `ExamineSpot` searches one: its look, item, notebook clue and any hidden pieces go to the group, and every `Search` puzzle whose `finds` are all searched solves itself. `InspectItem` and `CombineItems` do the same for closer looks and recipes, and `PressSwitch` works a light panel (`EscapeState.Switches` holds the lights once touched). The hub's actions are `EscapeExamine`, `EscapeInspect`, `EscapeCombine` and `EscapePress`. The projector sends a spot's look only once it's searched, an item's closer look only once looked at, and never the recipes.
- **New generators** (`PuzzleGenerators`): ciphers, number patterns, logic puzzles and light panels. Each is built to have exactly one answer, and `PuzzleProofTests` prove it over 1,000 seeds. A cipher's key is written into the room wherever it says `{key:<puzzle id>}`, and `RoomVariants` notes on the puzzle where that is (`KeyAt`), so the validator can check the group reaches it in time. `SeededRandom` is the SplitMix64 generator shared by all of them.
- **Difficulty:** `EscapeState.Difficulty` (null is Normal, so parties saved before it are unchanged). `RoomFor` is `RoomLengths.Cut(RoomVariants.Build(room, seed, difficulty), minutes, difficulty)`. `Build` sizes the generators, picks cipher words, halves the hint penalty (Easy) or drops the last hint step (Hard); `Cut` drops puzzles above their `minDifficulty`. Normal takes exactly the path it always did, so every existing room builds byte-for-byte the same puzzles. Results record their difficulty, and the leaderboard ranks each one apart (`?difficulty=`, with old results as Normal).
- **Editions:** `EscapeRoom.Edition` numbers a rebuilt room. Results store the edition they were played on (null counts as 1), and `EscapeResults.AtEdition` keeps each room's leaderboards and the shelf's best time to its current edition.
- **Hard-only spots:** `SceneObject.MinDifficulty` works like the puzzle field. `RoomLengths.Cut` leaves those spots out of easier games, so Hard gets extra decoys, red herrings and key chains without touching Normal.
- **Hiding pieces:** at the start, a puzzle's pieces beyond the player count go into its stage's `hidesPieces` spots, chosen from the seed (`PieceHolder` with a `SpotId` and no seat). Searching the spot hands the piece to the searcher. A room without hiding spots deals exactly as before.
- **The validator's play-through** is a fixed point per stage: search every spot it can, look at every item, put together every pair, solve every puzzle it can, and repeat until nothing changes. It's exact because an item is either used up (by one puzzle or recipe) or a tool (never used up), never both, so no order of play can strand the group. It runs for every length × difficulty × 200 puzzle sets.
- **Screens** (`src/web/src/escape/`):
  - `SceneView` draws a scene as HTML buttons placed over the picture, by percentage of the canvas, so every spot works by keyboard and screen reader; `props.tsx` draws each prop as a small SVG.
  - `ItemInspector` holds, inspects and combines items; `Notebook` shows the notebook.
  - `PuzzleWidgets` has the light grid, the logic grid and line-up, the cipher decoders and the pattern display.
  - A cipher's view (`EscapeCipherView`) says which decoder to show and is `unlocked` only once `EscapeState.KeysFound` has it. The engine records a key as found after any command that makes one of its `KeyAt` places visible, and it stays found. Key cards and shift amounts are sent only for keys the group has found (each is already written on a spot they saw). **Decoy keys:** a cipher may write `{key:<id>}` in several places. `RoomVariants.WithKeys` picks the real place per seed among those the game's difficulty shows (`RoomLengths.Shows`), and fills the others with decoys from `PuzzleGenerators.Cipher` (key cards that read a `decoyWords` word, or wrong shift amounts), recorded in `CipherDecoder.Candidates`. The view lists only the keys found so far (`EscapeFoundKey`, labelled by place), never which one is real. `EscapeRoomValidator.ValidateGame` checks one puzzle set the way it's played: built and cut at the same difficulty.
  - The logic helpers keep their marks in the phone's `sessionStorage` and never send them.
  - The e2e tests play the test-only Laboratory, which `Escape:TestRoomsRoot` adds to the shelf only when `Escape:ExposeAnswersForTests` is on.
- **AI:** the hint prompt includes only searched spots, looked-at items, found pieces and the notebook, plus whether a cipher's key has been found (never the key), and on Hard it asks for a nudge only (`EscapeScenePromptTests`). A Hard decoy search raises a `Decoy` cue for the game master to tease.
- **AI-written rooms** use every kind (#86). `ShapeErrors` asks for a scene in every stage (with decoys and hiding spots), a search, a closer look or recipe, a cipher, a logic puzzle, a Hard-only part and at most 2 `use` steps, and a puzzle count that fits the clock. It also refuses cipher words or riddle answers already on screen (`EscapeRoomText`, shared with the content tests). `Normalize` places the spots on a grid (the model never writes positions) and drops what only a build or a hand-written room has (lengths, variants, grids, key locations). `Anonymize` renames puzzles and spots, including in `{key:…}`. The blind solver tests riddles with everything their stage's spots and items show, and the logic puzzles as one fixed seed builds them.

**Atmosphere** (the TV only; phones stay quiet):
- **Sound is synthesised in the browser** (`src/web/src/escape/sound.ts`, Web Audio), so rooms ship no audio files and there's nothing to license.
  - A room, and optionally each stage, names a `Soundscape` preset: drone, workshop, carnival, sea, space, haunted or silence. The view carries the current one.
  - `useAtmosphere` works out the stingers from what changed between two views: an unlock, a new stage, a wrong answer, a hint, the escape or the failure. It also sounds a gong at one minute left, then a heartbeat that speeds up. Because the view is its only input, a TV that reconnects just carries on.
  - Browsers only allow sound after a click, so the TV shows a sound switch. Its setting is remembered on the device.
- **Room art goes through the mystery media pipeline.**
  - `EscapeMediaPlan` asks for a cover and one picture per stage. The prompts are built only from text the TV already shows, never puzzles, pieces or answers, and `EscapeMediaPlanTests` checks this over many puzzle sets.
  - An escape party created with the AI on queues a `MediaJob` whose id is `escape:{room id}`, so it never mixes with a mystery's. `MediaWorker` paints the pictures once per room, `MediaService` caches them, and every party of the room reuses them.
  - `EscapeCatalog.ArtAsync` loads them into the session, cached for 30 seconds so pictures from another server show up soon. `EscapeProjector` picks the current stage's picture, or the cover.
- **Finale:** doors swing open on an escape, and bars drop when the group is trapped. It uses movement only, never flashing, and nothing moves under `prefers-reduced-motion`. The results, the ranking and the game master's captioned last line stay on screen.

**Rooms written by AI** (`EscapeRoomGenerator` in `ButlerDidIt.Ai`, run as a `GenerationKind.EscapeRoom` job by `GenerationWorker`):
- **Writing.** The Storyteller (`TASK: escape-room-write`) writes the whole room as JSON in the same format as `content/escape/`. It writes the story, the riddles, the items and the game master, and it chooses which proven templates (`digitFacts`, `colorDigits`, `wordSequence`) fill the code and password slots. **It never writes a code:** those come from the generator and the seed, so they're correct by construction and shuffled every game like any other room.
- **Checks, with repairs.** Each draft is normalised: the server sets the id, rating, clock, hint penalty and player range, and strips variants. It then goes through `EscapeRoomGenerator.ShapeErrors` and `EscapeRoomValidator`. The shape rules are: 2–4 stages, 5–10 puzzles, at least two generators, a riddle and a `use` puzzle, every code from a generator, and no riddle that gives its own answer away. The validator adds its 200-seed play-through. Problems go back to the model, up to three repairs.
- **The blind tester.** `EscapeRoomSolver` (Inspector, `TASK: escape-room-solve`) sees each riddle the way the group would: its prompt and every clue piece, numbered, never the answers, the hints or the model's puzzle ids. A missed riddle gets one repair turn, then the room is kept with a warning.
- **Neutral ids.** The model names puzzles after what they are ("echo-riddle"), and puzzle ids reach the browsers. So once a room passes, `Anonymize` renames them `puzzle-1`, `puzzle-2`… This runs last, so repair turns can still quote the model's own ids back to it.
- **Storage and access.** A room is saved as an `EscapeRoomEntity` (jsonb) for its host only after it has passed.
  - `EscapeCatalog` merges the file rooms with the DB rooms. `FindAsync` is for loading a party (guests of any host), and it caches the parsed room because it never changes. `FindForHostAsync` is for starting a party, and it always asks the DB for the owner, so a deleted room can't be started from another server's cache. `OwnedAsync` builds the host's shelf.
  - Only the owner sees a room on the shelf (`generated: true`), starts parties with it, or deletes it. Its results and leaderboards stay after a delete.

