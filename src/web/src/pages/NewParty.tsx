import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import { ConfirmEmailBanner } from './Account'
import { useThemes } from '../lib/theme'
import type { AiStatus, AuthOptions, ContentRating, GenerationJob, MysteryLength, PartyMode, ThemeCard, Tone } from '../lib/types'
import { useMe } from '../lib/useMe'

const MODES: { id: PartyMode; title: string; body: string }[] = [
  { id: 'sharedScreen', title: 'Dinner party', body: 'Put the stage on a TV or laptop. Guests use their own phones for secrets and clues.' },
  { id: 'remote', title: 'Video call', body: 'Screen-share the stage on Zoom, Meet or Teams (share tab audio). Guests join on their own devices.' },
  { id: 'passAndPlay', title: 'Pass & play', body: 'One device for everyone. Each guest takes a turn to read their private dossier.' },
]

/**
 * The tones on offer depend on the shelf. The mystery's rating sets the limits (a Family story
 * is always Family); the tone only changes how the AI game master speaks within them.
 */
const TONES: Record<ContentRating, { id: Tone; title: string; body: string }[]> = {
  mature: [
    { id: 'standard', title: '🍷 Mature', body: 'The full adult experience: scandal, dark humour, the odd risqué remark.' },
    { id: 'clean', title: '👔 Normal', body: 'For mixed company, like work friends or the in-laws: scandal yes, crude no.' },
  ],
  family: [
    { id: 'standard', title: '🧸 Normal', body: 'A proper mystery for all ages: spooky, never scary.' },
    { id: 'playful', title: '😂 Funny', body: 'Silly and over the top: puns, big reactions and jokes for kids.' },
  ],
}

