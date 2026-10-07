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
- `watcher:{watcherId}`: one spectator's screen (#112), so the host removing them reaches it

After every command, `PartyService.BroadcastAsync` sends a **complete snapshot** (not a diff) to each group. Snapshots are a few KB, and a phone that missed messages while asleep fixes itself with the next one.

Two lighter messages sit beside the snapshots:
- `npcTyping {interrogationId, text}`: an NPC's answer so far, while the AI is still writing it, sent to the stage and every seat at most every 150 ms. It isn't saved; the finished answer arrives in the next snapshot, and screens show `answer ?? typing[id]`, so the finished answer always wins.
- `jobs`: sent to `user:{userId}` whenever one of that host's mystery or media jobs changes. It carries no data: the page re-fetches the job through its normal, access-checked endpoint, and still polls every 15 seconds in case a signal is lost.
- `cheer {emoji, name}` and `audience`: spectator mode's cheers, and the data-less "who's watching changed" signal for the host's TV (see section 6).

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
| **Spectator** | The same kind of token, starting `w.`, issued on watching: the TV's view and cheers only (#112) | `SeatTokens.cs`, `SpectatorEndpoints.cs` |

Guests don't need accounts. The 6-letter party code only gets you to the join page (and joins are rate-limited). The seat token is what proves you are "Bob" afterwards. The database stores only a SHA-256 hash of each token, like a password. Because the token lives in `localStorage`, a phone that sleeps, refreshes or drops off Wi-Fi rejoins the same seat.

SignalR sends the token as `Authorization: Bearer …`. For WebSockets, browsers can't set headers, so it goes in the `access_token` query string, which the server only accepts on `/hubs` paths.

The hub accepts both identities at once (`AuthPolicies.PartyMember`), so the host's device can be the stage *and* a pass-and-play seat.

