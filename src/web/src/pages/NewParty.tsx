import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import { useThemes } from '../lib/theme'
import type { AiStatus, ContentRating, GenerationJob, MysteryLength, PartyMode, ThemeCard } from '../lib/types'
import { useMe } from '../lib/useMe'

const MODES: { id: PartyMode; title: string; body: string }[] = [
  { id: 'sharedScreen', title: 'Dinner party', body: 'Put the stage on a TV or laptop. Guests use their own phones for secrets and clues.' },
  { id: 'remote', title: 'Video call', body: 'Screen-share the stage on Zoom, Meet or Teams (share tab audio). Guests join on their own devices.' },
  { id: 'passAndPlay', title: 'Pass & play', body: 'One device for everyone. Each guest takes a turn to read their private dossier.' },
]

export default function NewParty() {
  const { me } = useMe()
  const { themes, reload: reloadThemes } = useThemes()
  const [ai, setAi] = useState<AiStatus | null>(null)
  const [useAi, setUseAi] = useState(true)
  const [drinking, setDrinking] = useState(false)
  const navigate = useNavigate()
  const [chosenId, setScenarioId] = useState<string>()
  const [mode, setMode] = useState<PartyMode>('sharedScreen')
  const [content, setContent] = useState<ContentRating>('mature')
  const [when, setWhen] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (me === null) navigate('/login')
    if (me) api.aiStatus().then(setAi, () => setAi(null))
  }, [me, navigate])

  const playable = useMemo(() => themes?.flatMap((t) => t.scenarios.map((s) => ({ theme: t.theme, scenario: s }))) ?? [], [themes])
  // Until the host picks one, the first mystery is selected.
  const scenarioId = chosenId ?? playable[0]?.scenario.id

  const selected = playable.find((p) => p.scenario.id === scenarioId)
  const tooMature = selected?.scenario.contentRating === 'mature' && content === 'family'

  const create = async () => {
    if (!scenarioId) return
    setBusy(true)
    setError(null)
    try {
      const party = await api.createParty(scenarioId, mode, content, when ? new Date(when).toISOString() : null, useAi, drinking && content !== 'family')
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

      <section className="space-y-3">
        <h2 className="font-display text-xl">1. Choose a mystery</h2>
        {playable.map(({ theme, scenario }) => (
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
            </p>
          </button>
        ))}
        {themes && playable.length === 0 && <p className="text-muted">No mysteries are installed yet.</p>}
        {ai?.storyteller && themes && (
          <GenerateMystery
            themes={themes}
            content={content}
            onReady={async (job) => {
              const fresh = await reloadThemes()
              const found = fresh.flatMap((t) => t.scenarios).find((x) => x.id === job.scenarioId)
              if (found) {
                setScenarioId(found.id)
                setContent(found.contentRating)
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

      <section className="mt-8 space-y-3">
        <h2 className="font-display text-xl">3. Content level</h2>
        <div className="grid grid-cols-2 gap-3">
          {(['mature', 'family'] as const).map((c) => (
            <button
              key={c}
              onClick={() => setContent(c)}
              className={`rounded-xl border p-4 text-left transition ${content === c ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}
            >
              <p className="font-semibold">{c === 'mature' ? 'Mature' : 'Family'}</p>
              <p className="mt-1 text-xs text-muted">
                {c === 'mature' ? 'Affairs, scandal, dark humour, described violence. Nothing explicit.' : 'Suitable for teenagers and mixed company.'}
              </p>
            </button>
          ))}
        </div>
        {tooMature && <p className="text-sm text-red-200">This mystery is rated Mature. Family-friendly mysteries arrive with the AI storyteller.</p>}
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
      {content !== 'family' && (
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

      {ai && (ai.actor || ai.inspector) && (
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
        </section>
      )}

      <div className="mt-8 space-y-3">
        <ErrorText>{error}</ErrorText>
        <Button onClick={create} disabled={!scenarioId || busy || tooMature} className="w-full text-base">
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
