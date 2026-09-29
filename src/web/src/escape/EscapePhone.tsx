import { useState } from 'react'
import { Button, ErrorText, StatusPill, inputClass } from '../components/ui'
import { useParty } from '../lib/hub'
import type { EscapePlayerView, EscapePuzzleView, EscapeStageView } from '../lib/types'
import { EscapeClock } from './EscapeClock'
import { elapsedSeconds, formatDuration, penaltyLabel } from './time'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * A player's phone in an escape room: their own clue pieces (nobody else sees them), the
 * puzzles in front of the group with an answer pad for each, and what the group is carrying.
 */
export function EscapePhone({ code, token, onLeave }: { code: string; token: string; onLeave: () => void }) {
  const [removed, setRemoved] = useState(false)
  const { player, status, fatal, invoke } = useParty<EscapeStageView, EscapePlayerView>({
    code,
    token,
    joinSeat: true,
    onRemoved: () => setRemoved(true),
  })

  if (removed || fatal) {
    return (
      <div className="mx-auto max-w-md space-y-4 p-6 text-center">
        <p className="font-display text-2xl">{removed ? 'The host removed your seat.' : 'We lost your seat.'}</p>
        {fatal && <p className="text-muted">{fatal}</p>}
        <Button onClick={onLeave}>Join again</Button>
      </div>
    )
  }
  if (!player) return <p className="p-8 text-center text-muted">Finding your seat…</p>
  const stage = player.stage

  return (
    <div className="mx-auto max-w-md space-y-5 px-4 py-5">
      <StatusPill status={status} />
      <header className="flex items-start justify-between gap-3">
        <div>
          <p className="text-xs tracking-widest text-accent uppercase">{stage.roomTitle}</p>
          <p className="font-display text-2xl">{stage.phase === 'playing' ? stage.stage?.title : player.name}</p>
        </div>
        {stage.phase === 'playing' && <EscapeClock view={stage} />}
      </header>

      {stage.phase === 'lobby' && (
        <div className="rounded-xl border border-line bg-surface p-4">
          <p className="font-display text-xl">You're in, {player.name}.</p>
          <p className="mt-2 text-sm text-muted">
            When the host starts the clock, your clues appear here. Everyone gets different ones, so be ready to read yours out loud.
          </p>
        </div>
      )}

      {stage.phase === 'playing' && (
        <>
          <section>
            <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">Only you can see these</h2>
            {player.pieces.length === 0 ? (
              <p className="mt-2 text-sm text-muted">No clues for you in this room. Listen to the others!</p>
            ) : (
              <ul className="mt-2 space-y-2">
                {player.pieces.map((p) => (
                  <li key={p.puzzleId + p.text} className="rounded-xl border border-accent/70 bg-accent/5 p-3 text-sm" data-testid="my-clue">
                    <span className="text-xs text-accent">🧩 {p.puzzleTitle}</span>
                    <p className="mt-1 text-ink">{p.text}</p>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="space-y-3">
            <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">In front of you</h2>
            {stage.puzzles.map((p) => (
              <PuzzleCard key={p.id} puzzle={p} penalty={stage.hintPenaltySeconds} gameMaster={stage.gameMaster?.name ?? null} invoke={invoke} />
            ))}
          </section>

          {stage.inventory.length > 0 && (
            <section>
              <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">The group is carrying</h2>
              <ul className="mt-2 flex flex-wrap gap-2 text-sm">
                {stage.inventory.map((i) => (
                  <li key={i.id} className="rounded-full border border-line bg-surface px-3 py-1" title={i.description}>
                    🎒 {i.name}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}

      {(stage.phase === 'escaped' || stage.phase === 'failed') && (
        <div className="rounded-xl border border-line bg-surface p-5 text-center">
          <p className="text-5xl" aria-hidden>
            {stage.phase === 'escaped' ? '🏁' : '⏰'}
          </p>
          <p className="font-display mt-2 text-3xl">{stage.phase === 'escaped' ? 'You escaped!' : 'Trapped!'}</p>
          <p className="mt-3 text-sm text-ink/90">{stage.endText}</p>
          <p className="mt-3 text-sm text-muted">
            {formatDuration(elapsedSeconds(stage))} · {stage.solvedCount}/{stage.puzzleCount} puzzles · {stage.hintsUsed} hint{stage.hintsUsed === 1 ? '' : 's'}
          </p>
          {stage.puzzleSet !== null && !stage.daily && <p className="mt-2 text-xs text-muted">Puzzle set #{stage.puzzleSet}: share it to challenge friends.</p>}
        </div>
      )}
    </div>
  )
}

function PuzzleCard({ puzzle: p, penalty, gameMaster, invoke }: { puzzle: EscapePuzzleView; penalty: number; gameMaster: string | null; invoke: Invoke }) {
  const [answer, setAnswer] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const run = async (action: () => Promise<string | null>) => {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      setResult(await action())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const tryAnswer = () =>
    run(async () => {
      const opened = await invoke<boolean>('EscapeAnswer', p.id, answer)
      setAnswer('')
      return opened ? '✓ It opened!' : '✗ Nothing happened.'
    })
  const use = () =>
    run(async () => {
      await invoke('EscapeUse', p.id)
      return '✓ It worked!'
    })
  const hint = () => {
    if (!confirm(`A hint costs ${penaltyLabel(penalty)} of the group's time. Take it?`)) return
    void run(async () => {
      await invoke('EscapeHint', p.id)
      return null
    })
  }

  return (
    <article className={`rounded-xl border p-3 ${p.solved ? 'border-green-600/60' : 'border-line'} bg-surface`} data-testid={`phone-puzzle-${p.id}`}>
      <p className="font-semibold">
        {p.solved ? '✅ ' : p.needs.length ? '🔒 ' : ''}
        {p.title}
      </p>
      <p className="mt-1 text-sm text-ink/90">{p.prompt}</p>
      {p.solved ? (
        <p className="mt-2 text-sm text-green-300">{p.solvedText}</p>
      ) : p.needs.length > 0 ? (
        <p className="mt-2 text-xs text-muted">You need {p.needs.join(' and ')} first.</p>
      ) : p.kind === 'use' ? (
        <Button className="mt-3 w-full" disabled={busy} onClick={use}>
          Use it
        </Button>
      ) : (
        <form
          className="mt-3 flex gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            if (answer.trim()) void tryAnswer()
          }}
        >
          <input
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            inputMode={p.kind === 'code' ? 'numeric' : 'text'}
            autoComplete="off"
            maxLength={100}
            placeholder={p.kind === 'code' ? 'Code' : 'Answer'}
            aria-label={`Answer for ${p.title}`}
            className={`${inputClass} flex-1`}
          />
          <Button type="submit" disabled={busy || !answer.trim()}>
            Try
          </Button>
        </form>
      )}
      {!p.solved &&
        p.hints.map((h, i) => (
          <p key={i} className="mt-2 rounded-lg bg-bg/60 p-2 text-sm">
            💡 {h}
          </p>
        ))}
      {!p.solved && p.hintPending && <p className="mt-2 animate-pulse text-sm text-muted">💭 {gameMaster ?? 'The game master'} is thinking of a hint…</p>}
      {!p.solved && p.hintsLeft > 0 && !p.hintPending && (
        <button className="mt-2 text-xs text-muted underline" onClick={hint} disabled={busy}>
          Need a hint? (−{penaltyLabel(penalty)})
        </button>
      )}
      {result && <p className="mt-2 text-sm" role="status">{result}</p>}
      <ErrorText>{error}</ErrorText>
    </article>
  )
}
