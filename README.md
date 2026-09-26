# The Butler Did It

An interactive murder-mystery party game for the web. Every guest plays a suspect with secrets to keep. The evening runs on two kinds of screens:

- **The stage**: a TV, a laptop, or a shared screen on a video call. It plays the story (narration, scene art, timers, clue reveals, the final unmasking) and shows only public information.
- **The dossier**: each guest's phone. It holds their character, secrets, private clues, notes and accusation.

Play it around the dinner table, over Zoom/Meet/Teams (share the stage tab with audio), or pass a single device around with press-and-hold private hand-offs. Guests who don't turn up are replaced by NPCs voiced by the narrator.

Mysteries come in two catalogs: **Adults** (mature, not explicit) and **Family** (for all ages). The hand-written ones:
- **Death at Blackwood Manor** (Adults): a 1920s country house, a séance and a new will.
- **Death Among the Vines** (Adults): a Tuscan wedding where the groom's father doesn't survive the rehearsal-dinner toast.
- **The Captain's Last Cocoa** (Family): pirates, buried treasure and a very suspicious mug of cocoa.

All three play with 3–8 guests in about two hours, and **each comes in three versions**: the same place and suspects, but a different killer, motive and clues, like dealing a new game of Clue. With "Surprise me" the version is dealt when the evening begins: one the host hasn't played, whose killer is one of the guests whenever possible (or, if none fits, one the AI writes for tonight's cast), so even the host can play along. With the optional **AI game master**, which works with Claude, ChatGPT, Gemini or local Ollama models, hosts can:
- generate new mysteries for any of the eight themes, family-friendly or mature
- let guests question characters nobody is playing
- get private hints from the Inspector
- hear a personalised verdict at the reveal
- hear real voices for the narrator and NPCs, and see generated portraits and scene art

Hosts can also print a party kit (invitations with QR codes, name tags, character booklets, clue cards), guests can upload costume selfies, and adult parties can switch on toast prompts with themed cocktails and mocktails.

See [docs/ai-setup.md](docs/ai-setup.md).

## Tech stack

| Part | Technology |
|---|---|
| Game rules | C# class library (`ButlerDidIt.Game`): pure functions, no web or database code |
| AI | `ButlerDidIt.Ai` on Microsoft.Extensions.AI `IChatClient`: Anthropic, OpenAI, Gemini, Ollama; OpenAI for voices and pictures |
| Media | QuestPDF + QRCoder (printable kit), SkiaSharp (selfies) |
| Server | ASP.NET Core 10, SignalR (real-time), EF Core + PostgreSQL, ASP.NET Core Identity |
| Front end | React 19 + TypeScript, Vite, Tailwind CSS |
| Tests | xUnit (engine + API against real Postgres), Playwright (full parties in real browsers) |
| Deployment | One Docker image + Postgres via `docker-compose.yml`, deployed with Coolify |

## Run it locally

You need the .NET 10 SDK, Node 22 and PostgreSQL 16.

```bash
# 1. A database user matching src/ButlerDidIt.Api/appsettings.json
sudo -u postgres psql -c "CREATE ROLE butler LOGIN SUPERUSER PASSWORD 'butler';"

# 2. The API (creates the database and loads /content on first start)
dotnet run --project src/ButlerDidIt.Api          # http://localhost:5080

# 3. The front end, with hot reload (proxies API calls to :5080)
cd src/web && npm install && npm run dev           # http://localhost:5173
```

Open http://localhost:5173, create a host account, create a party, then join from your phone (on the same network, use `npm run dev -- --host` and your computer's IP address) or from a few private browser windows.

Or run the production image with Docker:

```bash
cp .env.example .env    # set POSTGRES_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build   # http://localhost:8080
```

## Tests

```bash
dotnet test                                        # engine + API integration tests (needs Postgres)
cd src/web && npm run build                        # the e2e tests use the built front end
cd tests/e2e && npm install && npx playwright test # two complete parties in real browsers
```

## Project layout

```
src/ButlerDidIt.Game/   rules engine, scenario model, validator (no dependencies)
src/ButlerDidIt.Ai/     AI providers, prompts, mystery generator (no web or database code)
src/ButlerDidIt.Api/    ASP.NET Core host: REST endpoints, SignalR hub, database, auth
src/web/                React front end (builds into the API's wwwroot)
content/themes/         themes, mysteries (JSON) and media
tests/                  xUnit tests and Playwright end-to-end tests
docs/                   architecture, deployment and scenario-writing guides
```

## Documentation

- [Architecture: how it works and why](docs/architecture.md)
- [Deploying on Coolify](docs/deploy-coolify.md)
- [Writing a mystery](docs/writing-scenarios.md)
- [Setting up the AI game master](docs/ai-setup.md)

## Roadmap

The roadmap, feature requests and known issues are tracked in [GitHub Issues](https://github.com/jjsolis3/TheButlerDidIt/issues), with one roadmap issue per phase.

1. **Playable core**: done. Parties, join codes, stage + dossiers, pass-and-play, one hand-written mystery.
2. **AI game master** (#2): done. Multi-provider AI, generated mysteries, NPC questioning, hints, verdicts, cost controls.
3. **Media pipeline** (#8): character voices, generated portraits and scene art, printable party kits, costume selfies, toast prompts.
4. **Polish and scale** (#14): recap page, S3 storage, multi-instance scaling, scenario editor, more themes.
