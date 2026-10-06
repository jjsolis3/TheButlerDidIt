import type { EscapeMarkView, EscapeStageView } from '../lib/types'

// Finding the locks, and the final lock built from them (#134): shared by the TV and the phones.

/** The mark an opened lock left for its stage's final lock (#134): big enough to read across the room. */
export function MarkBadge({ mark }: { mark: EscapeMarkView }) {
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full border border-accent/60 bg-accent/10 px-2 py-0.5 align-middle text-sm font-semibold text-ink"
      data-testid="mark"
      title="The mark this lock left"
    >
      <span>{mark.mark}</span>
      <span className="font-display">{mark.digit}</span>
    </span>
  )
}

/**
 * On the final lock: every mark the other locks here left, in the room's own order (never the code's: finding that is
 * the last puzzle), so the group can read them off in one place.
 */
export function MarksSoFar({ stage }: { stage: EscapeStageView }) {
  const marks = stage.puzzles.flatMap((x) => (x.mark ? [{ id: x.id, mark: x.mark }] : []))
  if (marks.length === 0) return null
  return (
    <p className="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-muted" data-testid="marks-so-far">
      The marks you've found:
      {marks.map((m) => (
        <MarkBadge key={m.id} mark={m.mark} />
      ))}
    </p>
  )
}

/**
 * How many locks the group has found in this part of the room (#134), never how many are left. When every lock
 * found is open and the room still hasn't opened, something here is still out of sight: say so, so a group that has
 * opened everything in front of it knows to search rather than wait.
 */
export function LocksFound({ stage }: { stage: EscapeStageView }) {
  const found = stage.puzzles.length
  const allOpen = stage.phase === 'playing' && found > 0 && stage.puzzles.every((p) => p.solved)
  return (
    <div className="shrink-0 text-sm" data-testid="locks-found">
      <p className="text-muted">
        🔓 {found} lock{found === 1 ? '' : 's'} found here
      </p>
      {allOpen && (
        <p className="mt-1 rounded-lg border border-accent/60 bg-accent/10 px-3 py-2 text-ink" role="status">
          🔎 Every lock you've found here is open, but something is still hidden. Search the room!
        </p>
      )}
    </div>
  )
}
