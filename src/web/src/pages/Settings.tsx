import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { api } from '../lib/api'
import { ANSWER_RULES, DIFFICULTIES, ESCAPE_MODES, MODES, TONES } from '../lib/partyOptions'
import type { ContentRating, HostPreferences } from '../lib/types'
import { useMe } from '../lib/useMe'

const SHELVES: { id: ContentRating; title: string }[] = [
  { id: 'mature', title: '🍷 Adults' },
  { id: 'family', title: '🧸 Family' },
]

/** A row of choices, one of which is picked: a radio group drawn as chips. */
function Choice<T extends string | number | null>({
  label,
  value,
  options,
  onChange,
}: {
  label: string
  value: T
  options: { id: T; title: string }[]
  onChange: (v: T) => void
}) {
  return (
    <fieldset>
      <legend className="mb-2 text-sm font-semibold">{label}</legend>
      <div className="flex flex-wrap gap-2" role="radiogroup" aria-label={label}>
        {options.map((o) => (
          <button
            key={String(o.id)}
            type="button"
            role="radio"
            aria-checked={value === o.id}
            onClick={() => onChange(o.id)}
            className={`min-h-11 rounded-lg border px-3 py-2 text-sm ${value === o.id ? 'border-accent bg-accent/10 text-ink' : 'border-line bg-bg text-muted hover:border-accent/60'}`}
          >
            {o.title}
          </button>
        ))}
      </div>
    </fieldset>
  )
}

function Toggle({ label, hint, checked, onChange }: { label: string; hint?: ReactNode; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-start gap-3 text-sm">
      <input type="checkbox" className="mt-1 accent-[var(--theme-accent)]" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        <span className="font-semibold">{label}</span>
        {hint && <span className="block text-xs text-muted">{hint}</span>}
      </span>
    </label>
  )
}

/**
 * Party settings (#102): how a new party starts. The host page reads them, and its "Save these as my usual settings"
 * link writes the same ones, so a host who always plays Family escape rooms on Hard sets that once.
 */
