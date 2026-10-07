# Verifying the real AI providers

Everything AI in The Butler Did It is tested with the **Fake** provider, which gives canned answers, silent voices and gradient pictures. That proves the game logic, but not that each vendor's API accepts our requests. The live check suite does that part: it calls the real services with your keys and reports what came back. It covers the checklists in #25 (chat providers), #32 (OpenAI voices and images), #63 (Claude's effort and refusal fallback) and #33 (ElevenLabs, Piper and Stable Diffusion).

The suite lives in `tests/ButlerDidIt.Ai.LiveTests`. Every check is **skipped** unless its key is set, so CI and a plain `dotnet test` never call a paid API.

## 1. Set the keys

Set only the ones you want to check, as environment variables. In a Claude Code cloud environment, add them in the environment's settings (the environment menu in the session's title bar, then **Edit**). A new session picks them up. Never paste a key into a chat or commit it.

| Variable | Checks |
|---|---|
| `LIVE_ANTHROPIC_KEY` | Claude: chat, JSON, streaming, every effort level, the refusal-fallback path |
| `LIVE_OPENAI_KEY` | OpenAI: chat, JSON, streaming, voices (`tts-1`), portrait and landscape pictures |
| `LIVE_GEMINI_KEY` | Gemini: chat, JSON, streaming, voices, pictures |
| `LIVE_OLLAMA_URL` | A local Ollama server, e.g. `http://localhost:11434`: chat, JSON, streaming |
| `LIVE_ELEVENLABS_KEY` | ElevenLabs: a voice clip, cast from your account's voices |
| `LIVE_PIPER_URL` | A local Piper server, e.g. `http://localhost:5000`: a voice clip |
| `LIVE_SD_URL` | A local Stable Diffusion WebUI started with `--api`, e.g. `http://localhost:7860`: portrait and landscape pictures |
| `LIVE_FULL=1` | Also write a whole escape room and a whole mystery with each provider set above |

Each check uses a sensible default model. To test the model you actually run, override it: `LIVE_ANTHROPIC_MODEL`, `LIVE_ANTHROPIC_FALLBACK_MODEL`, `LIVE_OPENAI_MODEL`, `LIVE_OPENAI_TTS_MODEL`, `LIVE_OPENAI_IMAGE_MODEL`, `LIVE_GEMINI_MODEL`, `LIVE_GEMINI_TTS_MODEL`, `LIVE_GEMINI_IMAGE_MODEL`, `LIVE_OLLAMA_MODEL`, `LIVE_ELEVENLABS_MODEL` (default `eleven_multilingual_v2`), `LIVE_PIPER_VOICE` and `LIVE_SD_MODEL` (both `default`: the voice or checkpoint the server already has).

**Network access:** the machine running the checks has to reach the vendor. In a Claude Code cloud environment, api.anthropic.com and generativelanguage.googleapis.com are usually allowed; **api.openai.com and api.elevenlabs.io may need adding** to the environment's allowed domains. Piper and Stable Diffusion have to be reachable from the machine running the checks, so run those on your own machine or network.

## 2. Run them

```bash
dotnet test tests/ButlerDidIt.Ai.LiveTests --logger "console;verbosity=detailed"
```

Each check prints one table row: provider, check, model, what came back, tokens and time. It also appends the row to `live-report.md` next to the test binaries, or wherever `LIVE_REPORT` points. Paste that table into the issue you're closing.

## What it costs

- **Without `LIVE_FULL`:** a few cents per provider. Short chat replies, one voice clip, and two pictures for OpenAI and Gemini (pictures are the biggest part, roughly $0.04–$0.20 each). ElevenLabs uses about 50 characters of your plan's credit; Piper and Stable Diffusion are free.
- **With `LIVE_FULL`:** roughly $0.50–$2 more per provider, depending on the model. A mystery is several long calls, and an escape room is one or two long calls plus a tester.

The suite has no budget cap of its own, so keep `LIVE_FULL` for when you mean it.

## What each issue still needs by hand

- **#25:** the **Test connection** button on Admin → AI, for each provider you set up there. The live suite covers the rest.
- **#32:**
  - a safety refusal on a mature scene is counted as "couldn't be made" without stopping the job, and "Fill in anything missing" retries it;
  - the cost log on Admin → AI matches the vendor's bill;
  - a full Blackwood Manor preparation stays within budget and time.

  Check these on a real party's media preparation.
- **#63:** compare the `effort low` and `effort default` rows' times in the report, to decide whether the Actor role should default to low effort.
- **#33:** on Admin → AI, **Test connection** for each new provider, then play a scene with its voices or pictures: with ElevenLabs or a many-speaker Piper voice, the narrator and each character should sound different.