export default function NewParty() {
  const { me } = useMe()
  const { themes, reload: reloadThemes } = useThemes()
  const [ai, setAi] = useState<AiStatus | null>(null)
  const [authOptions, setAuthOptions] = useState<AuthOptions | null>(null)
  const [useAi, setUseAi] = useState(true)
  const [drinking, setDrinking] = useState(false)
  const navigate = useNavigate()
  const [chosenId, setScenarioId] = useState<string>()
  // Which version of the chosen story to play. "surprise" by default, so even the host doesn't know the killer.
  const [version, setVersion] = useState('surprise')
  // If no version's killer is one of tonight's guests, the AI may write one (only asked when a Storyteller is set up).
  const [tailor, setTailor] = useState(true)
  const [mode, setMode] = useState<PartyMode>('sharedScreen')
  // Two catalogs: Adults (Mature) and Family. Adults first, so Blackwood Manor stays the default.
  // The shelf is also the party's content level: the server takes it from the mystery itself.
  const [shelf, setShelf] = useState<ContentRating>('mature')
  const [chosenTone, setTone] = useState<Tone>('standard')
  const [when, setWhen] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (me === null) navigate('/login')
    if (me) api.aiStatus().then(setAi, () => setAi(null))
    if (me) api.authOptions().then(setAuthOptions, () => setAuthOptions(null))
  }, [me, navigate])

  const playable = useMemo(() => themes?.flatMap((t) => t.scenarios.map((s) => ({ theme: t.theme, scenario: s }))) ?? [], [themes])
  const onShelf = playable.filter((p) => p.scenario.contentRating === shelf)
  // The host's pick if it's on this shelf, otherwise the shelf's first mystery. Worked out
  // during render, so switching shelves can never leave a mystery from the other shelf selected.
  const scenarioId = onShelf.some((p) => p.scenario.id === chosenId) ? chosenId : onShelf[0]?.scenario.id

  // Like the selected mystery, the tone is checked during render: "Funny" picked on the Family shelf
  // falls back to "Normal" if the host then switches to Adults, which has no "Funny".
  const tone = TONES[shelf].some((t) => t.id === chosenTone) ? chosenTone : 'standard'

  const selected = playable.find((p) => p.scenario.id === scenarioId)
  const aiAvailable = !!ai && (ai.actor || ai.inspector)

  const create = async () => {
    if (!scenarioId) return
    setBusy(true)
    setError(null)
    try {
      const versions = selected?.scenario.versions ?? []
      const chosenVersion = versions.length === 0 ? null : version === 'surprise' || versions.some((v) => v.id === version) ? version : 'surprise'
      const party = await api.createParty(scenarioId, mode, when ? new Date(when).toISOString() : null, {
        useAi,
        drinkingPrompts: drinking && shelf === 'mature',
        version: chosenVersion,
        tone,
        tailorWithAi: tailor && chosenVersion === 'surprise' && !!ai?.storyteller,
      })
      navigate(`/stage/${party.code}`)
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Eyebrow>New party</Eyebrow>
      <Heading className="mt-2 mb-6">Set the scene</Heading>
      <ConfirmEmailBanner required={!!authOptions?.requireConfirmedEmail && me?.emailConfirmed === false} />

      <section className="space-y-3">
        <h2 className="font-display text-xl">1. Choose a mystery</h2>
        <div className="grid grid-cols-2 gap-2 rounded-xl border border-line bg-surface p-1" role="tablist" aria-label="Catalog">
          {(['mature', 'family'] as const).map((s) => {
            const count = playable.filter((p) => p.scenario.contentRating === s).length
            return (
              <button
                key={s}
                role="tab"
                aria-selected={shelf === s}
                onClick={() => setShelf(s)}
                className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${shelf === s ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
              >
                {s === 'mature' ? '🍷 Adults' : '🧸 Family'} <span className="font-normal opacity-80">({count})</span>
              </button>
            )
          })}
        </div>
        <p className="text-xs text-muted">
          {shelf === 'mature'
            ? 'For grown-ups: affairs, scandal, dark humour and drinking-game toasts. Nothing explicit.'
            : 'For all ages: no gore, no alcohol, no swearing. Great with kids and teens.'}
        </p>
        {themes && onShelf.length === 0 && (
          <p className="text-sm text-muted">
            No {shelf === 'family' ? 'Family' : 'Adult'} mysteries yet.{ai?.storyteller ? ' Write one with AI below.' : ''}
          </p>
        )}
        {onShelf.map(({ theme, scenario }) => (
          <button
            key={scenario.id}
            onClick={() => setScenarioId(scenario.id)}
            className={`w-full rounded-xl border p-4 text-left transition ${scenarioId === scenario.id ? 'border-accent bg-accent/10' : 'border-line bg-surface hover:border-accent/60'}`}
          >
            <p className="text-xs tracking-widest text-accent uppercase">{theme.name}</p>
            <p className="font-display mt-1 text-xl">{scenario.title}</p>
            <p className="mt-2 line-clamp-3 text-sm text-muted">{scenario.synopsis}</p>
            <p className="mt-2 text-xs text-muted">
              {scenario.minPlayers}–{scenario.maxPlayers} players · about{' '}
              {scenario.estimatedMinutes < 90 ? `${scenario.estimatedMinutes} minutes` : `${Math.round(scenario.estimatedMinutes / 60)} hours`} ·{' '}
              {scenario.contentRating === 'mature' ? 'Mature themes' : 'Family friendly'}
              {scenario.aiGenerated && ' · ✨ written by AI for you'}
              {scenario.custom && ' · ✏️ your edited copy'}
              {scenario.versions.length > 1 && ` · 🎲 ${scenario.versions.length} versions, a different killer each`}
            </p>
          </button>
        ))}
        {selected && selected.scenario.versions.length > 1 && (
          <div className="rounded-xl border border-line bg-surface p-4">
            <span className="text-xs font-semibold tracking-widest text-accent uppercase">Version</span>
            <select
              className={`${inputClass} mt-2`}
              aria-label="Version"
              value={selected.scenario.versions.some((v) => v.id === version) ? version : 'surprise'}
              onChange={(e) => setVersion(e.target.value)}
            >
              <option value="surprise">🎲 Surprise me: a version I haven't played</option>
              {selected.scenario.versions.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.label}
                  {v.playedByMe ? ' (played)' : ''}
                </option>
              ))}
            </select>
            <span className="mt-2 block text-xs text-muted">
              Same place and suspects, a different killer and new clues. With “Surprise me” even you won't know whodunit, so you can play along,
              and the version is dealt when you begin the evening, so that one of your guests is the killer whenever possible.
            </span>
            {ai?.storyteller && !selected.scenario.versions.some((v) => v.id === version) && (
              <label className="mt-3 flex items-start gap-3 text-sm">
                <input type="checkbox" className="mt-1 accent-[var(--theme-accent)]" checked={tailor} onChange={(e) => setTailor(e.target.checked)} />
                <span>
                  ✨ If no version fits tonight's cast, let the AI write one
                  <span className="block text-xs text-muted">
                    About a minute when you begin, from your AI budget. It keeps the same story and suspects, and makes one of your guests the killer.
                  </span>
                </span>
              </label>
            )}
          </div>
        )}
        {themes && playable.length === 0 && <p className="text-muted">No mysteries are installed yet.</p>}
        {ai?.storyteller && themes && (
          <GenerateMystery
            themes={themes}
            content={shelf}
            onReady={async (job) => {
              const fresh = await reloadThemes()
              const found = fresh.flatMap((t) => t.scenarios).find((x) => x.id === job.scenarioId)
              if (found) {
                setShelf(found.contentRating)
                setScenarioId(found.id)
              }
            }}
          />
        )}
        {ai && !ai.storyteller && ai.isAdmin && (
          <p className="text-sm text-muted">
            Want new mysteries for every theme? <a href="/admin/ai" className="text-accent underline">Set up an AI Storyteller</a>.
          </p>
        )}
      </section>

      <section className="mt-8 space-y-3">
        <h2 className="font-display text-xl">2. How are you playing?</h2>
        <div className="grid gap-3 sm:grid-cols-3">
          {MODES.map((m) => (
            <button
              key={m.id}
              onClick={() => setMode(m.id)}
              className={`rounded-xl border p-4 text-left transition ${mode === m.id ? 'border-accent bg-accent/10' : 'border-line bg-surface hover:border-accent/60'}`}
            >
              <p className="font-semibold">{m.title}</p>
              <p className="mt-1 text-xs text-muted">{m.body}</p>
            </button>
          ))}
        </div>
        <p className="text-xs text-muted">You can always mix: guests with phones and pass-and-play seats work in every mode.</p>
      </section>


      <section className="mt-8">
        <Card>
          <Field label="When is the party? (optional)" hint="Guests can join early to see their character and plan a costume.">
            <input type="datetime-local" className={inputClass} value={when} onChange={(e) => setWhen(e.target.value)} />
          </Field>
        </Card>
      </section>

      {/* Drinking games are an adults-only extra, so the option disappears for Family parties
          (the server also forces it off, in case an old browser tab still sends it). */}
      {shelf === 'mature' && (
        <section className="mt-8">
          <label className="flex items-start gap-3 rounded-xl border border-line bg-surface p-4">
            <input type="checkbox" className="mt-1 accent-[var(--theme-accent)]" checked={drinking} onChange={(e) => setDrinking(e.target.checked)} />
            <span>
              <span className="font-semibold">🥂 Toast prompts</span>
              <span className="block text-xs text-muted">
                The narrator calls for a toast at a few dramatic moments, with a non-alcoholic alternative always shown. The lobby also
                suggests cocktails and mocktails to match the evening.
              </span>
            </span>
          </label>
        </section>
      )}

      {aiAvailable && ai && (
        <section className="mt-8">
          <label className="flex items-start gap-3 rounded-xl border border-line bg-surface p-4">
            <input type="checkbox" className="mt-1 accent-[var(--theme-accent)]" checked={useAi} onChange={(e) => setUseAi(e.target.checked)} />
            <span>
              <span className="font-semibold">Use the AI game master</span>
              <span className="block text-xs text-muted">
                {[ai.actor && 'Guests can question characters nobody is playing', ai.inspector && 'hints when stuck and verdicts at the reveal']
                  .filter(Boolean)
                  .join('; ')
                  .replace(/^h/, 'H')}
                . This month's AI spend: ${ai.spentThisMonthUsd.toFixed(2)}
                {ai.budgetUsd > 0 ? ` of $${ai.budgetUsd.toFixed(2)}` : ''}.
              </span>
            </span>
          </label>
          {/* The tone only changes what the AI says, so it's offered only when the AI is on. */}
          {useAi && (
            <div className="mt-3 rounded-xl border border-line bg-surface p-4">
              <p className="text-xs font-semibold tracking-widest text-accent uppercase">Tone</p>
              <div className="mt-2 grid grid-cols-2 gap-3" role="radiogroup" aria-label="Tone">
                {TONES[shelf].map((t) => (
                  <button
                    key={t.id}
                    role="radio"
                    aria-checked={tone === t.id}
                    onClick={() => setTone(t.id)}
                    className={`rounded-xl border p-3 text-left transition ${tone === t.id ? 'border-accent bg-accent/10' : 'border-line bg-bg hover:border-accent/60'}`}
                  >
                    <p className="font-semibold">{t.title}</p>
                    <p className="mt-1 text-xs text-muted">{t.body}</p>
                  </button>
                ))}
              </div>
              <p className="mt-2 text-xs text-muted">How the AI's characters, hints and verdicts sound. The written story stays the same.</p>
            </div>
          )}
        </section>
      )}

      <div className="mt-8 space-y-3">
        <ErrorText>{error}</ErrorText>
        <Button onClick={create} disabled={!scenarioId || busy} className="w-full text-base">
          Create party and get the invite code
        </Button>
      </div>
    </Shell>
  )
}

