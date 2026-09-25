import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import { useThemes } from '../lib/theme'
import type { ContentRating, PartyMode } from '../lib/types'
import { useMe } from '../lib/useMe'

const MODES: { id: PartyMode; title: string; body: string }[] = [
  { id: 'sharedScreen', title: 'Dinner party', body: 'Put the stage on a TV or laptop. Guests use their own phones for secrets and clues.' },
  { id: 'remote', title: 'Video call', body: 'Screen-share the stage on Zoom, Meet or Teams (share tab audio). Guests join on their own devices.' },
  { id: 'passAndPlay', title: 'Pass & play', body: 'One device for everyone. Each guest takes a turn to read their private dossier.' },
]

export default function NewParty() {
  const { me } = useMe()
  const { themes } = useThemes()
  const navigate = useNavigate()
  const [scenarioId, setScenarioId] = useState<string>()
  const [mode, setMode] = useState<PartyMode>('sharedScreen')
  const [content, setContent] = useState<ContentRating>('mature')
  const [when, setWhen] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (me === null) navigate('/login')
  }, [me, navigate])

  const playable = useMemo(() => themes?.flatMap((t) => t.scenarios.map((s) => ({ theme: t.theme, scenario: s }))) ?? [], [themes])
  useEffect(() => {
    if (!scenarioId && playable.length > 0) setScenarioId(playable[0].scenario.id)
  }, [playable, scenarioId])

  const selected = playable.find((p) => p.scenario.id === scenarioId)
  const tooMature = selected?.scenario.contentRating === 'mature' && content === 'family'

  const create = async () => {
    if (!scenarioId) return
    setBusy(true)
    setError(null)
    try {
      const party = await api.createParty(scenarioId, mode, content, when ? new Date(when).toISOString() : null)
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
              {scenario.minPlayers}–{scenario.maxPlayers} players · about {Math.round(scenario.estimatedMinutes / 60)} hours ·{' '}
              {scenario.contentRating === 'mature' ? 'Mature themes' : 'Family friendly'}
            </p>
          </button>
        ))}
        {themes && playable.length === 0 && <p className="text-muted">No mysteries are installed yet.</p>}
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

      <div className="mt-8 space-y-3">
        <ErrorText>{error}</ErrorText>
        <Button onClick={create} disabled={!scenarioId || busy || tooMature} className="w-full text-base">
          Create party and get the invite code
        </Button>
      </div>
    </Shell>
  )
}
