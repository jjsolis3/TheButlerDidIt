# The Butler Did It

Murder-mystery party game: ASP.NET Core 10 + SignalR + EF Core/PostgreSQL back end, React + TypeScript (Vite, Tailwind) front end. See docs/architecture.md.

## Commands

- Back-end tests: `dotnet test` (needs Postgres; `TEST_DATABASE_URL` overrides the default `Host=localhost;Username=butler;Password=butler`). The multi-server and S3 tests are skipped unless `TEST_REDIS_URL` (e.g. `localhost:6379`) and `TEST_S3_URL` (an S3 emulator such as `moto_server -p 9000`, with `TEST_S3_ACCESS_KEY`/`TEST_S3_SECRET_KEY`) are set; the Stripe adapter's tests need `TEST_STRIPE_MOCK_URL` (`go install github.com/stripe/stripe-mock@v0.206.0`, then `stripe-mock -http-port 12111`)
- Live AI checks: `dotnet test tests/ButlerDidIt.Ai.LiveTests` calls the real providers only when `LIVE_ANTHROPIC_KEY` / `LIVE_OPENAI_KEY` / `LIVE_GEMINI_KEY` / `LIVE_OLLAMA_URL` are set (skipped otherwise); see docs/verifying-providers.md
- Run API: `dotnet run --project src/ButlerDidIt.Api` (http://localhost:5080)
- Front end: `cd src/web && npm run dev` (http://localhost:5173, proxies to :5080); `npx tsc -b`, `npm run lint`, `npm run build` (builds into src/ButlerDidIt.Api/wwwroot)
- E2E: build the front end first, then `cd tests/e2e && npx playwright test`
- New migration: `dotnet ef migrations add <Name> --project src/ButlerDidIt.Api --output-dir Data/Migrations`

## Conventions

- Game rules live only in each game's pure engine project (mysteries: `src/ButlerDidIt.Game`, no I/O). The API calls `GameEngine.Apply` via `PartyService.ExecuteAsync`. Platform code (joining, seats, selfies, the ticker) goes through `PartyRuntime` and `GameSession`, never a specific engine; see docs/architecture.md section 14.
- AI code lives in `src/ButlerDidIt.Ai` and goes through `AiGateway` (budget + usage logging). The engine never calls AI; AI actions are Begin/Complete/Cancel commands. Prompts must include only what that character or player may know. Every prompt starts with a `TASK:` line, which `FakeChatClient` keys on for tests.
- Track bugs, tech debt and follow-ups as GitHub issues (labels: `bug`, `enhancement`, `documentation`, `phase-N`, `ai`, `media`) and reference them in PRs.
- Never send scenario data to browsers directly: add fields to `Views.cs` and copy them explicitly in `ViewProjector`. Extend `ViewProjectorTests` for anything private.
- C# view records and `src/web/src/lib/types.ts` must stay in sync (camelCase, enums as camelCase strings).
- Scenario JSON in `content/themes/` is validated at startup and in tests by `ScenarioValidator`; escape rooms in `content/escape/` by `EscapeRoomValidator` (which also proves each room can be escaped). Escape views live in `ButlerDidIt.Escape/Engine/EscapeViews.cs` and are mirrored in `types.ts`; extend `PrivacyTests` for anything private.
