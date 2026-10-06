import { useState } from 'react'
import { Link } from 'react-router'
import { FeedbackCard } from '../components/FeedbackCard'
import { Button, ErrorText, StatusPill, inputClass } from '../components/ui'
import { useParty } from '../lib/hub'
import type { EscapePlayerView, EscapePuzzleView, EscapeStageView } from '../lib/types'
import { EscapeClock } from './EscapeClock'
import { ItemInspector } from './ItemInspector'
import { Notebook } from './Notebook'
import { CipherTool, DeductionHelper, SequenceTerms, SwitchGrid } from './PuzzleWidgets'
import { SceneView } from './SceneView'
import { StageCard } from './StageCard'
import { elapsedSeconds, formatDuration, penaltyLabel } from './time'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * A player's phone in an escape room: their own clue pieces (nobody else sees them), the part of the
 * room to search, the puzzles in front of the group with the right controls for each, what the group
 * is carrying (to look at closely or put together), and the group's notebook.
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
          <StageCard code={code} view={stage} />
          <section>
            <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">Only you can see these</h2>
            {player.pieces.length === 0 ? (
              <p className="mt-2 text-sm text-muted">No clues for you in this room. Listen to the others!</p>
            ) : (
              <ul className="mt-2 space-y-2">
                {player.pieces.map((p) => (
                  <li key={p.puzzleId + p.text} className="rounded-xl border border-accent/70 bg-accent/5 p-3 text-sm" data-testid="my-clue">
                    <span className="text-xs text-accent">
                      🧩 {p.puzzleTitle}
                      {p.foundIn && <span className="text-muted"> · found in the {p.foundIn}</span>}
                    </span>
                    <p className="mt-1 text-ink">{p.text}</p>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {stage.scene && (
            <SceneView
              scene={stage.scene}
              artUrl={stage.artUrl}
              feed={stage.feed}
              onExamine={(id) => invoke('EscapeExamine', id)}
              penaltySeconds={stage.searchPenaltySeconds}
            />
          )}

          <section className="space-y-3">
            <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">In front of you</h2>
            {/* Yours first, then the ones nobody has taken, then the others', then the solved ones. */}
            {[...stage.puzzles]
              .sort((a, b) => order(a, player.seatId) - order(b, player.seatId))
              .map((p) => (
                <PuzzleCard key={p.id} code={code} puzzle={p} stage={stage} me={player.seatId} invoke={invoke} />
              ))}
          </section>

          <ItemInspector items={stage.inventory} invoke={invoke} />
          <Notebook notes={stage.notebook} />
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
          <Link to="/" className="mt-4 inline-flex min-h-11 items-center justify-center rounded-lg border border-line px-4 py-2 text-sm hover:border-accent">
            Back to home
          </Link>
        </div>
      )}
      {/* How was it? (#130) Anonymous, for whoever made the room. */}
      <FeedbackCard token={token} gameOver={stage.phase === 'escaped' || stage.phase === 'failed'} />
    </div>
  )
}

const order = (p: EscapePuzzleView, me: string) => (p.solved ? 3 : p.heldBy?.seatId === me ? 0 : p.heldBy ? 2 : 1)

/**
 * One puzzle on a phone. While puzzles go to people (#132), only its holder gets the controls to answer it: anyone
 * else sees who's on it (and can take over once they've stopped trying). Its holder can hand it back or pass it on.
 */
function PuzzleCard({ code, puzzle: p, stage, me, invoke }: { code: string; puzzle: EscapePuzzleView; stage: EscapeStageView; me: string; invoke: Invoke }) {
  const penalty = stage.hintPenaltySeconds
  const gameMaster = stage.gameMaster?.name ?? null
  const turns = stage.answering !== 'anyone' && p.kind !== 'search'
  const mine = turns && p.heldBy?.seatId === me
  // Whose things a search can find: the holder's, or anyone's when anyone may answer.
  const finder = !turns ? null : mine ? 'only you can find them' : p.heldBy ? `only ${p.heldBy.name} can find them` : 'whoever takes this one can find them'
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
  const act = (method: string, ...args: unknown[]) =>
    void run(async () => {
      await invoke(method, p.id, ...args)
      return null
    })
  const others = stage.players.filter((x) => x.seatId !== me)

  return (
    <article className={`rounded-xl border p-3 ${p.solved ? 'border-green-600/60' : 'border-line'} bg-surface`} data-testid={`phone-puzzle-${p.id}`}>
      <p className="font-semibold">
        {p.solved ? '✅ ' : p.needs.length ? '🔒 ' : ''}
        {p.title}
      </p>
      <p className="mt-1 text-sm text-ink/90">{p.prompt}</p>
      {!p.solved && p.piecesHidden > 0 && (
        <p className="mt-1 text-xs text-accent">
          🧩 {p.piecesHidden} clue piece{p.piecesHidden === 1 ? '' : 's'} still hidden in the room{finder ? `: ${finder}` : ''}
        </p>
      )}
      {!p.solved && p.keysHidden > 0 && (
        <p className="mt-1 text-xs text-accent">
          🔑 Its key is written on something in this room{finder ? `: ${finder}` : ''}
        </p>
      )}
      {!p.solved && turns && (
        <div className="mt-2 rounded-lg bg-bg/60 p-2 text-sm" data-testid="puzzle-turn">
          {!p.heldBy ? (
            <Button className="w-full" disabled={busy} onClick={() => act('EscapeTake')}>
              🙋 Take this puzzle
            </Button>
          ) : mine ? (
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-semibold text-accent">🙋 You're on this one.</span>
              <button className="text-xs text-muted underline hover:text-ink" disabled={busy} onClick={() => act('EscapeRelease')}>
                Hand it back
              </button>
              {others.length > 0 && (
                <select
                  aria-label={`Pass ${p.title} to`}
                  className="rounded border border-line bg-bg px-2 py-1 text-xs"
                  value=""
                  disabled={busy}
                  onChange={(e) => e.target.value && act('EscapePass', e.target.value)}
                >
                  <option value="">Pass it to…</option>
                  {others.map((o) => (
                    <option key={o.seatId} value={o.seatId}>
                      {o.name}
                    </option>
                  ))}
                </select>
              )}
            </div>
          ) : (
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span>
                🙋 <span className="font-semibold">{p.heldBy.name}</span> is on this one.
                {p.heldBy.free && <span className="text-muted"> They haven't tried it for a while.</span>}
              </span>
              {p.heldBy.free && (
                <Button variant="ghost" className="min-h-9 py-1" disabled={busy} onClick={() => act('EscapeTake')}>
                  Take over
                </Button>
              )}
            </div>
          )}
        </div>
      )}
      {p.solved ? (
        <p className="mt-2 text-sm text-green-300">{p.solvedText}</p>
      ) : p.needs.length > 0 ? (
        <p className="mt-2 text-xs text-muted">You need {p.needs.join(' and ')} first.</p>
      ) : turns && !mine ? null : p.kind === 'use' ? (
        <Button className="mt-3 w-full" disabled={busy} onClick={use}>
          Use it
        </Button>
      ) : p.kind === 'search' ? (
        <p className="mt-2 text-sm text-muted">
          🔎 Search the room: {p.finds?.found ?? 0} of {p.finds?.total ?? 0} found
        </p>
      ) : p.kind === 'switches' ? (
        <SwitchGrid puzzle={p} invoke={invoke} />
      ) : (
        <>
        {p.kind === 'code' && <SequenceTerms prompt={p.prompt} />}
        {p.cipher && <CipherTool cipher={p.cipher} />}
        {p.deduction && <DeductionHelper puzzle={p} scratchKey={`escape:${code}:${p.id}`} onUseCode={setAnswer} />}
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
        </>
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
