# The Butler Did It

An interactive murder-mystery party game for the web. Every guest plays a suspect with secrets to keep. The evening runs on two kinds of screens:

- **The stage**: a TV, a laptop, or a shared screen on a video call. It plays the story (narration, scene art, timers, clue reveals, the final unmasking) and shows only public information.
- **The dossier**: each guest's phone. It holds their character, secrets, private clues, notes and accusation.

Play it around the dinner table, over Zoom/Meet/Teams (share the stage tab with audio), or pass a single device around with press-and-hold private hand-offs. Guests who don't turn up are replaced by NPCs voiced by the narrator.

Mysteries come in two catalogs: **Adults** (mature, not explicit) and **Family** (for all ages). The hand-written ones:
- **Death at Blackwood Manor** (Adults): a 1920s country house, a séance and a new will.
- **Death Among the Vines** (Adults): a Tuscan wedding where the groom's father doesn't survive the rehearsal-dinner toast.
- **The Captain's Last Cocoa** (Family): pirates, buried treasure and a very suspicious mug of cocoa.
- **The Ringmaster's Last Bow** (Adults): a 1950s travelling carnival, a magician who can vanish, a sad clown and a bearded lady, all with a grudge against the ringmaster.
- **Who Stopped the Circus?** (Family): the same carnival for all ages: a ringmaster who never wakes from his bedtime snack, and a missing Golden Ticket.
- **Who Crashed the Reunion?** (Family): the Class of '86 is back in the school gym, the principal never wakes up in time to open the time capsule, and the championship trophy has vanished.
- **Death at the Gin Joint** (Adults): a 1927 Chicago speakeasy, a torch singer, a crooked cop and a nervous bookkeeper, and a club owner who meant to settle every account before leaving town.
- **Last Stop: Murder** (Adults): a 1930s sleeper train snowbound on the way to Istanbul, a stolen emerald, and a millionaire found dead in a compartment chained from the inside.
- **Murder on the Red Carpet** (Adults): a 1950s Hollywood premiere, and a leading man who never makes his curtain call.
- 🎃 **Last Night at Camp Blackwater** (Adults, Halloween): a 1980s summer camp, a storm on the lake, and the legend of the Lakeside Man in his burlap mask.
- 🎃 **Who Spooked the Halloween Party?** (Family, Halloween): a village costume party at creaky Hollow Hill House, a pumpkin contest, and a missing Golden Pumpkin.
- 🎃 **Death in Room 13** (Adults, Halloween): a 1926 séance at a gothic grand hotel where no guest ever checks out, and thirteen minutes of total darkness.
- 🎃 **The Witching Hour** (Adults, Halloween): a New England harvest festival, a reenactment of a 1692 witch trial, and a curse three hundred years old.

**🔐 Escape rooms** are the second kind of game night. The group joins on their phones and races the clock through a series of rooms: codes, riddles, keys and tools, with clues split across everyone's phones so nobody can solve them alone. Hints help, but each one costs time. Every game shuffles the room's codes, riddles and passwords, so you can play it again and again. Race your best time, take on **today's challenge** (the same puzzles for everyone), or replay a friend's puzzle set. Pick a **30, 45 or 60-minute** game: shorter games play fewer puzzles, and each length has its own leaderboard. Every room is a scene to search, full of decoys, hidden clues and tools to put together, with ciphers, number patterns, logic puzzles and light panels to crack. Pick **Easy, Normal or Hard** too: Hard adds a puzzle, more decoys and a red herring, and each difficulty has its own leaderboard. The rooms so far:
- **The Workshop** (Adults, 🎃): you wake up chained in a basement workshop, and the Tinkerer wants to play a game.
- **The Asylum** (Adults, 🎃): you wake in Cell Block C of an asylum closed for fifty years, and the Night Warden is listening on the PA.
- **The Bunker** (Adults): a sealed Cold War fallout shelter, where the Overseer grades your "survival exercise" over the intercom.
- **The Funhouse After Dark** (Family, 🎃): the carnival has closed, and Mister Giggles the robot clown has locked every door.
- **The Tick-Tock Toy Factory** (Family): Sprocket, the grumpy wind-up foreman, won't let you leave until his factory is fixed.
- **The Wizard's Tower** (Family, 🎃): you read a spell out loud and every door locked itself. Professor Quill, an enchanted feather pen, helps in rhyme.
- **The Pirate Ship** (Family): stowaways aboard the Jolly Sardine must solve Captain Salty Sal's riddles before the tide turns.

With the optional AI, each room's villain becomes a live **game master**. It taunts and cheers the group out loud on the TV as they play, and writes hints for exactly where they're stuck. The app checks those hints, and they never give the answer away. The TV sets the mood too: a background soundscape for each room, a click and a chime when a lock opens, a heartbeat in the final minute, a picture of each room (painted once by the image model), and doors that swing open when you escape. It can also **write a whole new room from any theme** you type ("a haunted lighthouse"). Every code comes from the same proven templates, a tester AI has to crack its riddles, and the room must pass the same escapability check as the hand-written ones before it lands on your shelf.

A **🎃 Halloween** filter on each shelf shows just the spooky stories. It glows all through October. A theme joins a season by listing it in `"seasons"` in its `theme.json`.

All of them play with 3–8 guests in about two hours, and **each comes in three or four versions**: the same place and suspects, but a different killer, motive and clues, like dealing a new game of Clue. With "Surprise me" the version is dealt when the evening begins: one the host hasn't played, whose killer is one of the guests whenever possible (or, if none fits, one the AI writes for tonight's cast), so even the host can play along. With the optional **AI game master**, which works with Claude, ChatGPT, Gemini or local Ollama models, hosts can:
- generate new mysteries for any of the thirteen themes, family-friendly or mature
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
| Deployment | One Docker image + Postgres via `docker-compose.yml`, deployed with Coolify. Optional Redis and S3 for running several servers |

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
src/ButlerDidIt.Game/   murder-mystery rules engine, scenario model, validator (no dependencies)
src/ButlerDidIt.Escape/ escape-room rules engine, room model, solvability validator
src/ButlerDidIt.Ai/     AI providers, prompts, mystery generator (no web or database code)
src/ButlerDidIt.Api/    ASP.NET Core host: REST endpoints, SignalR hub, database, auth
src/web/                React front end (builds into the API's wwwroot)
content/themes/         themes, mysteries (JSON) and media
content/escape/         escape rooms (JSON)
tests/                  xUnit tests and Playwright end-to-end tests
docs/                   architecture, deployment and scenario-writing guides
```

## Documentation

- [Architecture: how it works and why](docs/architecture.md)
- [Deploying on Coolify](docs/deploy-coolify.md)
- [Writing a mystery](docs/writing-scenarios.md)
- [Writing an escape room](docs/writing-escape-rooms.md)
- [Setting up the AI game master](docs/ai-setup.md)

## Roadmap

The roadmap, feature requests and known issues are tracked in [GitHub Issues](https://github.com/jjsolis3/TheButlerDidIt/issues), with one roadmap issue per phase.

1. **Playable core**: done. Parties, join codes, stage + dossiers, pass-and-play, one hand-written mystery.
2. **AI game master** (#2): done. Multi-provider AI, generated mysteries, NPC questioning, hints, verdicts, cost controls.
3. **Media pipeline** (#8): character voices, generated portraits and scene art, printable party kits, costume selfies, toast prompts.
4. **Polish and scale** (#14): recap page, S3 storage, multi-instance scaling, scenario editor, more themes.
