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

1. **A lock per party** (`PartyLocks`, a `SemaphoreSlim` per party id): commands for one party run one at a time; different parties never wait on each other.
2. **Optimistic concurrency** (`Party.Version` mapped to Postgres' `xmin`): if two saves ever do race, for example after scaling to several servers, the second fails instead of silently overwriting.

## 5. Real-time updates with SignalR

`PartyHub` is the real-time channel. SignalR picks the best transport (WebSockets, falling back to long polling), and organises connections into **groups**:

- `stage:{partyId}`: every screen watching the stage
- `seat:{seatId}`: one guest's phone(s)

After every command, `PartyService.BroadcastAsync` sends a **complete snapshot** (not a diff) to each group. Snapshots are a few KB, and a phone that missed messages while asleep fixes itself with the next one.

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

## 7. Content: themes and scenarios

`content/themes/<slug>/` holds `theme.json` (palette, era, art style), `scenarios/*.json` (the mysteries) and `media/` (images, audio, video). On startup, `ContentCatalog.SeedAsync` validates every scenario (`ScenarioValidator`) and upserts them into the database. A broken mystery fails the deploy, not a party.

Only `media/` folders are served over HTTP (`MediaEndpoints.cs`), with a path check against `../` tricks. The scenario JSON sits next door and contains the solution.

## 8. The front end

- **Pages** (`src/web/src/pages`): `Home`, `Login`, `NewParty`, `Join`, `Stage` (TV + host controls), `Play` (phone), `PassAndPlay`.
- **`PlayerScreen`** is the whole phone experience. Pass-and-play reuses it for each local seat.
- **`CuePlayer`** plays cinematics: a list of cues (image, narration, NPC line, music, sound, video). Narration uses an audio file when the cue has one, otherwise the browser's built-in speech synthesis. Browsers block sound until the user interacts, which is why the stage starts with a "Tap to begin the evening" button.
- **Theming**: colours are CSS variables that `useThemePalette` swaps per theme; Tailwind utilities (`bg-surface`, `text-accent`) read those variables.

## 9. Deployment shape

One image (`Dockerfile`, three stages: Node builds the SPA, the .NET SDK publishes the API, and the small ASP.NET runtime image runs it) plus Postgres in `docker-compose.yml`. The API serves the SPA from `wwwroot` and falls back to `index.html` for client-side routes, so everything shares one origin: no CORS, and cookies and WebSockets just work behind Coolify's proxy. See [deploy-coolify.md](deploy-coolify.md).

## Where milestone 2 (AI) plugs in

- **Mystery generation** produces a `Scenario` JSON in the same format, validated by the same `ScenarioValidator`, stored with `Source = AiGenerated`.
- **AI NPCs** get a new hub method (e.g. `AskNpc(characterId, question)`). The NPC's prompt is built from its `CharacterPrivate` sheet, so it only knows what that character knows.
- **Voices and images** (milestone 3) fill the empty `src`/`portrait` slots that the UI already renders. The `MediaAssets` table caches generated files by content hash, so each one is paid for once.