**Spectators** (#112). People can watch a party's TV on their own phone, without a seat: family far away, or more people than a game has seats. This is `SpectatorEndpoints.cs`, `Audience`, and `/watch/:code` on the front end.
- **A third kind of token.**
  - `POST /api/parties/{code}/watch` makes a `Spectator` row and returns a token starting with `w.`; only its SHA-256 hash is stored, like a seat's. It's rate-limited like joining.
  - `SeatTokenHandler` looks a `w.` token up among the spectators and gives it the party's id and a `watcher_id`, **never a `seat_id`**.
  - So `WatchParty`'s existing guest check lets a watcher see the TV's view, while every player action (all start with `RequireSeat`) and every seat endpoint turns them away. `SpectatorTests` checks both.
- **When it works.** Anyone with the code can watch, in the lobby or mid-game (joining as a player closes when the clock starts), up to 50 per party (`Audience.MaxWatchers`).
- **The host's controls.** The host sees who's watching (`GET …/spectators`, refreshed on the data-less `audience` signal), can remove someone, and can switch watching off (`Party.AllowSpectators`, on by default). Switching it off removes everyone.
- **Being removed.** The token is deleted and the screen gets `removed` through its `watcher:{id}` group.
  - Over long polling the open connection fails at its next poll.
  - Over a WebSocket, checked only when it connects, it keeps receiving the TV's updates (as a removed seat does) until it closes, which the page does at once.
- **Cheers** (`Cheer`). A watcher or guest sends one of six emoji, never free text, so there's nothing to moderate.
  - The TV shows each one rising with the sender's name: the name from the token, not the host's email on the host's own phone.
  - `CheerLimiter` drops more than one per person every 2 seconds, or 5 per party per second.
  - Cheers are never saved, and someone removed can't cheer.
- **Clean-up.** Watchers go wherever seats go: with a deleted party (cascade) and when the retention job prunes a finished one.

**The host's own account** (`AccountEndpoints.cs`, #98). The header's account menu (`AccountMenu`, on every page but the pass-and-play screen) leads to `/account`:
- **Every endpoint acts on the signed-in host.** No request carries a user id, so there's nothing to change to reach someone else's account.
- **The current password guards what could lock the owner out** (email, password, deleting). It's checked with lockout on (`CheckPasswordSignInAsync`), so someone with a stolen session can't guess it faster than on the sign-in page.
- **A new email address must confirm itself** with a link sent there (`GenerateChangeEmailTokenAsync`), and the old address is told. The email and the sign-in name change together in one transaction. A server without email changes it at once, because there's nothing to confirm with.
- **Other devices are signed out** by a new security stamp: a new password, a new email or "Sign out everywhere else". Each browser re-checks its stamp every `Auth:SessionCheckSeconds` (60, rather than Identity's default 30 minutes), and the device that made the change is signed in again (`RefreshSignInAsync`).
- **Download my data** is a JSON file of the account, its parties, mysteries, rooms, escapes and monthly AI use. It leaves out guests' names and notes, which belong to the guests.
- **Deleting** an account removes its parties (with seats, notes and selfie files, via `RetentionWorker.DeletePartyAsync`), its own mysteries and rooms, and their art. Leaderboard times and AI costs are kept without the name. The admin account can't be deleted.

**Plans and access** (`Plans/Access.cs`, #100). Hosts pay for murder mysteries, escape rooms or both. Access is a set of **grants** (`AccessGrants`), each with its games (`GameAccess` flags), kind, start and end:

| Kind | Games | Lasts | Comes from |
|---|---|---|---|
| `Trial` | both | `Plans:TrialDays` (14) | signing up |
| `Comp` | both | for good | the admin (Hosts page), an invite with free access, or having had an account before plans (the migration) |
| `Pass` | one | e.g. 72 hours | buying a party pass (#101) |
| `Subscription` | the plan's | the paid period | the payment provider (#101) |

- **A host's access is every grant in effect put together** (`Access.From`, pure and unit-tested), so sources overlap without special cases: a pass bought during a trial, a subscription on top of a pass. Grants aren't edited into something else: they end, or are revoked, and new ones are added.
- **The code checks games, never plan names.** The filter `Access.RequireGame(GameKind)` sits beside `RequireConfirmedHost` on the four endpoints that start something: a mystery party, an escape party, and the two AI writers. The admin always passes.
- **Only starting is gated.** Guests, joining, a party already created (a trial that ends mid-evening never stops one), recaps and the host's own content never are.
- **The pages show the answer:** `MeResponse.Access` carries it. The host page says what's locked (`AccessNotice`), and the account and Hosts pages show the plan. The server is still what enforces it.

**Invites** (`InviteEndpoints.cs`, #97). While sign-ups are closed (the admin hub's switch, or `Auth:AllowRegistration=false`), a new host needs an invite link from the admin (`/login?invite=…`):
- **Stored like seat tokens.** The link carries a random 256-bit token, and the database keeps only its SHA-256 hash. The admin's list never shows a link again, and a copy of the database can't be used to sign up.
- **Used once, in the sign-up's own transaction.** `POST /api/auth/register` claims the invite with one `UPDATE … WHERE UsedAt IS NULL` inside the transaction that creates the account. The UPDATE locks the row, so if two people use one link at the same moment, the second waits, then finds it used. If creating the account fails (a weak password, an email already taken), the rollback leaves the invite unused.
- **Optional limits:** an invite can be for one email address only (compared the way Identity normalises emails), and it expires after 1 to 30 days.
- **No separate "closed" mode.** An invite works whether or not sign-ups are open. An admin who wants no new hosts simply makes no invites.

**The admin hub** (`/admin`, #102). One frame (`Admin.tsx`) with a tab bar, each tab its own address, drawn through a nested route and `<Outlet/>`:

| Tab | Page | Endpoints |
|---|---|---|
| Overview | `AdminOverview` | `GET /api/admin/overview` |
| Games | `AdminGames` | `GET /api/admin/games` |
| Hosts | `AdminHosts` | `/api/admin/hosts` (free access, reset links) |
| Sign-ups | `AdminSignups` + `InvitesPanel` | `GET`/`PUT /api/admin/signups`, `/api/admin/invites` |
| AI | `AdminAi` | `/api/admin/ai/*` |

- **Admin only, twice:** the frame shows the tabs only to the admin, and every endpoint has an admin filter (`AdminHubEndpoints.RequireAdmin`), so a host who types the address gets a 403.
- **The overview** (`AdminOverview` record) is a handful of aggregate queries:
  - **hosts:** the total, new this week (from `AppUser.CreatedAt`) and active this month;
  - **plans:** every host's grants put together with `Access.From`, as on the Hosts page, since SQL can't combine grants;
  - **parties this week:** the ones that left the lobby, with their seats, plus the games under way now;
  - **games played per week:** from `PlayRecords`, not `Parties`, because the retention job deletes abandoned parties but the records stay;
  - **AI spend this month, ratings and votes from the last 30 days;**
  - **the server:** email, media storage and size, one server or several, AI providers and roles. These come from configuration, so the page only reports them.
- **Games** lists every mystery (a version's games count on its mystery, as on My mysteries) and every room, built-in and hosts' own, with plays, ★, solve rate and the votes. The page flags a game that needs a look (a low rating, votes saying too hard or too easy, few solving it) once it has at least 3 games or votes, and links to its insights, which link back to the list.
- **Sign-ups switch:** `SiteSettings`, one row (id 1) whose `AllowRegistration` is null until the admin chooses. `SignUps.OpenAsync` reads it on every sign-up and sign-in-page load (the admin's choice, else `Auth:AllowRegistration`), so a switch reaches every server at once with no redeploy. "Use the server's setting" sets it back to null.
- **`AppUser.CreatedAt`** is set at sign-up. The migration gave older accounts the date of their first grant or party, whichever came first.

## 7. Content: themes and scenarios

`content/themes/<slug>/` holds `theme.json` (palette, era, art style), `scenarios/*.json` (the mysteries) and `media/` (images, audio, video). On startup, `ContentCatalog.SeedAsync` validates every scenario (`ScenarioValidator`) and upserts them into the database. A broken mystery fails the deploy, not a party.

Only `media/` folders are served over HTTP (`MediaEndpoints.cs`), with a path check against `../` tricks. The scenario JSON sits next door and contains the solution.

**Libraries, sharing and hiding** (both games). Built-in rooms (`content/escape`) and hand-written mysteries are read from files at every start, so they're never edited in place. A host edits their own copy, listed in **My mysteries** (`/mysteries`) or **My escape rooms** (`/escape/rooms`, `GET /api/escape-rooms/library`).

The admin can improve one for everyone, with two switches (`PUT …/{id}/sharing`, `ContentVisibility`):
- **Share** their own room or mystery with every host. That's a `Shared` flag on its row.
  - A shared escape room is on every shelf, including the public one. Anyone can host it, read it and copy it, like a built-in room; only the admin edits it or its media.
  - A shared mystery is on every shelf and playable, versions included. Like a hand-written one, other hosts can't read or copy it.
- **Hide** a built-in room or hand-written mystery: a `HiddenContent` row.
  - It's a table because built-in rooms aren't database rows, and the mystery seeder rewrites its rows on every start.
  - A hidden one is off every shelf, and new parties with it are refused, except the admin's. Existing parties, recaps and leaderboards keep working.

Together, they let an improved copy take the original's place. The copy is a new room, with its own leaderboards.

These rules are checked on the server where a party starts (`EscapeCatalog.FindForHostAsync`, the mystery party endpoint), not just on the shelves.

## 8. The front end

- **Pages** (`src/web/src/pages`): `Home`, `Login`, `NewParty`, `Join`, `Stage` (TV + host controls), `Play` (phone), `Watch` (the TV on a spectator's phone, with cheers), `PassAndPlay`, and the shared recaps: `Recap` (mysteries) and `EscapeRecap`.
- **Front doors** (#99): `/` (`Home`) offers both games, with a card for each in that game's colours, plus "Your parties". Each game has its own page for visitors who haven't signed in: `/mystery` (`MysteryLanding`: the themes) and `/escape` (`EscapeLanding`: how it works, then the room shelf with filters and each room's leaderboards). Each also has a printable how-to-play sheet, `/how-to-play` and `/how-to-play/escape`. "Host" links go through sign-in with `?next=`, so a visitor lands on the host page with the room they picked.
- **`PlayerScreen`** is the whole phone experience. Pass-and-play reuses it for each local seat.
- **The mystery's TV layout** (#129): nobody scrolls a TV, so on a screen at least 1024×600 (`TV_LAYOUT`, the escape TV's test), `Stage` becomes a full-height frame: the top bar, then the phase, then the host bar (part of the frame rather than floating over it). The watchers panel becomes the top bar's 👀 chip.
  - **The phase is wrapped in `FitToScreen`.** It measures the content's natural height and scales it down to fit when it's a little too tall, but never below 0.6. Below that it's unreadable from the sofa, so the box scrolls instead, as a last resort that `mystery-tv.spec.ts` checks never happens. `data-fit` says which it did.
  - **Lists that grow all evening stay short on the TV, because the phones keep everything.**
    - Evidence shows the newest four clues as compact cards on a 1080p screen, or two on a short one, and names the rest.
    - Secrets show the latest three.
    - The NPC interrogation room shows the last two.
  - **The endings are laid out in two columns:**
    - the unmasked killer beside the Inspector's verdicts, then beside the explanation;
    - the finale beside the scores and the timeline;
    - the winners beside the scores.
  - **Scenes are sized to fit:** each is as wide as the screen allows while its 16:9 picture fits between the bars.
  - **Phones and small windows** keep the scrolling page.
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
 (OpenAI, Gemini, ElevenLabs, Piper,               scenario when ContentCatalog loads it
  Stable Diffusion or Fake)
```

**Each media provider is its own small adapter** (`MediaClientFactory`), because voice and picture APIs have no shared .NET interface the way chat has `IChatClient`. `AiProviderAbilities` says which kinds chat, speak or paint, and the admin endpoints only let a role take a provider that can do it. The adapters added in #33:
- **ElevenLabs** (`ElevenLabsSpeech`): one HTTPS call per clip. Characters are cast with OpenAI's six voice names everywhere in the app, so `ElevenLabsVoices` maps each to one of the account's voices, read from `GET /v1/voices` once an hour: ElevenLabs' default voices by name first, then the account's own by gender, so deep characters stay deep.
- **Piper** (`PiperSpeech`): a local server, `POST /synthesize` returning WAV (and the text posted to `/` for servers from before 2025). For a voice with several speakers, `PiperVoices` gives each character one, always within the voice's speaker count (read from `/voices` or `/info`).
- **Stable Diffusion WebUI** (`StableDiffusionImages`): a local AUTOMATIC1111 or Forge, `POST /sdapi/v1/txt2img`, at the size the checkpoint was trained for (`StableDiffusionSizes`).
- **Local OpenAI-compatible servers** need no key when a base URL is set.

Piper and Stable Diffusion are logged as free, like Ollama. "Test connection" on Admin → AI makes a one-word clip for a voice provider and lists the checkpoints for Stable Diffusion (`IMediaClientFactory.CheckAsync`).

**Why prepare everything up front:** voice clips and pictures take seconds each. Making them while the guests watch would stall every scene. Instead, creating a party queues a job that makes every portrait, the victim and setting pictures, a picture for each clue card, all narration and every NPC line. When it finishes, every screen of every party using that mystery is refreshed.

**Why a cache keyed by a hash:** the same sentence in the same voice from the same model always sounds the same, so it's only paid for once. The hash of the request is the `MediaAssets.ContentHash` (unique), which also stops two workers from saving duplicates at the same time.

**Why an overlay instead of editing the scenario:** hand-written scenario JSON stays exactly as written, and `ScenarioMedia` maps keys like `portrait/finch` or `line/finch/act1/0` to files. `MediaOverlay` (pure, in `ButlerDidIt.Game`) fills them in only where the author left `src`, `portrait` or `image` empty, so hand-made art always wins over the AI's.

**A host's own media for a mystery** (`MysteryMediaEndpoints`, the editor's "Pictures, video & music" tab):
- **What a host can upload:**
  - the cover (`setting`), the victim, each character's portrait and each clue's picture;
  - a video for the opening scene, for each act and for the finale (`video/prologue`, `video/{actId}`, `video/finale`);
  - background music for the evening and for each act (`music`, `music/{actId}`).
- **Same pipeline as the escape rooms.** Upload, checks, allowance and shared files all go through `MediaUploads` (see §14). Only the owner or the admin can upload; hand-written mysteries only the admin, like the editor.
- **Uploads win over everything.** `MediaOverlay.Apply` takes the uploaded keys separately: they replace even hand-placed media, while the AI's still only fills gaps.
- **A scene's video *is* the scene:** it replaces that scene's narration, pictures and sounds. The NPCs' lines and the drinking toasts still follow it.
- **Music:**
  - `Scenario.Music` and `Act.Music` reach the stage as `StageView.MusicUrl`: the current act's, else the evening's, and none from the reveal on. That's never a later act's.
  - The stage loops it in an `<audio>` element (`useBackgroundMusic`), softer while a scene plays.
- **Background sound without uploads** (#127): a mystery is never silent. `ThemeDefinition.Soundscape` sets each theme's preset (a country house in a storm, a train, a lounge…), and `Scenario.Soundscape` and `Act.Soundscape` can override it.
  - `ContentCatalog.GetScenarioAsync` fills in the theme's when the mystery names none (`ScenarioDefaults`), so the pure engine never needs the theme, and AI-written mysteries get one for free.
  - `StageView.Soundscape` follows the same rules as the music: the current act's, else the mystery's, and `silence` from the reveal on.
  - The stage synthesises it with the escape rooms' `Atmosphere` (`useSoundscape`), quieter during scenes, only when there's no uploaded track.
- **Versions share the original's uploads.**
  - Uploads are stored under the original mystery's id. `ContentCatalog.GetScenarioAsync` merges them over each version's own AI media, and `InvalidateFamily` forgets every version when they change.
  - The editor warns that a finale video plays in every version, each with a different killer, so it shouldn't name one.
- **Nothing is wasted or deleted by mistake:**
  - `MediaWorker` doesn't paint what a host uploaded, or voice narration a video replaces.
  - Editing a mystery keeps uploads unless their place is gone (a removed character, act or clue, or a renamed clue).
  - Deleting a never-played mystery frees its files.
- **Never early:** `ViewProjectorTests` checks that no act's video or music, and no finale video, reaches a screen before its moment.

**Why clue pictures use only the title:** a clue's text (and whether it's a red herring) can change between versions of a story, so the prompt is just "an evidence photograph of: *title*". The key, `clue/{id}/{hash of title}`, changes if the clue is renamed, so a renamed clue gets a new picture instead of the wrong one. Views only carry the clues a screen may see, so a private clue's picture reaches only its recipient.

**Why background jobs claim work atomically:** `MediaWorker` and `GenerationWorker` move a job from `Queued` to `Running` with a single `UPDATE … WHERE Status = 'Queued'`. Only one worker can win, so a job never runs twice, even with several app instances. A media job interrupted by a restart goes back to `Queued` and skips what's already done.

**Voiced NPC answers** are made on demand, after the answer text is stored: the stage shows the text immediately and plays the clip when it arrives (it waits a few seconds for it, then falls back to the browser's voice).

**Costume selfies** (`POST /api/seat/photo`, seat token) are decoded and re-encoded with SkiaSharp. That drops every bit of metadata, including GPS location; the photo is turned upright and shrunk to 640 px. Only the URL goes into the game state (`SetPlayerPhoto`).

**The printable kit** (`/api/parties/{code}/kit/*.pdf`, host only) is drawn with QuestPDF: invitations with a QR code (QRCoder), name tags, character booklets (with secrets) and clue cards with a sealed solution.

**Toast prompts** are a `toast` cue type. `ViewProjector` drops them unless the host switched on drinking prompts, which is always off for Family parties. Every toast carries a non-alcoholic alternative.

## 11b. Feedback and insights (#130)

**What it's for:** which mysteries and rooms work, and where groups get stuck, so the people who edit them know what to fix.

- **One record per game** (`PlayRecords`): written in the existing `GameSession.OnSaving` hook, in the same save as the moment it records.
  - **Mysteries:** written when the accusations become final, as the game moves from accusation to reveal. It holds:
    - how many guests accused, and how many named the killer;
    - who got accused, by character.
  - **Escape rooms:** written next to the leaderboard's `EscapeResult` when the clock stops. It holds:
    - escaped or not, the difficulty, the length and the hints;
    - for each puzzle played (`RoomFor`), the seconds from its stage opening to its solving, the hints shown, and whether time ran out with it in front of the group.
  - **Built by a pure function:** `PlayRecords.ForMystery` and `ForEscape`, from state the reveal or the ending already showed the table.
  - **Kept apart from the party,** which `RetentionWorker` strips, so a mystery's history outlives its parties.
- **One verdict per guest** (`PlayFeedback`, keyed by party and seat):
  - **What they give:** 1 to 5 stars, too easy, just right or too hard, and on Adults content only a comment of up to 280 characters.
  - **Why no comments on Family:** Family games may be played by children, so they're never asked for free text, and the server drops any text sent anyway.
  - **When it's accepted:**
    - `POST /api/seat/feedback`, authorised by the seat token, from the reveal on (or once the clock stops);
    - a guest can change their answer;
    - a watcher's token has no seat, so watchers can't rate.
  - **It's anonymous:** no names are stored.
- **Insights:** `GET /api/insights/{mystery|escape}/{id}`.
  - **Who can see them:** the same rule as the editors. A mystery's or room's owner sees them, and so does the admin; built-in content is admin only. Anyone else gets 404.
  - **A mystery's versions add up,** and each version is shown on its own, since each has its own killer.
  - **For escape rooms:** a per-puzzle table with the average time, the hint rate and where groups ran out of time. The slowest and most-hinted puzzles are badged with an icon and words.
  - **Library cards:** `PlaySummary` puts ★ and plays on the My mysteries and My escape rooms cards.
- **Deleting an account:**
  - records and feedback about their own content go with it;
  - their games of anyone else's content stay in that content's insights, unnamed and without the guests' comments.

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
| Two servers starting together would both seed content, and migrations should be a deploy step | Startup takes a `startup` lock; `--migrate` migrates and exits; `Database:MigrateOnStartup=false` skips it on the servers. The others wait for the lock for up to 10 minutes, not the 30 seconds a database command gets by default: on a fresh database, migrating and seeding takes longer than that (#93) |
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
- **Locks to find and final locks** (#134): a puzzle with `revealedBy` (a spot, another puzzle or an item in its stage) starts out of sight, and a puzzle with a `final` generator appears once the rest of its stage is solved.
  - `EscapeEngine.Visible` and `InSight` are the one test of what the group can see. `Reveal` runs after every search, solve, closer look and recipe, and as a stage opens. It records finds in `EscapeState.Revealed`, logs them, and deals them when puzzles are dealt.
  - A lock out of sight can't be answered, taken, hinted or dealt, and its hidden clue pieces stay put. `OpenPuzzle` gives it the same error as a puzzle that doesn't exist.
  - **The projector sends only the puzzles in sight.** `PuzzleCount` counts the ones found so far while the game plays, and the recap lists only the ones found.
  - **A final lock is made in two steps.** `FinalLocks.Make` (called by `RoomVariants.Build`) picks every mark, digit and the order from the seed. `FinalLocks.Assemble` (called by `RoomLengths.Cut`) keeps the parts of the puzzles that game plays, sets the code and fills `{order:<id>}`, `{count}` and `{answer}`. It's two steps because which puzzles a game keeps depends on its length, which `Cut` decides after `Build`.
  - The same cut lets a puzzle whose spot or trigger puzzle the game leaves out be in sight from the start.
- **The clock:**
  - it's a `Deadline`, and each hint moves it earlier;
  - `EscapeEngine.NextDueAt` hands the deadline to the platform's ticker, which ends the game when time runs out;
  - a wrong answer locks that puzzle for 3 seconds, so a code can't be brute-forced from a script.
- **Answers are checked on the server**, and forgivingly: case, spacing, punctuation and a leading "a", "an" or "the" don't matter. They never reach a browser. `PrivacyTests` walk every room through every stage and search the serialized views for answers, unpaid hints, other players' clue pieces and later stages' puzzles.
- **`EscapeRoomValidator`** checks references and answer formats, keeps Family rooms free of gruesome words, and **proves each room can be escaped**: it plays the room greedily, solving any puzzle in sight whose items are in hand, and reports the first stage that gets stuck. Locks to find count only once that play has found them, and a final lock only once its order has been read. The app refuses to start with a broken room.
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
- **Moments (cues):** with `GameMaster` on, the engine records an `EscapeCue` for each moment worth reacting to: the start, a solve, a stage opening, a final lock appearing, three wrong answers in a row, five minutes left, the escape and the failure. There is one cue per moment (an escape is not also a solve). "Five minutes left" is a tick: `NextDueAt` returns that moment first, then the deadline.
- **Lines:** the engine never calls the AI. After every committed save, `PartyRuntime` calls each `IPartySavedHandler`. `NarrationTrigger` queues the party when a new cue appears, which covers the ticker's changes too. `NarrationWorker` then has `EscapeGameMaster.NarrateAsync` write a line for the newest cue (Actor role, `TASK: escape-narration`) and record it (Voice role). Older waiting cues are passed over (`SkipCues`), and cues older than a minute are never narrated. With a voice, the line is recorded first and comes back with its recording in one `SetCueNarration`, so the TV shows the words as it starts saying them (#132: shown first, they ran 5 to 7 seconds ahead of the voice). A recording that fails or takes over `VoiceWait` (12 s) is left out, and the TV's browser reads the line at once. A failed call is logged and passed over, because the ticker still says what happened. A `ClusterLock` stops two servers speaking for one party.
- **Hints:** with `Hints` on, a hint is `BeginEscapeHint`, which pays the time and reserves the step exactly like a written hint. `EscapeGameMaster.RequestHintAsync` then asks the AI outside the lock (Inspector role, `TASK: escape-hint`) and returns `CompleteEscapeHint`, or `CancelEscapeHint` on failure. The **engine** checks the text with `EscapeHintGuard` against the answers of the puzzle set being played. A hint that spells out an answer (digits in any form, words in any case, with or without accents or a plural) is dropped, and the written hint for that step shows instead. So does a hint still missing after `AiHintTimeout`, so a server restart mid-call never leaves a puzzle stuck "thinking".
- **Privacy:** the narration prompt is built from the TV's public view. The hint prompt holds the puzzle, its clue pieces with who holds them, the items, the recent wrong tries, the hints already shown and the author's written hint for the step, but never the answers. `EscapePromptTests` play every room over 200 puzzle sets and check that no prompt holds an answer the group can't already see.

**Lengths:**
- A room offers `lengths` (30, 45 or 60 minutes), and a puzzle can have `minMinutes`. The host picks a length, and it's kept on the state (`EscapeState.Minutes`).
- `EscapeEngine.RoomFor` is `RoomLengths.Cut(RoomVariants.Build(room, seed), minutes)`: the chosen puzzle set, minus the puzzles kept for longer games (and any stage left empty), with the clock at the chosen length. Both steps are cached. Everything downstream (rules, projector, prompts, art) sees the cut room, so a shorter game needs no special cases.
- The validator checks every length over 200 puzzle sets. Results record their length, and the leaderboard ranks each length apart. Results from before lengths existed count as the room's standard length.
- `EscapeRoom`, `EscapeStage` and `EscapePuzzle` are records, so a built or cut room is made with `with { … }` and every setting carries over. Hand-copying had already dropped a setting once.

**Harder rooms** (#82):
- **Scenes:** a stage can have a `scene` of spots. `ExamineSpot` searches one: the first search takes its look, item and notebook clue, any hidden pieces go to the searcher, and every `Search` puzzle whose `finds` are all searched solves itself. A spot can be searched again; a search that turns up nothing new for the searcher costs `SearchPenaltySeconds` (Easy 0, Normal 10, Hard 20, #132) and raises a `Decoy` cue.
- **Who answers** (#132): `EscapeState.Answering` is `Anyone` (as before, and for parties saved before), `TakeIt` or `Dealt`, chosen on the host page (and saved in `HostPreferences`). While `TakesTurns()` (more than one player), `Holds` maps each open puzzle to whoever is working on it:
  - `TakePuzzle`, `ReleasePuzzle` (by its holder, or the host from the TV) and `PassPuzzle`. `Attempt` refuses an answer, item use or switch press from anyone but the holder. A Search puzzle can't be held: searching is everyone's.
  - With `TakeIt` a player holds one unsolved puzzle at a time. `Dealt` deals each stage's puzzles round a seed-shuffled table as it opens (`DealStage`, continuing from `DealOffset`), so the counts stay within one.
  - A holder's `Misses` (3 wrong answers in a row) free the puzzle, and another player can take it over once `Active` (the last try or take) is `TakeOverAfter` (3 minutes) old. The view's `EscapeHoldView.Free` says so.
  - **Searching with a purpose:** a puzzle's own things turn up only for its holder (`MayFind`): its hidden pieces, and spots whose look or clue writes its key (`KeyPuzzlesAt`, the current stage's unsolved puzzles only). For anyone else such a spot stays unsearched, so its text stays unseen. `KeysHidden` counts its key spots not yet found, never where.
  - The views carry the effective rule (`Anyone` with one player), each puzzle's `HeldBy` and the stage's `SearchPenaltySeconds`. `TurnTests` cover the rules and play every built-in room through with three players under each rule; `EscapeBot` takes, passes and searches as a holder would. `InspectItem` and `CombineItems` do the same for closer looks and recipes, and `PressSwitch` works a light panel (`EscapeState.Switches` holds the lights once touched). The hub's actions are `EscapeExamine`, `EscapeInspect`, `EscapeCombine` and `EscapePress`. The projector sends a spot's look only once it's searched, an item's closer look only once looked at, and never the recipes.
- **New generators** (`PuzzleGenerators`): ciphers, number patterns, logic puzzles and light panels. Each is built to have exactly one answer, and `PuzzleProofTests` prove it over 1,000 seeds. A cipher's key is written into the room wherever it says `{key:<puzzle id>}`, and `RoomVariants` notes on the puzzle where that is (`KeyAt`), so the validator can check the group reaches it in time. `SeededRandom` is the SplitMix64 generator shared by all of them.
- **Difficulty:** `EscapeState.Difficulty` (null is Normal, so parties saved before it are unchanged). `RoomFor` is `RoomLengths.Cut(RoomVariants.Build(room, seed, difficulty), minutes, difficulty)`. `Build` sizes the generators, picks cipher words, halves the hint penalty (Easy) or drops the last hint step (Hard); `Cut` drops puzzles above their `minDifficulty`. Normal takes exactly the path it always did, so every existing room builds byte-for-byte the same puzzles. Results record their difficulty, and the leaderboard ranks each one apart (`?difficulty=`, with old results as Normal).
- **Editions:** `EscapeRoom.Edition` numbers a rebuilt room. Results store the edition they were played on (null counts as 1), and `EscapeResults.AtEdition` keeps each room's leaderboards and the shelf's best time to its current edition.
- **Hard-only spots:** `SceneObject.MinDifficulty` works like the puzzle field. `RoomLengths.Cut` leaves those spots out of easier games, so Hard gets extra decoys, red herrings and key chains without touching Normal.
- **Hiding pieces:** at the start, a puzzle's pieces beyond the player count go into its stage's `hidesPieces` spots, chosen from the seed (`PieceHolder` with a `SpotId` and no seat). Searching the spot hands the piece to the searcher. A room without hiding spots deals exactly as before.
- **The validator's play-through** is a fixed point per stage: search every spot it can, look at every item, put together every pair, solve every puzzle it can, and repeat until nothing changes. It's exact because an item is either used up (by one puzzle or recipe) or a tool (never used up), never both, so no order of play can strand the group. It runs for every length × difficulty × 200 puzzle sets.
- **Screens** (`src/web/src/escape/`):
  - **The TV layout** (#116). While the clock runs on a screen at least 1024×600 (`TV_LAYOUT`, read with `useMediaQuery`), `TvRoom` fills the screen and never scrolls, because nobody works the TV during a game.
    - **Header:** the stage, its description (2 lines at most), the clock, sound and the host's watchers chip.
    - **Left column:** the scene, sized with container query units (`SceneView fit`) to fill its box at its own proportions, then the game master's line, then what's happened and what's been searched, side by side, newest first and fading out at the bottom.
    - **Right column:** first what the group holds and its latest notebook entries (`Found`, never cut off: on an 85" TV they had been squeezed into the bottom of the left column, #132), then the puzzles in two newspaper-style columns, open ones first and solved ones shrunk to the end, each saying who's working on it. The puzzles sit in `FitToScreen`, which scales them down to fit (to 0.6) before a small laptop scrolls their box. `escape-turns.spec.ts` checks 1080p and 1440p never scroll and the found panel stays on screen at 720p.
    - **Why it fits:** the busiest stage in any room has six puzzles. The escape e2e checks that the page never scrolls, that every card is wholly on screen at 1440×900, and that no stage's puzzles overflow during a whole game.
    - **Smaller screens:** phones (someone watching) keep the stacked layout, which scrolls.
  - `SceneView` draws a scene as HTML buttons placed over the picture, by percentage of the canvas, so every spot works by keyboard and screen reader; `props.tsx` draws each prop as a small SVG.
  - `ItemInspector` holds, inspects and combines items; `Notebook` shows the notebook.
  - `PuzzleWidgets` has the light grid, the logic grid and line-up, the cipher decoders and the pattern display.
  - A cipher's view (`EscapeCipherView`) says which decoder to show and is `unlocked` only once `EscapeState.KeysFound` has it. The engine records a key as found after any command that makes one of its `KeyAt` places visible, and it stays found. Key cards and shift amounts are sent only for keys the group has found (each is already written on a spot they saw). **Decoy keys:** a cipher may write `{key:<id>}` in several places. `RoomVariants.WithKeys` picks the real place per seed among those the game's difficulty shows (`RoomLengths.Shows`), and fills the others with decoys from `PuzzleGenerators.Cipher` (key cards that read a `decoyWords` word, or wrong shift amounts), recorded in `CipherDecoder.Candidates`. The view lists only the keys found so far (`EscapeFoundKey`, labelled by place), never which one is real. `EscapeRoomValidator.ValidateGame` checks one puzzle set the way it's played: built and cut at the same difficulty.
  - The logic helpers keep their marks in the phone's `sessionStorage` and never send them.
  - The e2e tests play the test-only Laboratory, which `Escape:TestRoomsRoot` adds to the shelf only when `Escape:ExposeAnswersForTests` is on.
- **AI:** the hint prompt includes only searched spots, looked-at items, found pieces and the notebook, plus whether a cipher's key has been found (never the key), and on Hard it asks for a nudge only (`EscapeScenePromptTests`). A search that turns up nothing (and costs time, so not on Easy) raises a `Decoy` cue for the game master to tease.
- **AI-written rooms** use every kind (#86). `ShapeErrors` asks for a scene in every stage (with decoys and hiding spots), a search, a closer look or recipe, a cipher, a logic puzzle, a Hard-only part and at most 2 `use` steps, and a puzzle count that fits the clock. It also refuses cipher words or riddle answers already on screen (`EscapeRoomText`, shared with the content tests). `Normalize` places the spots on a grid (the model never writes positions) and drops what only a build or a hand-written room has (lengths, variants, grids, key locations). `Anonymize` renames puzzles and spots, including in `{key:…}`, `{order:…}` and `revealedBy`. The blind solver tests riddles with everything their stage's spots and items show, and the logic puzzles as one fixed seed builds them.
- **AI rooms have locks to find and a final lock** (#143). `ShapeErrors` asks for at least two puzzles with `revealedBy` and a final lock in the last stage; the validator proves they work. The blind solver also tests each final lock as a Normal game of the room's length builds it (`RoomLengths.Cut`, so the order is written in): the mark and digit each other lock in its stage leaves, in the stage's order, and everything that stage shows.

**Atmosphere** (the TV only; phones stay quiet):
- **Sound is synthesised in the browser** (`src/web/src/lib/sound.ts`, Web Audio), so rooms ship no audio files and there's nothing to license.
  - A room, and optionally each stage, names a `Soundscape` preset: drone, workshop, carnival, sea, space, haunted, manor, storm, train, night, lounge, arcade, concert, stadium, meadow, cave, tension or silence. The view carries the current one.
  - The presets are shared with the murder mysteries (#127): the enum lives in `ButlerDidIt.Game/Scenarios/Soundscape.cs`, and the synth in `lib/sound.ts`.
  - `useAtmosphere` works out the stingers from what changed between two views: an unlock, a new stage, a wrong answer, a hint, the escape or the failure. It also sounds a gong at one minute left, then a heartbeat that speeds up. Because the view is its only input, a TV that reconnects just carries on.
  - Browsers only allow sound after a click, so the TV shows a sound switch. Its setting is remembered on the device.
- **Room art goes through the mystery media pipeline.**
  - `EscapeMediaPlan` asks for a cover and one picture per stage. The prompts are built only from text the TV already shows, never puzzles, pieces or answers, and `EscapeMediaPlanTests` checks this over many puzzle sets.
  - An escape party created with the AI on queues a `MediaJob` whose id is `escape:{room id}`, so it never mixes with a mystery's. `MediaWorker` paints the pictures once per room, `MediaService` caches them, and every party of the room reuses them.
  - `EscapeCatalog.ArtAsync` loads them into the session, cached for 30 seconds so pictures from another server show up soon. `EscapeProjector` picks the current stage's picture, or the cover.
- **Room reveals** (#110, step 1):
  - **On the TV:** pressing Start plays the room's intro full screen: the cover with a slow pan, and the welcome read out in the game master's voice with subtitles. The clock starts when it ends or is skipped, so nobody loses time watching it. Each later stage opens with a short reveal of its own picture, name and description; the clock keeps running in the corner, and anyone can skip it.
  - **On the phones:** a card at the top shows the same picture and text and is tapped away. It's a card, not a pop-up, so it never blocks a player mid-puzzle, and phones stay quiet.
  - **Built from the TV's own view** (`escape/reveal.ts`, played by the mystery's `CuePlayer`). So it can't show anything the TV couldn't already: the projector only ever sends the current stage's picture.
  - **Read aloud by the game master** (#127). With a Voice role set up, `EscapeMediaPlan` also records the intro (`intro-voice`) and each stage's description (`stage-voice:{id}`), in the voice the game master uses for its live lines, in the same once-per-room job as the pictures. `EscapeProjector` sends `IntroVoiceUrl`, and `StageVoiceUrl` only for the stage in front of the group, and the reveal's narration cue plays the clip. Without one, the browser reads the words. The recording is of text the TV already prints, so it says nothing the screen doesn't. A moment with an uploaded video gets no recording, and editing a stage's words re-records only that stage (`StaleArt` compares whole plans).
  - **Once per screen:** each screen remembers the reveals it has shown in `sessionStorage`, so a refresh or a reconnect doesn't replay them.
  - **Reduced motion:** the pan stops under `prefers-reduced-motion`.
  - **Uploaded videos** (step 2, below) play in place of the picture and the read-out text. AI video clips are step 3 of #110.
- **A room's own pictures, videos and sounds** (#110 step 2, `EscapeMediaEndpoints`):
  - **Stored like the AI's pictures.** Each is a `ScenarioMedia` row for `escape:{room id}` under an `EscapeArt` key: `cover`, `intro-video`, `ambience`, and per stage `stage:{id}`, `stage-video:{id}` and `ambience:{id}`. So an uploaded cover shows on the shelf, the TV, the phones and the recap with nothing else to change, and `MediaWorker` never paints over it (it only fills empty keys).
  - **Who:** the owner of a room, or the admin. Built-in rooms only the admin, and then every host's games get it. Anyone else gets a 404, like the editor. Changes apply at once, and `PartyRuntime.RefreshAsync` updates the screens of any party playing the room.
  - **Checked by the bytes**, never by the file's name or the type the browser claims (`MediaFormats`). Pictures (10 MB) are re-encoded by `PhotoProcessing` at up to 1920 px, which drops hidden details such as where a phone photo was taken. Videos (MP4, MOV or WebM, `Media:MaxVideoMb`, 100 MB) and sounds (MP3, M4A, OGG, WAV or WebM, 20 MB) are kept as they are. The editor also checks that the browser can play a file before sending it.
  - **The body is the file**, not a form. It's copied to a temporary file as it arrives, counting the bytes, so a large video never sits in memory and a body that lies about its length is still stopped. The endpoint raises Kestrel's 30 MB request limit for itself only. A raw body also isn't something another website can send with the host's cookie, as it could a form.
  - **Allowance:** each host can upload `Media:UploadQuotaMb` (2 GB) in all, counted by `MediaAsset.OwnerUserId`; the admin has no limit. A file being replaced doesn't count.
  - **Shared files:** a copy of a room starts with the original's rows, so they share files. `MediaService.DeleteUnusedUploadsAsync` deletes an upload only when no row points at it, and never deletes what the AI made (it's cached and shared). Replacing or removing media, deleting a room and deleting an account all go through it.
  - **Editing a room** forgets the AI pictures whose words changed, but keeps the host's uploads, unless their stage is gone.
  - **Never early:** the view carries the intro video, the current stage's video, and the current stage's (else the room's) sound. A later stage's are never sent, and `PrivacyTests` checks it for every stage of every room.
  - **On the TV:** `RoomReveal` plays an uploaded intro or stage video in place of the picture and the read-out text, with the background sound turned down under it. If the browser won't start it with sound, `CuePlayer` plays it muted rather than not at all. An uploaded background sound loops through `Atmosphere` in place of the made-up one, under the same volume and sound switch, and the stingers still play on top. Phones only show the pictures.
- **Finale:** doors swing open on an escape, and bars drop when the group is trapped. It uses movement only, never flashing, and nothing moves under `prefers-reduced-motion`. The results, the ranking and the game master's captioned last line stay on screen.

**The recap and share card** (#111):
- **What it holds.** `EscapeProjector.Recap` builds an `EscapeRecapView` from the saved state once the game is over:
  - the result: time, time to spare, hints and score;
  - the team with their photos, and each player's count of puzzles opened;
  - the timeline, worked out from `Solved` (`PuzzleId`, `SolvedBy`, `At`): a stage opens when the stage before it is cleared, and is cleared by its last puzzle;
  - a few highlights: most puzzles opened, first breakthrough, fastest stage, no hints, a photo finish;
  - the game master's latest lines.
- **It never spoils the room.** A shared recap can reach friends who will play the room next. So it carries no answers, prompts, hints, solved texts (a generated puzzle writes its answer into those) or clue pieces, and only the stages the group reached. Puzzle titles are fine: the TV listed them.
  - **Tested:** `RecapTests` finishes every room at every length, once escaped with a hint taken and once trapped, and searches the recap's decoded text for all of those.
  - **Why decoded:** the JSON writer escapes characters such as a curly apostrophe, so searching the raw JSON could miss a leak. `ViewText.Decoded` does this, and the live screens' `PrivacyTests` now use it too.
- **Sharing works like the mystery's** (`RecapEndpoints.cs`). Share and unshare only set `Party.RecapSlug`, for either game, and the link depends on the game: `/escape/recap/{slug}`. Other details:
  - The host previews it at `GET /api/parties/{code}/escape-recap`.
  - The public page reads `GET /api/escape-recap/{slug}`, sent with `noindex`.
  - **Rank:** the leaderboard place is counted when the page opens, on the board the game was played on: room, length, difficulty and edition, plus the day for a daily challenge. It uses `EscapeResults.RankOfPartyAsync`, and the leaderboard shares its counting rule (`RankInAsync`).
  - **Scoring:** time and score come from `EscapeEngine.ElapsedSeconds`/`Score`, the same functions that record the leaderboard row, so the two can't disagree.
  - **A deleted room** (an AI room its host deleted) has no recap, because the state alone doesn't hold the room's text.
- **Link previews.** Chat apps read a page's Open Graph tags and never run its JavaScript. So `GET /escape/recap/{slug}` serves `index.html` with the tags written in: the result as the title, the team as the description, the cover as the picture. Every value is HTML-encoded, since guests type their own names. An unknown link gets the plain app, which says the recap isn't available.
- **The share card** (`escape/shareCard.ts`) is a 1080×1350 PNG drawn on a `<canvas>` **in the browser**:
  - **Why the browser:** it already has the site's fonts, which the server's image lacks, and a phone's share sheet takes a picture file directly (`navigator.share({ files })`). Where files can't be shared (most computers), it downloads instead.
  - **Why the canvas can be saved:** the cover comes from the same site, so the canvas isn't "tainted" and can be exported.
  - **Where it appears:** the host's recap panel on the TV (`RecapShare`, shared with the mystery), and the public recap page, so players can share it too.

**Rooms written by AI** (`EscapeRoomGenerator` in `ButlerDidIt.Ai`, run as a `GenerationKind.EscapeRoom` job by `GenerationWorker`):
- **Writing.** The Storyteller (`TASK: escape-room-write`) writes the whole room as JSON in the same format as `content/escape/`. It writes the story, the riddles, the items and the game master, and it chooses which proven templates (`digitFacts`, `colorDigits`, `wordSequence`) fill the code and password slots. **It never writes a code:** those come from the generator and the seed, so they're correct by construction and shuffled every game like any other room.
- **Checks, with repairs.** Each draft is normalised: the server sets the id, rating, clock, hint penalty and player range, and strips variants. It then goes through `EscapeRoomGenerator.ShapeErrors` and `EscapeRoomValidator`. The shape rules are: 2–4 stages, 5–10 puzzles, at least two generators, a riddle and a `use` puzzle, every code from a generator, and no riddle that gives its own answer away. The validator adds its 200-seed play-through. Problems go back to the model, up to three repairs.
- **The blind tester.** `EscapeRoomSolver` (Inspector, `TASK: escape-room-solve`) sees each riddle the way the group would: its prompt and every clue piece, numbered, never the answers, the hints or the model's puzzle ids. A missed riddle gets one repair turn, then the room is kept with a warning.
- **Neutral ids.** The model names puzzles after what they are ("echo-riddle"), and puzzle ids reach the browsers. So once a room passes, `Anonymize` renames them `puzzle-1`, `puzzle-2`… This runs last, so repair turns can still quote the model's own ids back to it.
- **Storage and access.** A room is saved as an `EscapeRoomEntity` (jsonb) for its host only after it has passed.
  - `EscapeCatalog` merges the file rooms with the DB rooms. `FindAsync` is for loading a party (guests of any host), and it caches the parsed room because it never changes. `FindForHostAsync` is for starting a party, and it always asks the DB for the owner, so a deleted room can't be started from another server's cache. `OwnedAsync` builds the host's shelf.
  - Only the owner sees a room on the shelf (`mine: true`), starts parties with it, edits it or deletes it. Its results and leaderboards stay after a delete.

**The room editor** (#113, `EscapeEditorEndpoints.cs`, `/escape/rooms/:id`). It's the escape rooms' version of the mystery editor.
- **Who edits what.**
  - A host edits their own rooms: the ones the AI wrote for them, and their copies.
  - **Any host can copy any room**, a built-in one included, to change a riddle or put their family's names in. The copy is a record `with` a new id, a "(copy)" title and edition 1, so every other setting carries over. It's marked `CopiedFrom`, and it shares the original's pictures.
  - Built-in rooms are never edited in place: they're read from `content/escape` at every start.
- **Answers stay out of sight.** The editor shows every answer only after a "Spoilers!" warning. Generated codes are never written in a room, so they stay a surprise.
- **Finding and final locks** (#134, #143). Each puzzle has a "How the group finds it" choice (`revealedBy`). A final lock has a marks editor instead: tap a mark to remove it, or type a new one. It refuses a mark with a digit, a long one or a repeat (the validator's rule), says when the stage has more puzzles than marks, and offers the standard set (`DEFAULT_MARKS`, mirroring `FinalLocks.DefaultMarks`).
- **Checked like a shipped room.**
  - A room is saved only when `EscapeRoomValidator` proves it can still be escaped at every length and difficulty over all 200 puzzle sets.
  - The full check takes a second or two, so checking as you type uses 12 (`Validate(room, seeds)`), and saving runs the full one.
  - A refused save is a problem whose message is the first error, with the whole list attached.
  - Limits: 200,000 characters, 6 stages, 30 puzzles. The AI-only shape rules don't apply to a host's edits.
- **A room in play waits.** Saving is refused while a party is in the lobby or playing it, as the mystery editor does.
- **Editions.** The server sets the edition, never the document.
  - A change that alters how the room plays starts a new edition, so its leaderboards start fresh. That's anything but the title, synopsis, intro, endings, look, sound, seasons, game master and the stages' names and descriptions.
  - A reworded story keeps the old edition, and its leaderboards.
- **Pictures.**
  - **Stale pictures:** the keys whose `EscapeMediaPlan` prompt changed (or whose stage went) are forgotten, so only those are painted again.
  - **Tidy documents:** documents are written without the room's read-only properties and nulls.
- **Every server sees an edit.**
  - The catalog caches each database room with its `UpdatedAt`. Party loads check that time with one small query, and re-read the document only after an edit.
  - An edit saved on another server therefore reaches this one's next command; `EscapeEditorTests` checks this, and fails with the old "cached for ever" rule.
  - A deleted room is now gone on every server at once.

