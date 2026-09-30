import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import type { EscapeDifficulty, Leaderboard, LeaderboardEntry } from '../lib/types'
import { formatDuration } from './time'

/**
 * The end-of-game leaderboard: where this group ranked and the room's best escapes, all time
 * or today's challenge, at this game's length (a 30-minute game plays fewer puzzles, so it has
 * its own board, and so does each difficulty). Times are public; names only appear on the host's own escapes.
 */
export function LeaderboardPanel({ roomId, code, daily, minutes, difficulty }: { roomId: string; code: string; daily: boolean; minutes: number; difficulty: EscapeDifficulty }) {
  const [board, setBoard] = useState<Leaderboard | null>(null)
  useEffect(() => {
    // The result is saved with the game's last move, so it's already there when this appears.
    api.leaderboard(roomId, daily, minutes, code, difficulty).then(setBoard, () => setBoard(null))
  }, [roomId, code, daily, minutes, difficulty])
  if (!board) return null

  return (
    <section className="mt-8 rounded-xl border border-line bg-surface p-4 text-left" aria-label="Leaderboard">
      <h2 className="font-display text-2xl">
        {daily ? "🏆 Today's challenge" : '🏆 Best escapes'} <span className="text-base text-muted">
          · {minutes}-minute game · {difficulty[0].toUpperCase() + difficulty.slice(1)}
        </span>
      </h2>
      {board.thisParty && (
        <p className="mt-1 text-accent" data-testid="your-rank">
          You ranked #{board.thisParty.rank} with {formatDuration(board.thisParty.score)}
          {board.thisParty.hintsUsed > 0 ? ` (including ${board.thisParty.hintsUsed} hint${board.thisParty.hintsUsed === 1 ? '' : 's'})` : ''}.
        </p>
      )}
      <ol className="mt-3 space-y-1 text-sm">
        {board.top.map((e) => (
          <Row key={e.rank} entry={e} />
        ))}
      </ol>
      {board.top.length === 0 && <p className="mt-2 text-sm text-muted">Nobody has escaped yet. Be the first!</p>}
      <p className="mt-3 text-xs text-muted">Score = time taken plus the time each hint cost. Lower is better.</p>
    </section>
  )
}

function Row({ entry: e }: { entry: LeaderboardEntry }) {
  return (
    <li className={`flex items-baseline justify-between gap-3 rounded-lg px-2 py-1 ${e.thisParty ? 'bg-accent/15 text-ink' : ''}`}>
      <span>
        <span className="inline-block w-8 text-muted">#{e.rank}</span>
        {e.team ?? `A team of ${e.playerCount}`}
        {e.thisParty && <span className="ml-2 text-xs text-accent">(you)</span>}
      </span>
      <span className="font-display tabular-nums">{formatDuration(e.score)}</span>
    </li>
  )
}
