# Setting up the AI game master

AI is optional. Without it the game plays exactly like Phase 1. With it you get:

| Feature | Uses the role | Where players see it |
|---|---|---|
| **Generate a mystery**: a brand-new mystery for any theme, player count, content level and twist | Storyteller | *Host a new party → ✨ Write a brand-new mystery with AI* |
| **Solvability check**: every generated mystery must be solvable from its clues | Inspector | Automatic |
| **Question the NPCs**: guests question characters nobody is playing | Actor | Phone: **Question** tab. Stage: *The interrogation room* (answers read aloud) |
| **Hints**: one private nudge per player per act | Inspector | Phone: **Clues** tab → *Ask the Inspector* |
| **Verdicts**: a witty comment on each guest's accusation | Inspector | Stage, when the killer is unmasked |
| **Voices**: narration, NPC lines and NPC answers read by a real voice | Voice | Stage, during scenes and in the interrogation room |
| **Pictures**: character portraits, the victim and the setting | Illustrator | Stage, phones and the printable kit |

Voices and pictures are made **once per mystery** in the background when a host creates a party, then reused by every party that plays the same mystery. The host sees the progress in the lobby (*Preparing… 12 of 40 done*), and the party is playable straight away: until a clip or picture is ready, the browser's own speech and the candlelit placeholders fill in.

## 1. Choose providers

The game works with any mix of providers.

| Provider | Type | API key | Notes |
|---|---|---|---|
| Anthropic | `Anthropic` | Yes | Claude models, e.g. `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`. Prices are pre-filled. |
| OpenAI | `OpenAI` | Yes | ChatGPT models. Also any **OpenAI-compatible** server (LM Studio, OpenRouter, vLLM…) by setting the base URL. |
| Google | `Gemini` | Yes | Gemini models, through Google's OpenAI-compatible endpoint. |
| Ollama | `Ollama` | No | Runs open models on your own hardware for free. Set the base URL, e.g. `http://ollama:11434`. |

**Voices and pictures need OpenAI** for now: the Voice role uses OpenAI text-to-speech (`tts-1`, or `tts-1-hd` for higher quality) and the Illustrator role uses `dall-e-3`. You can still use Claude, Gemini or Ollama for the text roles and add OpenAI just for media.

**A sensible starting point:** a strong model as the **Storyteller**, since it runs once per mystery and quality matters most. A faster, cheaper model as the **Actor**, since it runs every time a guest asks a question. The Inspector sits in between.

Small local models (Ollama) are fine for the Actor. They often struggle to write a whole valid mystery, so the generator may fail more often; it retries and reports clearly when it gives up.

## 2. Configure it

### Option A: the admin page (easiest)
1. Sign in with the **first account created on the server**, which is the admin.
2. Open **AI settings** from the home page (`/admin/ai`).
3. **Providers → Add provider.** Pick the type and paste the API key. Keys are encrypted before they are stored and are never shown again.
4. **Test connection** with a model name to confirm the key works.
5. **Who does what:** pick a provider and model for Storyteller, Actor and Inspector, then Save. Optionally pick an OpenAI provider for Voice (`tts-1`) and Illustrator (`dall-e-3`).
6. **Prices:** add prices for any non-Claude models (US$ per million tokens, from the provider's pricing page) so costs are tracked correctly.
   - **Voices** are charged per character, which the usage log records as input tokens. For `tts-1` enter the price per million characters in *In $/1M* (check OpenAI's pricing page).
   - **Pictures** are charged per image: enter it in *Per call $* (for example the price of one `dall-e-3` 1024×1792 image).

### Option B: environment variables (good for Coolify)
Set these in Coolify's *Environment Variables* tab (see `.env.example`):

```
AI_PROVIDER_NAME=Claude
AI_PROVIDER_KIND=Anthropic
AI_PROVIDER_API_KEY=sk-ant-...
AI_STORYTELLER_MODEL=claude-opus-5
AI_ACTOR_MODEL=claude-sonnet-5
AI_INSPECTOR_MODEL=claude-sonnet-5
AI_MONTHLY_BUDGET_USD=25

# Optional: voices and pictures (OpenAI only for now)
AI_MEDIA_PROVIDER_NAME=OpenAI
AI_MEDIA_API_KEY=sk-...
AI_VOICE_MODEL=tts-1
AI_IMAGE_MODEL=dall-e-3
```

Environment settings are applied at every start-up and overwrite the same-named provider and role on the admin page. For several providers, configure them on the admin page, or use the full form `Ai__Providers__1__Name=…`, `Ai__Providers__1__Kind=…` and so on.

## 3. Costs and budgets

- Every AI call is logged with its tokens, estimated cost, duration and which host it was for. See **Usage and cost** on the admin page.
- Each host has a **monthly budget** (`AI_MONTHLY_BUDGET_USD`, default $25; `0` means unlimited). Once it's used up, AI features politely switch off until next month and the game carries on without them.
- Costs are estimates from the price list. A model with no price shows as $0 and is flagged on the admin page.
- Voices and pictures are cached by their exact text and model, so a mystery's media is paid for once, however many parties play it. A mystery like Blackwood Manor needs about 50 voice clips and pictures in total.
- Rough guide with Claude: generating a mystery costs well under a couple of dollars; each NPC answer or hint costs a fraction of a cent to a few cents, depending on the model.
- **Remixes** ("let the AI write one" on the new-party page) run only when no unplayed version of the story has a guest as the killer. One costs less than a new mystery, because the Storyteller writes a patch (the new solution, the changed character sheets and clues), not a whole story. It takes about a minute while the lobby waits. The host can press **Start without it** at any time, and if it fails, the evening starts with a hand-written version instead.

## 4. Privacy and safety

- **NPC prompts** contain only what that character knows: their own sheet, public facts, and clues already found. An innocent NPC has never been told the solution, so it can't leak it.
- **Hints** are built from the asking player's own view. If a reply names the killer anyway, it is thrown away and replaced.
- **Verdicts** mention the solution, so they only appear after the unmasking.
- **Remixes** send the whole story, including its solution, to the Storyteller, just like generating a mystery. The result must keep everything guests have already seen (bios, costumes, setting, prologue) and pass the validator and the blind solve before it's used.
- **Content level and tone:** every prompt includes the mystery's rating (Family or Mature), which sets the limits, and the tone the host picked when creating the party, which flavours the AI within them: *Mature* or *Normal* (PG-13, for mixed company) for Adults mysteries, *Normal* or *Funny* (silly, for kids) for Family ones. The tone only changes what the AI says; the written script is the same.
- **Costume selfies** never go to an AI provider. They are shrunk, stripped of metadata (including GPS location) and stored on your server.
- **What's sent:** player names and questions go to the AI provider you configured. If that matters to your guests, choose a provider whose data policy you're comfortable with, or run Ollama locally.

## 5. Troubleshooting

| Symptom | Fix |
|---|---|
| "No AI is set up for the … role" | Assign a provider and model to that role on the admin page. |
| "A stored AI API key can no longer be decrypted" | The Data Protection keys changed (for example, the `keys` volume was lost). Re-enter the key. |
| Test connection fails with 401/403 | Wrong or revoked API key. |
| Test connection fails with 404 / "model not found" | Check the model name on the provider's model list. |
| Generation fails repeatedly | Try a stronger Storyteller model; small models often produce invalid mysteries. |
| "The Voice role needs an OpenAI provider" (or Illustrator) | Those roles only work with OpenAI for now. Add an OpenAI provider for them. |
| Lobby says some items "couldn't be made" | Usually a picture refused by the provider's safety filter. Press *Fill in anything missing* to retry; the rest of the mystery is unaffected. |
