import { useState } from 'react'
import { QrCode } from '../components/Scene'
import { Button, ErrorText, StatusPill } from '../components/ui'
import { useParty } from '../lib/hub'
import type { EscapePlayerView, EscapePuzzleView, EscapeStageView, PartyInfo } from '../lib/types'
import { EscapeClock } from './EscapeClock'
import { GameMasterPanel } from './GameMasterPanel'
import { LeaderboardPanel } from './LeaderboardPanel'
import { elapsedSeconds, formatDuration, penaltyLabel } from './time'
import { useAtmosphere } from './useAtmosphere'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * The escape room on the TV: the lobby with the join code, then the room itself with the
 * clock, the puzzles in front of the group, what they've found and what's happened. It shows
 * only public things: clue pieces live on the phones, answers never leave the server.
 * The host (signed in) also gets the Start and Hint buttons here.
 */
export function EscapeStage({ info, token }: { info: PartyInfo; token?: string }) {
  const { stage, status, fatal, invoke } = useParty<EscapeStageView, EscapePlayerView>({ code: info.code, token, watchStage: true })
  if (fatal) return <Centered>{fatal}</Centered>
  if (!stage) return <Centered>Unlocking the room…</Centered>

  return (
    <div className="grain min-h-dvh">
      <StatusPill status={status} />
      <Tv stage={stage} info={info} invoke={invoke} />
    </div>
  )
}

/** Split out so the sound hook only runs once there is a room to play. */
function Tv({ stage, info, invoke }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke }) {
  const sound = useAtmosphere(stage)
  const over = stage.phase === 'escaped' || stage.phase === 'failed'
  return (
    <main className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-8">
      <div className="mb-2 flex justify-end">
        <button onClick={sound.toggle} className="rounded-full border border-line bg-surface/80 px-3 py-1 text-sm text-muted hover:text-ink" aria-pressed={sound.on}>
          {sound.on ? (sound.playing ? '🔊 Sound on' : '🔈 Click anywhere for sound') : '🔇 Sound off'}
        </button>
      </div>
      {stage.phase === 'lobby' && <Lobby stage={stage} info={info} invoke={invoke} />}
      {stage.phase === 'playing' && <Room stage={stage} info={info} invoke={invoke} />}
      {over && <Ending stage={stage} code={info.code} />}
    </main>
  )
}

/** A generated picture of the room or stage, if one has been made. Decorative: everything it shows is also in the text. */
function Art({ url, className = '' }: { url: string | null; className?: string }) {
  if (!url) return null
  return <img src={url} alt="" data-testid="room-art" className={`fade-in w-full rounded-xl object-cover ${className}`} />
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="grid min-h-dvh place-items-center p-6 text-center text-muted">{children}</div>
}

function Lobby({ stage, info, invoke }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke }) {
  const [error, setError] = useState<string | null>(null)
  const joinUrl = `${window.location.origin}/join/${info.code}`
  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_auto]">
      <div>
        <p className="text-xs tracking-[0.3em] text-accent uppercase">
          🔐 Escape room · {stage.timeLimitMinutes} minutes · {stage.daily ? "📅 Today's challenge" : '🎲 Shuffled puzzles'}
        </p>
        <h1 className="font-display mt-2 text-5xl">{stage.roomTitle}</h1>
        <Art url={stage.artUrl} className="mt-4 aspect-[21/9] max-w-3xl" />
        <p className="mt-4 max-w-2xl text-lg text-ink/90">{stage.synopsis}</p>
        <h2 className="font-display mt-8 text-2xl">Who's trapped ({stage.players.length})</h2>
        {stage.players.length === 0 ? (
          <p className="mt-2 text-muted">Nobody yet. Scan the code to join.</p>
        ) : (
          <ul className="mt-2 flex flex-wrap gap-2">
            {stage.players.map((p) => (
              <li key={p.seatId} className="rounded-full border border-line bg-surface px-3 py-1">
                {p.name}
              </li>
            ))}
          </ul>
        )}
        {info.isHost && (
          <div className="mt-8">
            <Button
              className="px-8 py-3 text-lg"
              disabled={stage.players.length === 0}
              onClick={() => invoke('EscapeStart', info.code).catch((e: Error) => setError(e.message))}
            >
              ⏱️ Start the clock
            </Button>
            <p className="mt-2 text-xs text-muted">Everyone's phone gets different clues when the clock starts, so wait until the whole group has joined.</p>
            <ErrorText>{error}</ErrorText>
          </div>
        )}
      </div>
      <div className="text-center">
        <QrCode url={joinUrl} size={200} />
        <p className="mt-3 text-sm text-muted">Join at {window.location.host}/join</p>
        <p className="font-display text-4xl tracking-widest">{info.code}</p>
      </div>
    </div>
  )
}

