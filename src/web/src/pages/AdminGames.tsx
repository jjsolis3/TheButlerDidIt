import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { ErrorText, FilterChip, Heading, inputClass } from '../components/ui'
import { api } from '../lib/api'
import { FEEL } from '../lib/feel'
import { ago, percent } from '../lib/numbers'
import type { AdminGameRow, ContentOrigin } from '../lib/types'

const ORIGINS: Record<ContentOrigin, string> = { builtIn: 'Built in', shared: 'Shared', host: "A host's own" }

type Sort = 'attention' | 'plays' | 'best' | 'worst'
const SORTS: { id: Sort; label: string }[] = [
  { id: 'attention', label: 'Needs a look first' },
  { id: 'plays', label: 'Most played' },
  { id: 'best', label: 'Best rated' },
  { id: 'worst', label: 'Lowest rated' },
]

/** Too few games or votes say nothing, so a game is only flagged after this many. */
const ENOUGH = 3

/**
 * Why a game might need a look, from its guests' votes and its games: the reasons, worst first, or none. Thresholds
 * are deliberately rough; the insights page has the detail.
 */
function concerns(g: AdminGameRow): string[] {
  const out: string[] = []
  const votes = g.tooEasy + g.justRight + g.tooHard
  if (g.ratings >= ENOUGH && g.rating !== null && g.rating < 3) out.push('Low rating')
  if (votes >= ENOUGH && g.tooHard / votes >= 0.5) out.push('Feels too hard')
  if (votes >= ENOUGH && g.tooEasy / votes >= 0.5) out.push('Feels too easy')
  if (g.plays >= ENOUGH && g.solveRate !== null) {
    if (g.kind === 'mystery' && g.solveRate < 0.2) out.push('Few name the killer')
    if (g.kind === 'mystery' && g.solveRate > 0.9) out.push('Almost everyone solves it')
    if (g.kind === 'escapeRoom' && g.solveRate < 0.2) out.push('Few groups escape')
  }
  return out
}

function compare(sort: Sort) {
  const byPlays = (a: AdminGameRow, b: AdminGameRow) => b.plays - a.plays || a.title.localeCompare(b.title)
  // Unrated games go last whichever way ratings are sorted: no rating isn't a low one.
  const rated = (a: AdminGameRow, b: AdminGameRow, dir: 1 | -1) =>
    a.rating === null ? (b.rating === null ? byPlays(a, b) : 1) : b.rating === null ? -1 : dir * (a.rating - b.rating) || byPlays(a, b)
  switch (sort) {
    case 'attention':
      return (a: AdminGameRow, b: AdminGameRow) => concerns(b).length - concerns(a).length || byPlays(a, b)
    case 'best':
      return (a: AdminGameRow, b: AdminGameRow) => rated(a, b, -1)
    case 'worst':
      return (a: AdminGameRow, b: AdminGameRow) => rated(a, b, 1)
    default:
      return byPlays
  }
}

/** The votes as one thin split bar, the same colours as the insights page. */
function FeltBar({ g }: { g: AdminGameRow }) {
  const total = g.tooEasy + g.justRight + g.tooHard
  if (total === 0) return <span className="text-muted">—</span>
  const label = (['tooEasy', 'justRight', 'tooHard'] as const).map((k) => `${FEEL[k].label} ${g[k]}`).join(', ')
  return (
    <span className="flex h-2.5 w-24 gap-px overflow-hidden rounded-[3px]" role="img" aria-label={label} title={label}>
      {(['tooEasy', 'justRight', 'tooHard'] as const)
        .filter((k) => g[k] > 0)
        .map((k) => (
          <span key={k} style={{ width: `${percent(g[k], total)}%`, background: FEEL[k].color }} />
        ))}
    </span>
  )
}

/**
 * Admin → Games: every mystery and escape room on the site, built-in and hosts' own, with how its games went. The
 * place to find what to improve next; each row opens its insights, where the detail is, and its editor.
 */
