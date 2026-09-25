# The Butler Did It

Murder-mystery party game: ASP.NET Core 10 + SignalR + EF Core/PostgreSQL back end, React + TypeScript (Vite, Tailwind) front end. See docs/architecture.md.

## Commands

- Back-end tests: `dotnet test` (needs Postgres; `TEST_DATABASE_URL` overrides the default `Host=localhost;Username=butler;Password=butler`)
- Run API: `dotnet run --project src/ButlerDidIt.Api` (http://localhost:5080)
- Front end: `cd src/web && npm run dev` (http://localhost:5173, proxies to :5080); `npx tsc -b`, `npm run lint`, `npm run build` (builds into src/ButlerDidIt.Api/wwwroot)
- E2E: build the front end first, then `cd tests/e2e && npx playwright test`
- New migration: `dotnet ef migrations add <Name> --project src/ButlerDidIt.Api --output-dir Data/Migrations`

## Conventions

- Game rules live only in `src/ButlerDidIt.Game` (pure, no I/O). The API calls `GameEngine.Apply` via `PartyService.ExecuteAsync`.
- AI code lives in `src/ButlerDidIt.Ai` and goes through `AiGateway` (budget + usage logging). The engine never calls AI; AI actions are Begin/Complete/Cancel commands. Prompts must include only what that character or player may know. Every prompt starts with a `TASK:` line, which `FakeChatClient` keys on for tests.
- Track bugs, tech debt and follow-ups as GitHub issues (labels: `bug`, `enhancement`, `documentation`, `phase-N`, `ai`, `media`) and reference them in PRs.
- Never send scenario data to browsers directly: add fields to `Views.cs` and copy them explicitly in `ViewProjector`. Extend `ViewProjectorTests` for anything private.
- C# view records and `src/web/src/lib/types.ts` must stay in sync (camelCase, enums as camelCase strings).
- Scenario JSON in `content/` is validated at startup and in tests by `ScenarioValidator`.