function Room({ stage, info, invoke }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke }) {
  const [error, setError] = useState<string | null>(null)
  const hint = (p: EscapePuzzleView) => {
    if (!confirm(`A hint for "${p.title}" costs ${penaltyLabel(stage.hintPenaltySeconds)} of your time. Take it?`)) return
    setError(null)
    invoke('EscapeHostHint', info.code, p.id).catch((e: Error) => setError(e.message))
  }
  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <p className="text-xs tracking-[0.3em] text-accent uppercase">
            {stage.roomTitle} · Room {stage.stageNumber} of {stage.stageCount}
          </p>
          <h1 className="font-display mt-1 text-4xl">{stage.stage?.title}</h1>
        </div>
        <div className="text-right" aria-label="Time left">
          <EscapeClock view={stage} large />
          <p className="text-xs text-muted">
            {stage.solvedCount}/{stage.puzzleCount} solved · {stage.hintsUsed} hint{stage.hintsUsed === 1 ? '' : 's'}
          </p>
        </div>
      </header>
      {/* Keyed on the stage, so the new room's picture fades in as the door opens. */}
      <Art key={stage.stage?.id} url={stage.artUrl} className="aspect-[21/7] max-h-72" />
      {stage.stageNumber === 1 && (
        // The villain's welcome, read out as the clock starts.
        <blockquote className="max-w-3xl border-l-2 border-accent pl-4 text-lg text-ink/80 italic">{stage.intro}</blockquote>
      )}
      <p className="max-w-3xl text-lg text-ink/90">{stage.stage?.description}</p>
      <GameMasterPanel gameMaster={stage.gameMaster} narration={stage.narration} />

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {stage.puzzles.map((p) => (
          <article
            key={p.id}
            data-testid={`puzzle-${p.id}`}
            className={`rounded-xl border p-4 ${p.solved ? 'border-green-600/60 bg-green-900/10' : p.needs.length ? 'border-line bg-surface/60 opacity-80' : 'border-accent/60 bg-surface'}`}
          >
            <div className="flex items-start justify-between gap-2">
              <h2 className="font-display text-xl">{p.title}</h2>
              <span className="shrink-0 text-lg" aria-hidden>
                {p.solved ? '✅' : p.needs.length ? '🔒' : p.kind === 'code' ? '🔢' : p.kind === 'text' ? '🔤' : '🗝️'}
              </span>
            </div>
            <p className="mt-2 text-sm leading-relaxed text-ink/90">{p.prompt}</p>
            {p.solved ? (
              <p className="mt-3 text-sm text-green-300">
                Opened by {p.solvedBy}. {p.solvedText}
              </p>
            ) : (
              <>
                {p.needs.length > 0 && <p className="mt-3 text-xs text-muted">Needs: {p.needs.join(', ')}</p>}
                {p.pieceCount > 0 && <p className="mt-3 text-xs text-accent">🧩 Clues on {p.pieceCount} phones: read them out!</p>}
                {p.hints.map((h, i) => (
                  <p key={i} className="mt-2 rounded-lg bg-bg/60 p-2 text-sm">
                    💡 {h}
                  </p>
                ))}
                {p.hintPending && <p className="mt-2 animate-pulse text-sm text-muted">💭 {stage.gameMaster?.name ?? 'The game master'} is thinking of a hint…</p>}
                {info.isHost && p.hintsLeft > 0 && !p.hintPending && (
                  <button className="mt-3 text-xs text-muted underline hover:text-ink" onClick={() => hint(p)}>
                    Hint (−{penaltyLabel(stage.hintPenaltySeconds)})
                  </button>
                )}
              </>
            )}
          </article>
        ))}
      </div>
      <ErrorText>{error}</ErrorText>

      <div className="grid gap-4 md:grid-cols-2">
        <section>
          <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">What you've found</h2>
          {stage.inventory.length === 0 ? (
            <p className="mt-2 text-sm text-muted">Nothing yet.</p>
          ) : (
            <ul className="mt-2 space-y-1 text-sm">
              {stage.inventory.map((i) => (
                <li key={i.id}>
                  🎒 <span className="text-ink">{i.name}</span> <span className="text-muted">— {i.description}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
        <section aria-live="polite">
          <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">What's happened</h2>
          <ul className="mt-2 space-y-1 text-sm text-muted">
            {[...stage.feed].reverse().slice(0, 6).map((f) => (
              <li key={f.at + f.text}>{f.text}</li>
            ))}
          </ul>
        </section>
      </div>
    </div>
  )
}

function Ending({ stage, code }: { stage: EscapeStageView; code: string }) {
  const escaped = stage.phase === 'escaped'
  // The doors are taken away once they've swung open; the bars stay, faded, as part of the "trapped" look.
  const [doorsOpen, setDoorsOpen] = useState(false)
  return (
    <div className="relative mx-auto max-w-2xl py-12 text-center">
      {/* The finale: doors swing open on an escape, bars come down when time runs out. Movement only, no flashing, and none at all with reduced motion. */}
      {!(escaped && doorsOpen) && (
        <div className={escaped ? 'finale-doors' : 'finale-bars'} aria-hidden data-testid={escaped ? 'finale-escaped' : 'finale-trapped'}>
          {escaped ? (
            <>
              <span />
              <span onAnimationEnd={() => setDoorsOpen(true)} />
            </>
          ) : (
            Array.from({ length: 9 }, (_, i) => <span key={i} style={{ animationDelay: `${i * 70}ms` }} />)
          )}
        </div>
      )}
      <Art url={stage.artUrl} className={`mb-6 aspect-[21/9] ${escaped ? '' : 'grayscale'}`} />
      <p className="text-6xl" aria-hidden>
        {escaped ? '🏁' : '⏰'}
      </p>
      <h1 className="font-display mt-4 text-5xl">{escaped ? 'You escaped!' : 'Trapped!'}</h1>
      <p className="mt-6 text-lg text-ink/90">{stage.endText}</p>
      {/* The game master's last word arrives a moment after the ending. */}
      <div className="mt-6 text-left">
        <GameMasterPanel gameMaster={stage.gameMaster} narration={stage.narration} />
      </div>
      <dl className="mt-8 grid grid-cols-3 gap-4 rounded-xl border border-line bg-surface p-4">
        <div>
          <dt className="text-xs text-muted">Time</dt>
          <dd className="font-display text-2xl">{formatDuration(elapsedSeconds(stage))}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted">Puzzles</dt>
          <dd className="font-display text-2xl">
            {stage.solvedCount}/{stage.puzzleCount}
          </dd>
        </div>
        <div>
          <dt className="text-xs text-muted">Hints</dt>
          <dd className="font-display text-2xl">{stage.hintsUsed}</dd>
        </div>
      </dl>
      {stage.puzzleSet !== null && (
        <p className="mt-4 text-sm text-muted" data-testid="puzzle-set">
          {stage.daily ? "Today's challenge" : 'Puzzle set'} <span className="font-display text-lg text-ink">#{stage.puzzleSet}</span>
          {stage.daily ? '' : ': share it to challenge friends with the same puzzles.'}
        </p>
      )}
      <LeaderboardPanel roomId={stage.roomId} code={code} daily={stage.daily} minutes={stage.timeLimitMinutes} />
    </div>
  )
}
