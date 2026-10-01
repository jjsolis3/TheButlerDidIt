import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import type { EscapeRoomSummary, Leaderboard } from '../lib/types'
import { LeaderboardRow } from './LeaderboardPanel'

const TABS = [
  { daily: true, label: "📅 Today's challenge" },
  { daily: false, label: '🏆 All time' },
] as const

/**
 * A room's best escapes on the landing page: today's challenge or all time, for the room's own
 * length on Normal. It loads only when opened, so the page doesn't ask for every room's board.
 * Times are public; team names appear only on the signed-in host's own escapes.
 */
export function RoomBoards({ room }: { room: EscapeRoomSummary }) {
  const [daily, setDaily] = useState(true)
  // Kept with the tab it belongs to, so switching tabs shows "Loading…" rather than the other board.
  const [loaded, setLoaded] = useState<{ daily: boolean; board: Leaderboard | null } | null>(null)

  useEffect(() => {
    let cancelled = false
    api.leaderboard(room.id, daily, room.timeLimitMinutes).then(
      (board) => !cancelled && setLoaded({ daily, board }),
      () => !cancelled && setLoaded({ daily, board: null }),
    )
    return () => {
      cancelled = true
    }
  }, [room.id, room.timeLimitMinutes, daily])

  const current = loaded?.daily === daily ? loaded : null
  const top = current?.board?.top.slice(0, 5) ?? []

  return (
    <section className="mt-3 rounded-lg border border-line bg-bg/60 p-3" aria-label={`${room.title} leaderboards`}>
      <div className="grid grid-cols-2 gap-1 rounded-lg border border-line p-1" role="tablist" aria-label="Which leaderboard">
        {TABS.map((tab) => (
          <button
            key={tab.label}
            role="tab"
            aria-selected={daily === tab.daily}
            onClick={() => setDaily(tab.daily)}
            className={`rounded-md px-2 py-1.5 text-xs font-semibold transition ${daily === tab.daily ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
          >
            {tab.label}
          </button>
        ))}
      </div>
      {!current && <p className="mt-3 text-xs text-muted">Loading…</p>}
      {current && !current.board && <p className="mt-3 text-xs text-muted">The leaderboard couldn't be loaded. Try again in a moment.</p>}
      {current?.board &&
        (top.length === 0 ? (
          <p className="mt-3 text-sm text-muted">{daily ? "Nobody has escaped today's puzzles yet. Be the first!" : 'Nobody has escaped yet. Be the first!'}</p>
        ) : (
          <ol className="mt-2 space-y-1 text-sm">
            {top.map((e) => (
              <LeaderboardRow key={e.rank} entry={e} />
            ))}
          </ol>
        ))}
      <p className="mt-2 text-xs text-muted">
        {room.timeLimitMinutes}-minute game on Normal.{daily ? " Today's challenge gives every group the same puzzles today." : ''}
      </p>
    </section>
  )
}