export default function AdminGames() {
  const [games, setGames] = useState<AdminGameRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [kind, setKind] = useState<'all' | AdminGameRow['kind']>('all')
  const [origin, setOrigin] = useState<'all' | ContentOrigin>('all')
  const [sort, setSort] = useState<Sort>('attention')
  const [search, setSearch] = useState('')

  useEffect(() => {
    api.admin.games().then(setGames, (e: Error) => setError(e.message))
  }, [])

  const shown = useMemo(() => {
    const q = search.trim().toLowerCase()
    return (games ?? [])
      .filter((g) => (kind === 'all' || g.kind === kind) && (origin === 'all' || g.origin === origin))
      .filter((g) => !q || g.title.toLowerCase().includes(q) || g.owner?.toLowerCase().includes(q))
      .sort(compare(sort))
  }, [games, kind, origin, sort, search])

  const flagged = (games ?? []).filter((g) => concerns(g).length > 0).length
  return (
    <div className="space-y-5">
      <div>
        <Heading>Games</Heading>
        <p className="mt-1 max-w-3xl text-sm text-muted">
          Every mystery and escape room, with how its games went. Guests rate a game on their phones when it ends. A game is flagged once it has {ENOUGH}{' '}
          games or votes that point the same way.
          {games && ` ${flagged ? `${flagged} need${flagged === 1 ? 's' : ''} a look.` : 'Nothing needs a look yet.'}`}
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <FilterChip on={kind === 'all'} onClick={() => setKind('all')}>
          All
        </FilterChip>
        <FilterChip on={kind === 'mystery'} onClick={() => setKind('mystery')}>
          🔎 Mysteries
        </FilterChip>
        <FilterChip on={kind === 'escapeRoom'} onClick={() => setKind('escapeRoom')}>
          🔐 Escape rooms
        </FilterChip>
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <input className={inputClass} type="search" placeholder="Find a game or host" aria-label="Find a game or host" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select className={inputClass} aria-label="Where it came from" value={origin} onChange={(e) => setOrigin(e.target.value as 'all' | ContentOrigin)}>
          <option value="all">Built in, shared and hosts' own</option>
          {(Object.keys(ORIGINS) as ContentOrigin[]).map((o) => (
            <option key={o} value={o}>
              {ORIGINS[o]}
            </option>
          ))}
        </select>
        <select className={inputClass} aria-label="Sort by" value={sort} onChange={(e) => setSort(e.target.value as Sort)}>
          {SORTS.map((s) => (
            <option key={s.id} value={s.id}>
              {s.label}
            </option>
          ))}
        </select>
      </div>

      <ErrorText>{error}</ErrorText>
      {!games && !error && <p className="text-muted">Gathering every game…</p>}
      {games && shown.length === 0 && <p className="text-muted">No games match.</p>}

      {shown.length > 0 && (
        // A wide table scrolls inside its own box on a phone; the page itself never scrolls sideways. `relative` makes
        // the box the containing block of the header's visually-hidden label (absolutely positioned), which would
        // otherwise escape the box's clipping and widen the whole page.
        <div className="relative overflow-x-auto rounded-xl border border-line bg-surface">
          <table className="w-full min-w-[46rem] text-left text-sm" data-testid="admin-games">
            <thead className="text-xs tracking-widest text-muted uppercase">
              <tr>
                <th className="px-4 py-3 font-normal">Game</th>
                <th className="px-3 py-3 text-right font-normal">Games</th>
                <th className="px-3 py-3 text-right font-normal">Rating</th>
                <th className="px-3 py-3 text-right font-normal">Solved</th>
                <th className="px-3 py-3 font-normal">Felt</th>
                <th className="px-4 py-3 font-normal">
                  <span className="sr-only">Links</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {shown.map((g) => {
                const flags = concerns(g)
                const insights = `/insights/${g.kind === 'mystery' ? 'mystery' : 'escape'}/${encodeURIComponent(g.id)}`
                const editor = g.kind === 'mystery' ? `/mysteries/${encodeURIComponent(g.id)}` : `/escape/rooms/${encodeURIComponent(g.id)}`
                return (
                  <tr key={`${g.kind}:${g.id}`} className="border-t border-line align-top" data-testid="admin-game">
                    <td className="px-4 py-3">
                      <p className="font-semibold text-ink">
                        <span aria-hidden>{g.kind === 'mystery' ? '🔎 ' : '🔐 '}</span>
                        {g.title}
                      </p>
                      <p className="text-xs text-muted">
                        {g.shelf === 'family' ? '🧸 Family' : '🍷 Adults'} · {ORIGINS[g.origin]}
                        {g.owner && `, ${g.owner}`}
                        {g.hidden && ' · off the shelf'}
                        {g.lastPlayed && ` · last played ${ago(g.lastPlayed)}`}
                      </p>
                      {flags.length > 0 && (
                        <p className="mt-1 flex flex-wrap gap-1 text-xs">
                          {flags.map((f) => (
                            <span key={f} className="rounded-full border border-red-300/50 px-2 text-red-200">
                              ⚠ {f}
                            </span>
                          ))}
                        </p>
                      )}
                    </td>
                    <td className="px-3 py-3 text-right tabular-nums">
                      {g.plays}
                      {g.recentPlays > 0 && <span className="block text-xs text-muted">{g.recentPlays} in 30 days</span>}
                    </td>
                    <td className="px-3 py-3 text-right tabular-nums">
                      {g.rating === null ? <span className="text-muted">—</span> : `★ ${g.rating}`}
                      {g.ratings > 0 && <span className="block text-xs text-muted">{g.ratings} votes</span>}
                    </td>
                    <td className="px-3 py-3 text-right tabular-nums">
                      {g.solveRate === null ? <span className="text-muted">—</span> : `${Math.round(g.solveRate * 100)}%`}
                      {g.solveRate !== null && <span className="block text-xs text-muted">{g.kind === 'mystery' ? 'named the killer' : 'escaped'}</span>}
                    </td>
                    <td className="px-3 py-3">
                      <FeltBar g={g} />
                    </td>
                    <td className="px-4 py-3 text-right whitespace-nowrap">
                      <Link to={insights} state={{ from: { to: '/admin/games', label: 'Admin · Games' } }} className="text-accent underline" aria-label={`Insights for ${g.title}`}>
                        Insights
                      </Link>
                      <Link to={editor} className="ml-3 text-muted underline hover:text-ink" aria-label={`Open ${g.title}`}>
                        Open
                      </Link>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