/** "Write a new mystery with AI": starts a background job and follows its progress. */
function GenerateMystery({ themes, content, onReady }: { themes: ThemeCard[]; content: ContentRating; onReady: (job: GenerationJob) => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const [themeSlug, setThemeSlug] = useState(themes.find((t) => t.scenarios.length === 0)?.theme.slug ?? themes[0]?.theme.slug)
  const [players, setPlayers] = useState(6)
  const [rating, setRating] = useState<ContentRating>(content)
  const [length, setLength] = useState<MysteryLength>('standard')
  const [twist, setTwist] = useState('')
  const [job, setJob] = useState<GenerationJob | null>(null)
  const [error, setError] = useState<string | null>(null)
  const onReadyRef = useRef(onReady)
  useEffect(() => {
    onReadyRef.current = onReady
  }, [onReady])

  // Poll the job every 2 seconds until it finishes. The work happens on the server,
  // so closing this page doesn't stop it.
  useEffect(() => {
    if (!job || job.status === 'succeeded' || job.status === 'failed') return
    const id = setTimeout(async () => {
      try {
        const next = await api.generationJob(job.id)
        setJob(next)
        if (next.status === 'succeeded') await onReadyRef.current(next)
      } catch (e) {
        setError((e as Error).message)
      }
    }, 2000)
    return () => clearTimeout(id)
  }, [job])

  const start = async () => {
    if (!themeSlug) return
    setError(null)
    try {
      setJob(await api.generate(themeSlug, players, rating, length, twist))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const running = job && (job.status === 'queued' || job.status === 'running')

  if (!open) {
    return (
      <button onClick={() => setOpen(true)} className="w-full rounded-xl border border-dashed border-accent/60 p-4 text-left hover:bg-accent/5">
        <p className="font-display text-lg text-accent">✨ Write a brand-new mystery with AI</p>
        <p className="text-sm text-muted">Any theme, your group size, family-friendly or mature, with your own twist. Takes a minute or two.</p>
      </button>
    )
  }

  return (
    <Card className="border-accent/60">
      <p className="font-display mb-4 text-lg text-accent">✨ Write a brand-new mystery with AI</p>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Theme">
          <select className={inputClass} value={themeSlug} onChange={(e) => setThemeSlug(e.target.value)} disabled={!!running}>
            {themes.map((t) => (
              <option key={t.theme.slug} value={t.theme.slug}>
                {t.theme.name} ({t.theme.era})
              </option>
            ))}
          </select>
        </Field>
        <Field label={`Guests: ${players}`}>
          <input type="range" min={3} max={8} value={players} onChange={(e) => setPlayers(Number(e.target.value))} className="w-full accent-[var(--theme-accent)]" disabled={!!running} />
        </Field>
        <Field label="Content">
          <select className={inputClass} value={rating} onChange={(e) => setRating(e.target.value as ContentRating)} disabled={!!running}>
            <option value="mature">Mature (adults)</option>
            <option value="family">Family friendly</option>
          </select>
        </Field>
        <Field label="Length">
          <select className={inputClass} value={length} onChange={(e) => setLength(e.target.value as MysteryLength)} disabled={!!running}>
            <option value="short">Short: 2 acts, about an hour</option>
            <option value="standard">Standard: 3 acts, about two hours</option>
            <option value="long">Long: 4 acts</option>
          </select>
        </Field>
        <div className="sm:col-span-2">
          <Field label="Twist or special request (optional)" hint="e.g. “the victim is a magician”, “include a pair of twins”, “set it on New Year's Eve”">
            <input className={inputClass} value={twist} onChange={(e) => setTwist(e.target.value)} maxLength={300} disabled={!!running} />
          </Field>
        </div>
      </div>
      <div className="mt-4 space-y-2">
        {!running && job?.status !== 'succeeded' && <Button onClick={start}>Write my mystery</Button>}
        {running && (
          <p className="candle text-sm text-accent" role="status">
            {job.progress}
          </p>
        )}
        {job?.status === 'succeeded' && (
          <div className="rounded-lg border border-accent/50 bg-accent/10 p-3 text-sm">
            <p>{job.progress} It's selected above. Carry on setting up your party.</p>
            {job.warnings.map((w) => (
              <p key={w} className="mt-1 text-muted">
                ⚠ {w}
              </p>
            ))}
          </div>
        )}
        {job?.status === 'failed' && <ErrorText>{job.error}</ErrorText>}
        <ErrorText>{error}</ErrorText>
      </div>
    </Card>
  )
}
