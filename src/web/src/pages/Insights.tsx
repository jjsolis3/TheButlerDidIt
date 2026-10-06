import { useEffect, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { Bar, Feel, Section, Stat } from '../components/Stats'
import { Card, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { api } from '../lib/api'
import { FEEL } from '../lib/feel'
import { percent } from '../lib/numbers'
import { DEFAULT_PALETTE, ESCAPE_PALETTE, usePalette } from '../lib/theme'
import type { EscapeInsights, InsightsView, MysteryInsights } from '../lib/types'

const minutes = (seconds: number | null) =>
  seconds === null ? '—' : seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`

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

  // Back to wherever they came from: the admin hub's games list passes itself along, else the host's own library.
  const from = (useLocation().state as { from?: { to: string; label: string } } | null)?.from
  const back = from ?? (escape ? { to: '/escape/rooms', label: 'My escape rooms' } : { to: '/mysteries', label: 'My mysteries' })
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