export default function Settings() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [prefs, setPrefs] = useState<HostPreferences | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (me === null) navigate(`/login?next=${encodeURIComponent('/settings')}`)
    if (me) api.account.preferences().then(setPrefs, (e: Error) => setError(e.message))
  }, [me, navigate])

  if (!prefs)
    return (
      <Shell>
        <ErrorText>{error}</ErrorText>
        {!error && <p className="text-muted">Loading your settings…</p>}
      </Shell>
    )

  const change = (edit: (p: HostPreferences) => HostPreferences) => {
    setSaved(false)
    setPrefs((p) => (p ? edit(p) : p))
  }
  const mystery = prefs.mystery
  const escape = prefs.escape
  // The tones on offer depend on the shelf, as on the host page: switching shelves falls back to the standard tone.
  const tones = TONES[mystery.shelf]

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      setPrefs(await api.account.savePreferences(prefs))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Eyebrow>Your account</Eyebrow>
      <Heading className="mt-2 mb-2">Party settings</Heading>
      <p className="mb-6 text-muted">
        How a new party starts. You can still change anything when you{' '}
        <Link to="/host/new" className="underline hover:text-ink">
          host a party
        </Link>
        .
      </p>

      <div className="space-y-6">
        <Card className="space-y-5">
          <Choice
            label="The host page opens on"
            value={prefs.game}
            options={[
              { id: 'mystery', title: '🔎 Murder mysteries' },
              { id: 'escapeRoom', title: '🔐 Escape rooms' },
            ]}
            onChange={(game) => change((p) => ({ ...p, game }))}
          />
        </Card>

        <Card className="space-y-5">
          <h2 className="font-display text-2xl">🔎 Murder mysteries</h2>
          <Choice
            label="How you play"
            value={mystery.mode}
            options={MODES.map((m) => ({ id: m.id, title: m.title }))}
            onChange={(mode) => change((p) => ({ ...p, mystery: { ...p.mystery, mode } }))}
          />
          <Choice
            label="Shelf"
            value={mystery.shelf}
            options={SHELVES}
            onChange={(shelf) =>
              change((p) => ({
                ...p,
                mystery: {
                  ...p.mystery,
                  shelf,
                  tone: TONES[shelf].some((t) => t.id === p.mystery.tone) ? p.mystery.tone : 'standard',
                  drinkingPrompts: shelf === 'mature' && p.mystery.drinkingPrompts,
                },
              }))
            }
          />
          <Toggle label="Use the AI game master" checked={mystery.useAi} onChange={(useAi) => change((p) => ({ ...p, mystery: { ...p.mystery, useAi } }))} />
          {mystery.useAi && (
            <>
              <Choice
                label="Tone"
                value={mystery.tone}
                options={tones.map((t) => ({ id: t.id, title: t.title }))}
                onChange={(tone) => change((p) => ({ ...p, mystery: { ...p.mystery, tone } }))}
              />
              <Toggle
                label="Rewrite “Surprise me” around tonight's cast"
                hint="When no version's killer is one of your guests, the AI writes one where a guest did it."
                checked={mystery.tailor}
                onChange={(tailor) => change((p) => ({ ...p, mystery: { ...p.mystery, tailor } }))}
              />
            </>
          )}
          {mystery.shelf === 'mature' && (
            <Toggle
              label="Drinking-game toasts"
              hint="Every toast has a non-alcoholic option. Never on the Family shelf."
              checked={mystery.drinkingPrompts}
              onChange={(drinkingPrompts) => change((p) => ({ ...p, mystery: { ...p.mystery, drinkingPrompts } }))}
            />
          )}
        </Card>

        <Card className="space-y-5">
          <h2 className="font-display text-2xl">🔐 Escape rooms</h2>
          <Choice
            label="How you play"
            value={escape.mode}
            options={ESCAPE_MODES.map((m) => ({ id: m.id, title: m.title }))}
            onChange={(mode) => change((p) => ({ ...p, escape: { ...p.escape, mode } }))}
          />
          <Choice
            label="Who answers the puzzles"
            value={escape.answering}
            options={ANSWER_RULES.map((a) => ({ id: a.id, title: a.title }))}
            onChange={(answering) => change((p) => ({ ...p, escape: { ...p.escape, answering } }))}
          />
          <Choice label="Shelf" value={escape.shelf} options={SHELVES} onChange={(shelf) => change((p) => ({ ...p, escape: { ...p.escape, shelf } }))} />
          <Choice
            label="Length"
            value={escape.minutes}
            options={[
              { id: null, title: "Each room's own" },
              { id: 30, title: '⏱️ 30 min' },
              { id: 45, title: '⏱️ 45 min' },
              { id: 60, title: '⏱️ 60 min' },
            ]}
            onChange={(minutes) => change((p) => ({ ...p, escape: { ...p.escape, minutes } }))}
          />
          <p className="-mt-3 text-xs text-muted">A room that can't be played at that length starts at its own.</p>
          <Choice
            label="Difficulty"
            value={escape.difficulty}
            options={DIFFICULTIES}
            onChange={(difficulty) => change((p) => ({ ...p, escape: { ...p.escape, difficulty } }))}
          />
          <Choice
            label="Puzzles"
            value={escape.puzzles}
            options={[
              { id: 'fresh', title: '🎲 Fresh puzzles' },
              { id: 'daily', title: "📅 Today's challenge" },
            ]}
            onChange={(puzzles) => change((p) => ({ ...p, escape: { ...p.escape, puzzles } }))}
          />
          <Toggle label="Use the AI game master" checked={escape.useAi} onChange={(useAi) => change((p) => ({ ...p, escape: { ...p.escape, useAi } }))} />
        </Card>

        <div className="space-y-3">
          <ErrorText>{error}</ErrorText>
          {saved && (
            <p role="status" className="rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm text-ink">
              ✓ Saved. New parties start like this.
            </p>
          )}
          <Button onClick={save} disabled={busy} className="w-full">
            {busy ? 'Saving…' : 'Save my settings'}
          </Button>
        </div>
      </div>
    </Shell>
  )
}
