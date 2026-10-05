import { useEffect, useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { Card, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { api } from '../lib/api'
import { DEFAULT_PALETTE, ESCAPE_PALETTE, usePalette } from '../lib/theme'
import type { EscapeInsights, InsightsView, MysteryInsights } from '../lib/types'

/**
 * How difficult it felt, as a diverging bar: too easy and too hard pull opposite ways from "just right", a neutral
 * gray in the middle. Fixed colours, not the theme's, so the two poles always read as opposites; checked for
 * colour-blind separation and contrast against the dark surfaces (the dataviz validator).
 */
const FEEL = {
  tooEasy: { label: 'Too easy', color: '#3987e5' },
  justRight: { label: 'Just right', color: '#383835' },
  tooHard: { label: 'Too hard', color: '#e66767' },
} as const

const percent = (part: number, whole: number) => (whole === 0 ? 0 : Math.round((part / whole) * 100))
const minutes = (seconds: number | null) =>
  seconds === null ? '—' : seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`

/** One headline number. */
function Stat({ label, value, detail }: { label: string; value: ReactNode; detail?: ReactNode }) {
  return (
    <div className="rounded-xl border border-line bg-surface p-4">
      <p className="text-xs tracking-widest text-muted uppercase">{label}</p>
      <p className="mt-1 text-3xl font-semibold tabular-nums">{value}</p>
      {detail && <p className="mt-1 text-xs text-muted">{detail}</p>}
    </div>
  )
}

/** A horizontal bar on a faint track, its value at the tip. One series, so the title says what it is: no legend. */
function Bar({ label, value, max, note, emphasis = true }: { label: ReactNode; value: number; max: number; note?: string; emphasis?: boolean }) {
  const width = max === 0 ? 0 : (value / max) * 100
  return (
    <li className="grid grid-cols-[minmax(6rem,16rem)_1fr_auto] items-center gap-3 text-sm" title={`${typeof label === 'string' ? label : ''} ${value}${note ? ` (${note})` : ''}`}>
      <span className="text-ink/90 [overflow-wrap:anywhere]">{label}</span>
      <span className="h-5 rounded-r bg-bg" aria-hidden>
        <span
          className="block h-full rounded-r-[4px]"
          style={{ width: `${width}%`, background: emphasis ? 'var(--theme-accent)' : 'color-mix(in oklab, var(--theme-ink) 30%, transparent)' }}
        />
      </span>
      <span className="text-right text-muted tabular-nums">
        {value}
        {note ? ` · ${note}` : ''}
      </span>
    </li>
  )
}

function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <Card className="space-y-4">
      <div>
        <h2 className="font-display text-2xl">{title}</h2>
        {hint && <p className="text-sm text-muted">{hint}</p>}
      </div>
      {children}
    </Card>
  )
}

/** Too easy · just right · too hard, as one bar split by the share of votes, with the counts in the legend under it. */
function Feel({ votes }: { votes: InsightsView['difficulty'] }) {
  const total = votes.tooEasy + votes.justRight + votes.tooHard
  if (total === 0) return <p className="text-sm text-muted">No votes yet.</p>
  const parts = (['tooEasy', 'justRight', 'tooHard'] as const).filter((k) => votes[k] > 0)
  return (
    <div className="space-y-3">
      {/* 2px gaps between the parts are the surface showing through. */}
      <div className="flex h-6 gap-[2px] overflow-hidden rounded-[4px]" role="img" aria-label={`How hard it felt: ${parts.map((k) => `${FEEL[k].label} ${votes[k]}`).join(', ')}`}>
        {parts.map((k) => (
          <span key={k} style={{ width: `${percent(votes[k], total)}%`, background: FEEL[k].color }} title={`${FEEL[k].label}: ${votes[k]} (${percent(votes[k], total)}%)`} />
        ))}
      </div>
      <ul className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
        {(['tooEasy', 'justRight', 'tooHard'] as const).map((k) => (
          <li key={k} className="inline-flex items-center gap-2">
            <span className="inline-block h-3 w-3 rounded-sm" style={{ background: FEEL[k].color }} aria-hidden />
            <span className="text-ink/90">{FEEL[k].label}</span>
            <span className="text-muted tabular-nums">
              {votes[k]} ({percent(votes[k], total)}%)
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}

function MysterySections({ m }: { m: MysteryInsights }) {
  return (
    <Section title="Who did they accuse?" hint="Each version has its own killer, so each is shown on its own. A story most groups solve may be too easy; one nobody solves may be unfair.">
      <div className="space-y-6">
        {m.versions.map((v) => (
          <div key={v.id} className="space-y-3" data-testid="version-insights">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h3 className="font-semibold">
                {v.label} <span className="text-sm font-normal text-muted">· the killer is {v.killerName}</span>
              </h3>
              <p className="text-sm text-muted">
                {v.plays} game{v.plays === 1 ? '' : 's'} · {percent(v.correct, v.accusers)}% named the killer
              </p>
            </div>
            {v.accused.length === 0 ? (
              <p className="text-sm text-muted">Not played yet.</p>
            ) : (
              <ul className="space-y-1.5">
                {v.accused.map((a) => (
                  // The killer's bar in the accent, the rest in gray: the one that matters stands out, and says so in words.
                  <Bar key={a.characterId} label={a.killer ? `${a.name} (the killer)` : a.name} value={a.count} max={Math.max(...v.accused.map((x) => x.count))} emphasis={a.killer} />
                ))}
              </ul>
            )}
          </div>
        ))}
      </div>
    </Section>
  )
}

function EscapeSections({ e }: { e: EscapeInsights }) {
  // The puzzle with the highest score, if any scores above zero (a room nobody needed a hint in has no "most hinted").
  const top = (score: (p: EscapeInsights['puzzles'][number]) => number) => {
    let best: string | null = null
    let max = 0
    for (const p of e.puzzles) if (score(p) > max) [best, max] = [p.puzzleId, score(p)]
    return best
  }
  const slowest = top((p) => p.averageSeconds ?? 0)
  const mostHinted = top((p) => p.hintRate)
  const longest = Math.max(1, ...e.puzzles.map((p) => p.averageSeconds ?? 0))
  return (
    <Section
      title="Puzzle by puzzle"
      hint="How long each puzzle held groups up once its room opened, how often they bought a hint, and where time ran out. The slowest and most-hinted are the first places to look."
    >
      <div className="overflow-x-auto">
        <table className="w-full min-w-[36rem] text-left text-sm" data-testid="puzzle-insights">
          <thead className="text-xs tracking-widest text-muted uppercase">
            <tr>
              <th className="py-2 pr-3 font-normal">Puzzle</th>
              <th className="py-2 pr-3 font-normal">Average time</th>
              <th className="py-2 pr-3 text-right font-normal">Hints</th>
              <th className="py-2 text-right font-normal">Ran out here</th>
            </tr>
          </thead>
          <tbody>
            {e.puzzles.map((p) => (
              <tr key={p.puzzleId} className="border-t border-line align-top">
                <td className="py-2 pr-3">
                  <p className="text-ink">{p.title}</p>
                  <p className="text-xs text-muted">
                    {p.stageTitle} · {p.plays} game{p.plays === 1 ? '' : 's'}
                  </p>
                  {/* Icon and words, never colour alone. */}
                  <p className="mt-1 flex flex-wrap gap-1 text-xs">
                    {p.puzzleId === slowest && <span className="rounded-full border border-line px-2">🐢 Slowest</span>}
                    {p.puzzleId === mostHinted && <span className="rounded-full border border-line px-2">💡 Most hinted</span>}
                  </p>
                </td>
                <td className="py-2 pr-3">
                  <div className="flex items-center gap-2">
                    <span className="h-3 w-24 rounded-r bg-bg" aria-hidden>
                      <span className="block h-full rounded-r-[4px] bg-accent" style={{ width: `${((p.averageSeconds ?? 0) / longest) * 100}%` }} />
                    </span>
                    <span className="tabular-nums">{minutes(p.averageSeconds)}</span>
                  </div>
                </td>
                <td className="py-2 pr-3 text-right tabular-nums">{p.plays ? `${Math.round(p.hintRate * 100)}%` : '—'}</td>
                <td className="py-2 text-right tabular-nums">{p.stuck || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  )
}

/**
 * A mystery's or a room's insights (#130), for whoever looks after it: how many games, what guests thought, where
 * groups get stuck. Read-only; the editor link is where to act on it.
 */
export default function Insights() {
  const { kind = 'mystery', id = '' } = useParams()
  const escape = kind === 'escape'
  usePalette(escape ? ESCAPE_PALETTE : DEFAULT_PALETTE)
  const [view, setView] = useState<InsightsView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.insights(escape ? 'escape' : 'mystery', id).then(setView, (e: Error) => setError(e.message))
  }, [escape, id])

  const back = escape ? { to: '/escape/rooms', label: 'My escape rooms' } : { to: '/mysteries', label: 'My mysteries' }
  if (!view)
    return (
      <Shell>
        <Link to={back.to} className="text-sm text-muted underline hover:text-ink">
          ← {back.label}
        </Link>
        <div className="mt-4">{error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Gathering the evidence…</p>}</div>
      </Shell>
    )

  const r = view.rating
  const solved = view.mystery ? percent(view.mystery.correct, view.mystery.accusers) : view.escape ? percent(view.escape.escaped, view.plays) : 0
  return (
    <Shell wide>
      <Link to={back.to} className="text-sm text-muted underline hover:text-ink">
        ← {back.label}
      </Link>
      <div className="mt-3 mb-6">
        <Eyebrow>Insights</Eyebrow>
        <Heading className="mt-1">{view.title}</Heading>
        <p className="mt-1 text-sm text-muted">
          From every game played{view.lastPlayed ? `, the latest on ${new Date(view.lastPlayed).toLocaleDateString()}` : ''}. Guests rate it anonymously from their
          phones when the game ends.
        </p>
      </div>

      {view.plays === 0 && r.count === 0 ? (
        <Card>
          <p className="text-muted">No games recorded yet. Once groups play it, you'll see how they did and what they thought.</p>
        </Card>
      ) : (
        <div className="space-y-6">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="insights-stats">
            <Stat label="Games" value={view.plays} detail={view.plays ? `${view.averagePlayers} players on average` : undefined} />
            <Stat label="Rating" value={r.average === null ? '—' : `★ ${r.average}`} detail={`${r.count} rating${r.count === 1 ? '' : 's'}`} />
            <Stat
              label={escape ? 'Escaped' : 'Named the killer'}
              value={`${solved}%`}
              detail={escape ? `${view.escape?.escaped ?? 0} of ${view.plays} games` : `${view.mystery?.correct ?? 0} of ${view.mystery?.accusers ?? 0} guests`}
            />
            <Stat label="Length" value={!view.plays ? '—' : view.averageMinutes ? `${view.averageMinutes} min` : 'under 1 min'} detail="on average" />
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            <Section title="Ratings">
              <ul className="space-y-1.5" aria-label="Ratings by stars">
                {[5, 4, 3, 2, 1].map((star) => (
                  <Bar key={star} label={'★'.repeat(star)} value={r.stars[star - 1]} max={Math.max(...r.stars)} />
                ))}
              </ul>
            </Section>
            <Section title="How hard did it feel?">
              <Feel votes={view.difficulty} />
            </Section>
          </div>

          {view.mystery && <MysterySections m={view.mystery} />}
          {view.escape && <EscapeSections e={view.escape} />}

          <Section title="What guests said" hint={escape || view.comments.length ? undefined : 'Family games ask for stars only, never words.'}>
            {view.comments.length === 0 ? (
              <p className="text-sm text-muted">No comments yet.</p>
            ) : (
              <ul className="space-y-3" data-testid="insights-comments">
                {view.comments.map((c, i) => (
                  <li key={i} className="rounded-lg border border-line bg-bg p-3 text-sm">
                    <p className="text-ink/90">“{c.comment}”</p>
                    <p className="mt-1 text-xs text-muted">
                      {'★'.repeat(c.rating)} · {FEEL[c.difficulty].label} · {new Date(c.at).toLocaleDateString()}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
      )}
    </Shell>
  )
}
