import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { CheerBar, CheerOverlay } from '../components/Cheers'
import { FitToScreen } from '../components/FitToScreen'
import { RecapShare } from '../components/RecapShare'
import { QrCode } from '../components/Scene'
import { Button, ErrorText, StatusPill } from '../components/ui'
import { WatchersPanel } from '../components/WatchersPanel'
import { api } from '../lib/api'
import { useCheers } from '../lib/cheers'
import { useParty } from '../lib/hub'
import { TV_LAYOUT, useMediaQuery } from '../lib/useMediaQuery'
import type { WatchingAs } from '../lib/seats'
import type { EscapePlayerView, EscapePuzzleView, EscapeStageView, PartyInfo } from '../lib/types'
import { EscapeClock } from './EscapeClock'
import { GameMasterPanel } from './GameMasterPanel'
import { DIFFICULTY, KIND_ICON } from './labels'
import { LeaderboardPanel } from './LeaderboardPanel'
import { LocksFound, MarkBadge, MarksSoFar } from './Locks'
import { markSeen, readSeen } from './reveal'
import { RoomReveal } from './RoomReveal'
import { Notebook } from './Notebook'
import { SwitchGrid } from './PuzzleWidgets'
import { backdrop } from './moods'
import { SceneView, SearchedList } from './SceneView'
import { ShareCardButton } from './ShareCardButton'
import { elapsedSeconds, formatDuration, penaltyLabel } from './time'
import { useAtmosphere } from './useAtmosphere'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * The escape room on the TV: the lobby with the join code, then the room itself with the
 * clock, the puzzles in front of the group, what they've found and what's happened. It shows
 * only public things: clue pieces live on the phones, answers never leave the server.
 * The host (signed in) also gets the Start and Hint buttons here.
 */
export function EscapeStage({ info, token, watcher }: { info: PartyInfo; token?: string; watcher?: WatchingAs }) {
  const cheers = useCheers()
  // Bumped whenever someone starts or stops watching, so the host's list refreshes (#112).
  const [audience, setAudience] = useState(0)
  const tvLayout = useMediaQuery(TV_LAYOUT)
  const { stage, status, fatal, invoke } = useParty<EscapeStageView, EscapePlayerView>({
    code: info.code,
    token,
    watchStage: true,
    onRemoved: watcher?.onRemoved,
    onCheer: cheers.add,
    onAudience: info.isHost ? () => setAudience((n) => n + 1) : undefined,
  })
  if (fatal) return <Centered>{watcher ? <WatchEnded onLeave={watcher.onLeave} /> : fatal}</Centered>
  if (!stage) return <Centered>Unlocking the room…</Centered>

  // While the clock runs on a big screen, the room fills it (#116) and the host's watchers sit in its header.
  const fullScreen = tvLayout && stage.phase === 'playing'
  return (
    <div className={`grain min-h-dvh ${watcher && !fullScreen ? 'pb-28' : ''}`}>
      <StatusPill status={status} />
      <Tv
        stage={stage}
        info={info}
        invoke={invoke}
        tv={tvLayout}
        inset={!!watcher}
        watchers={info.isHost && <WatchersPanel code={info.code} refresh={audience} variant="chip" />}
      />
      {info.isHost && !fullScreen && (
        <div className="mx-auto w-full max-w-6xl px-4 pb-10 sm:px-8">
          <WatchersPanel code={info.code} refresh={audience} open={stage.phase === 'lobby'} />
        </div>
      )}
      <CheerOverlay cheers={cheers.cheers} />
      {watcher && <CheerBar invoke={invoke} name={watcher.name} onLeave={watcher.onLeave} />}
    </div>
  )
}

/** Shown to someone watching whose token no longer works: the host removed them, or the party is over. */
function WatchEnded({ onLeave }: { onLeave: () => void }) {
  return (
    <div>
      <p className="font-display text-2xl text-ink">You're no longer watching this party.</p>
      <Button className="mt-4" onClick={onLeave}>
        Back home
      </Button>
    </div>
  )
}

/**
 * Split out so the sound hook only runs once there is a room to play. `tv` is the full-screen layout (#116): while
 * the clock runs, the room fills the screen, with the sound switch and the host's watchers in its header.
 * `inset` leaves room for a watcher's cheer bar along the bottom.
 */
function Tv({ stage, info, invoke, tv, watchers, inset }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke; tv: boolean; watchers: ReactNode; inset: boolean }) {
  const sound = useAtmosphere(stage)
  const over = stage.phase === 'escaped' || stage.phase === 'failed'
  // Each new stage after the first opens with a reveal (#110), once per TV: the intro already covered the first.
  const [seen, setSeen] = useState(() => readSeen('tv', info.code))
  const revealing = stage.phase === 'playing' && stage.stageNumber > 1 && stage.stage && !seen.has(stage.stage.id) ? stage.stage.id : null
  const revealed = (id: string) => {
    const next = new Set(seen).add(id)
    setSeen(next)
    markSeen('tv', info.code, next)
  }
  const soundSwitch = (
    <button onClick={sound.toggle} className="rounded-full border border-line bg-surface/80 px-3 py-1 text-sm text-muted hover:text-ink" aria-pressed={sound.on}>
      {sound.on ? (sound.playing ? '🔊 Sound on' : '🔈 Click anywhere for sound') : '🔇 Sound off'}
    </button>
  )
  const reveal = revealing && <RoomReveal key={revealing} view={stage} mode="stage" sound={sound.on} duck={sound.duck} onDone={() => revealed(revealing)} />

  if (tv && stage.phase === 'playing')
    return (
      <main className={`w-full px-6 py-5 xl:px-10 ${inset ? 'h-[calc(100dvh-4.5rem)]' : 'h-dvh'}`}>
        <Room
          stage={stage}
          info={info}
          invoke={invoke}
          tv
          controls={
            <>
              {soundSwitch}
              {watchers}
            </>
          }
        />
        {reveal}
      </main>
    )

  return (
    <main className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-8">
      <div className="mb-2 flex justify-end">{soundSwitch}</div>
      {stage.phase === 'lobby' && <Lobby stage={stage} info={info} invoke={invoke} sound={sound.on} duck={sound.duck} />}
      {stage.phase === 'playing' && <Room stage={stage} info={info} invoke={invoke} tv={false} controls={null} />}
      {over && <Ending stage={stage} code={info.code} isHost={info.isHost} />}
      {reveal}
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

function Lobby({ stage, info, invoke, sound, duck }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke; sound: boolean; duck: (down: boolean) => void }) {
  const [error, setError] = useState<string | null>(null)
  // Start plays the room's intro first (#110); the clock starts when it ends or is skipped.
  const [intro, setIntro] = useState(false)
  const start = () => {
    setIntro(false)
    invoke('EscapeStart', info.code).catch((e: Error) => setError(e.message))
  }
  const joinUrl = `${window.location.origin}/join/${info.code}`
  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_auto]">
      <div>
        <p className="text-xs tracking-[0.3em] text-accent uppercase">
          🔐 Escape room · {stage.timeLimitMinutes} minutes · {DIFFICULTY[stage.difficulty]} · {stage.daily ? "📅 Today's challenge" : '🎲 Shuffled puzzles'}
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
              onClick={() => {
                setError(null)
                setIntro(true)
              }}
            >
              ⏱️ Start the clock
            </Button>
            <p className="mt-2 text-xs text-muted">
              The room's intro plays first, then the clock starts. Everyone's phone gets different clues when it does, so wait until the whole group has joined.
            </p>
            <ErrorText>{error}</ErrorText>
            {intro && <RoomReveal view={stage} mode="intro" sound={sound} duck={duck} onDone={start} />}
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

/**
 * The room while the clock runs. On a TV or a laptop (TV_LAYOUT) it's the whole room on one screen that never
 * scrolls (#116): nobody works the TV during a game. On a phone (someone watching) it's the stacked page.
 */
function Room({ stage, info, invoke, tv, controls }: { stage: EscapeStageView; info: PartyInfo; invoke: Invoke; tv: boolean; controls: ReactNode }) {
  const [error, setError] = useState<string | null>(null)
  const hint = (p: EscapePuzzleView) => {
    if (!confirm(`A hint for "${p.title}" costs ${penaltyLabel(stage.hintPenaltySeconds)} of your time. Take it?`)) return
    setError(null)
    invoke('EscapeHostHint', info.code, p.id).catch((e: Error) => setError(e.message))
  }
  // A puzzle someone has taken and left (#132): the host can put it back on the table from the TV.
  const free = (p: EscapePuzzleView) => {
    if (!confirm(`Free "${p.title}" from ${p.heldBy?.name ?? 'its holder'}? Anyone can then take it.`)) return
    setError(null)
    invoke('EscapeHostFree', info.code, p.id).catch((e: Error) => setError(e.message))
  }
  const host = info.isHost ? { hint, free } : undefined
  return tv ? <TvRoom stage={stage} host={host} error={error} controls={controls} /> : <StackedRoom stage={stage} host={host} error={error} />
}

/** What the host can do from the TV, on any puzzle. */
type HostActions = { hint: (p: EscapePuzzleView) => void; free: (p: EscapePuzzleView) => void }

const solvedSummary = (stage: EscapeStageView) => `${stage.solvedCount}/${stage.puzzleCount} solved · ${stage.hintsUsed} hint${stage.hintsUsed === 1 ? '' : 's'}`

/**
 * The TV layout: a header, then two columns filling the rest of the screen, nothing scrolling (#116).
 *
 * The right column starts with what the group carries and what it has read (#132: on a big TV these were squeezed
 * into the bottom of the left column and cut off), always in full, then the puzzles, scaled down to fit when a busy
 * stage needs it. The left column is the room itself: the scene, the game master, and two short lists, what's
 * happened (newest first) and what's been searched, which fade out at the bottom since they only grow.
 */
function TvRoom({ stage, host, error, controls }: { stage: EscapeStageView; host?: HostActions; error: string | null; controls: ReactNode }) {
  // Open puzzles first, then the ones waiting for an item, then the solved ones (shrunk): the TV's own order within each.
  const rank = (p: EscapePuzzleView) => (p.solved ? 2 : p.needs.length ? 1 : 0)
  const puzzles = [...stage.puzzles].sort((a, b) => rank(a) - rank(b))
  const fade = '[mask-image:linear-gradient(to_bottom,black_75%,transparent)]'
  return (
    <div className="flex h-full flex-col gap-4" data-testid="tv-room">
      <header className="flex shrink-0 items-start justify-between gap-6">
        <div className="min-w-0">
          <p className="text-xs tracking-[0.3em] text-accent uppercase">
            {stage.roomTitle} · Room {stage.stageNumber} of {stage.stageCount}
          </p>
          <h1 className="font-display mt-1 text-4xl 2xl:text-5xl">{stage.stage?.title}</h1>
          <p className="mt-1 line-clamp-2 max-w-5xl text-ink/80 2xl:text-lg">{stage.stage?.description}</p>
        </div>
        <div className="flex shrink-0 items-start gap-5">
          <div className="text-right" aria-label="Time left">
            <EscapeClock view={stage} large />
            <p className="text-xs text-muted 2xl:text-sm">{solvedSummary(stage)}</p>
          </div>
          <div className="flex flex-col items-end gap-2">{controls}</div>
        </div>
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)] gap-6">
        <div className="flex min-h-0 flex-col gap-4">
          <div className="min-h-0 flex-1">
            {/* Keyed on the stage, so the new room's picture fades in as the door opens. */}
            {stage.scene ? (
              <SceneView key={stage.stage?.id} scene={stage.scene} artUrl={stage.artUrl} feed={stage.feed} fit />
            ) : stage.artUrl ? (
              <Art key={stage.stage?.id} url={stage.artUrl} className="h-full" />
            ) : (
              <div className="h-full rounded-xl border border-line" style={{ background: backdrop(stage.soundscape) }} aria-hidden />
            )}
          </div>
          <div className="shrink-0">
            <GameMasterPanel gameMaster={stage.gameMaster} narration={stage.narration} />
          </div>
          <div className={`grid max-h-[26%] min-h-0 shrink-0 grid-cols-2 gap-6 overflow-hidden ${fade}`}>
            <Happened stage={stage} />
            {stage.scene && <SearchedList scene={stage.scene} className="min-h-0 space-y-1 text-sm 2xl:text-base" />}
          </div>
        </div>

        <div className="flex min-h-0 flex-col gap-3">
          <Found stage={stage} tv />
          <ErrorText>{error}</ErrorText>
          <LocksFound stage={stage} />
          <div className="min-h-0 flex-1" data-testid="tv-puzzles">
            {/* Scaled to fit on a TV (1080p and up). A busy stage on a small laptop scrolls this box, as a last resort. */}
            <FitToScreen>
              {/* Two newspaper-style columns: each card takes the height it needs, so short ones don't leave gaps. */}
              <div className="columns-2 gap-3 [&>*]:mb-3 [&>*]:break-inside-avoid">
                {puzzles.map((p) => (
                  <PuzzleCard key={p.id} puzzle={p} stage={stage} host={host} compact />
                ))}
              </div>
            </FitToScreen>
          </div>
        </div>
      </div>
    </div>
  )
}

/** The phone layout: everything stacked, for someone holding the screen (and scrolling it). */
function StackedRoom({ stage, host, error }: { stage: EscapeStageView; host?: HostActions; error: string | null }) {
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
          <p className="text-xs text-muted">{solvedSummary(stage)}</p>
        </div>
      </header>
      {/* Keyed on the stage, so the new room's picture fades in as the door opens. A stage with spots to search shows them on the picture instead. */}
      {stage.scene ? (
        <SceneView key={stage.stage?.id} scene={stage.scene} artUrl={stage.artUrl} feed={stage.feed} />
      ) : (
        <Art key={stage.stage?.id} url={stage.artUrl} className="aspect-[21/7] max-h-72" />
      )}
      {stage.stageNumber === 1 && (
        // The villain's welcome, read out as the clock starts.
        <blockquote className="max-w-3xl border-l-2 border-accent pl-4 text-lg text-ink/80 italic">{stage.intro}</blockquote>
      )}
      <p className="max-w-3xl text-lg text-ink/90">{stage.stage?.description}</p>
      <GameMasterPanel gameMaster={stage.gameMaster} narration={stage.narration} />
      <Found stage={stage} />

      <LocksFound stage={stage} />
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {stage.puzzles.map((p) => (
          <PuzzleCard key={p.id} puzzle={p} stage={stage} host={host} />
        ))}
      </div>
      <ErrorText>{error}</ErrorText>
      <Happened stage={stage} />
    </div>
  )
}

/**
 * One puzzle in front of the group: its prompt, what it needs, its clues on the phones, who's working on it (#132),
 * the hints taken, and the host's buttons. `compact` (the TV layout) shrinks a solved one to its title and outcome.
 */
function PuzzleCard({ puzzle: p, stage, host, compact = false }: { puzzle: EscapePuzzleView; stage: EscapeStageView; host?: HostActions; compact?: boolean }) {
  if (compact && p.solved)
    return (
      <article data-testid={`puzzle-${p.id}`} className="rounded-xl border border-green-600/40 bg-green-900/10 px-3 py-2">
        <p className="flex flex-wrap items-baseline gap-x-2">
          <span aria-hidden>✅</span>
          <span className="font-display text-lg">{p.title}</span>
          <span className="text-xs text-green-300">opened by {p.solvedBy}</span>
          {p.mark && <MarkBadge mark={p.mark} />}
        </p>
        <p className="mt-1 line-clamp-2 text-xs text-green-300/80">{p.solvedText}</p>
      </article>
    )
  const turns = stage.answering !== 'anyone' && p.kind !== 'search'
  return (
    <article
      data-testid={`puzzle-${p.id}`}
      className={`rounded-xl border ${compact ? 'p-3' : 'p-4'} ${p.solved ? 'border-green-600/60 bg-green-900/10' : p.needs.length ? 'border-line bg-surface/60 opacity-80' : p.final ? 'border-2 border-accent bg-accent/10' : 'border-accent/60 bg-surface'}`}
    >
      {p.final && !p.solved && <p className="text-xs font-semibold tracking-widest text-accent uppercase">🏁 The final lock</p>}
      <div className="flex items-start justify-between gap-2">
        <h2 className={`font-display ${compact ? 'text-lg 2xl:text-2xl' : 'text-xl'}`}>{p.title}</h2>
        <span className="shrink-0 text-lg" aria-hidden>
          {p.solved ? '✅' : p.needs.length ? '🔒' : p.final ? '🏁' : KIND_ICON[p.kind]}
        </span>
      </div>
      {!p.solved && turns && (
        <p className="mt-1 text-sm" data-testid={`holder-${p.id}`}>
          {p.heldBy ? (
            <span className="rounded-full border border-accent/50 bg-accent/10 px-2 py-0.5">
              🙋 {p.heldBy.name}
              {p.heldBy.free && <span className="text-muted"> · free to take over</span>}
            </span>
          ) : (
            <span className="text-muted">🙋 Nobody's on it yet</span>
          )}
        </p>
      )}
      <p className={`mt-2 text-sm text-ink/90 ${compact ? 'leading-snug 2xl:text-base' : 'leading-relaxed'}`}>{p.prompt}</p>
      {p.solved ? (
        <p className="mt-3 text-sm text-green-300">
          Opened by {p.solvedBy}. {p.solvedText} {p.mark && <MarkBadge mark={p.mark} />}
        </p>
      ) : (
        <>
          {p.final && <MarksSoFar stage={stage} />}
          {p.needs.length > 0 && <p className="mt-3 text-xs text-muted">Needs: {p.needs.join(', ')}</p>}
          {p.pieceCount > 0 && <p className="mt-3 text-xs text-accent">🧩 Clues on {p.pieceCount} phones: read them out!</p>}
          {p.piecesHidden > 0 && (
            <p className="mt-1 text-xs text-accent">
              🔎 {p.piecesHidden} more clue piece{p.piecesHidden === 1 ? '' : 's'} hidden somewhere in the room
            </p>
          )}
          {p.keysHidden > 0 && <p className="mt-1 text-xs text-accent">🔑 Its key is written on something in this room</p>}
          {p.finds && (
            <p className="mt-3 text-sm text-muted">
              Searched {p.finds.found} of {p.finds.total}
            </p>
          )}
          {p.switches && <SwitchGrid puzzle={p} readOnly />}
          {p.hints.map((h, i) => (
            <p key={i} className="mt-2 rounded-lg bg-bg/60 p-2 text-sm">
              💡 {h}
            </p>
          ))}
          {p.hintPending && <p className="mt-2 animate-pulse text-sm text-muted">💭 {stage.gameMaster?.name ?? 'The game master'} is thinking of a hint…</p>}
          {host && (
            <p className="mt-3 flex gap-3 text-xs text-muted">
              {p.hintsLeft > 0 && !p.hintPending && (
                <button className="underline hover:text-ink" onClick={() => host.hint(p)}>
                  Hint (−{penaltyLabel(stage.hintPenaltySeconds)})
                </button>
              )}
              {p.heldBy && (
                <button className="underline hover:text-ink" onClick={() => host.free(p)}>
                  Free it
                </button>
              )}
            </p>
          )}
        </>
      )}
    </article>
  )
}

/**
 * What the group carries, and what it has read (the notebook: clues written down when searching, looking closely or
 * putting things together). On the TV it heads the puzzles' column (#132), so it's never cut off: every item, on one
 * line each, and the newest notes.
 */
function Found({ stage, tv = false }: { stage: EscapeStageView; tv?: boolean }) {
  return (
    <section className={tv ? 'shrink-0 rounded-xl border border-line bg-surface/70 p-3' : 'space-y-3'} data-testid="found">
      <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">🎒 What you've found</h2>
      {stage.inventory.length === 0 ? (
        <p className="mt-1 text-sm text-muted">Nothing yet: search the room.</p>
      ) : (
        <ul className={`mt-2 text-sm 2xl:text-base ${tv ? 'grid grid-cols-2 gap-x-4 gap-y-1' : 'space-y-1'}`} data-testid="found-items">
          {stage.inventory.map((i) => (
            <li key={i.id} className={tv ? 'min-w-0 truncate' : ''} title={i.description}>
              <span className="text-ink">{i.name}</span> <span className="text-muted">— {i.description}</span>
            </li>
          ))}
        </ul>
      )}
      {stage.notebook.length > 0 && (
        <div className={tv ? 'mt-3' : ''}>
          <Notebook notes={stage.notebook} limit={tv ? 3 : undefined} clamp={tv} />
        </div>
      )}
    </section>
  )
}

/** What's happened, newest first: the live part of the room. */
function Happened({ stage }: { stage: EscapeStageView }) {
  return (
    <section aria-live="polite" className="min-h-0">
      <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">What's happened</h2>
      <ul className="mt-2 space-y-1 text-sm text-muted 2xl:text-base">
        {[...stage.feed].reverse().slice(0, 6).map((f) => (
          <li key={f.at + f.text}>{f.text}</li>
        ))}
      </ul>
    </section>
  )
}

function Ending({ stage, code, isHost }: { stage: EscapeStageView; code: string; isHost: boolean }) {
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
      <LeaderboardPanel roomId={stage.roomId} code={code} daily={stage.daily} minutes={stage.timeLimitMinutes} difficulty={stage.difficulty} />
      {isHost && (
        <RecapShare
          code={code}
          load={api.escapeRecap}
          blurb="A page with your time, the team and their photos, who opened what and when, and the game master's best lines. It never shows the answers, so it's safe to send to friends who haven't played."
          extra={(loaded, link) => <ShareCardButton page={loaded.page} link={link} />}
        />
      )}
      <EndingActions stage={stage} isHost={isHost} />
    </div>
  )
}

const actionClass = 'inline-flex min-h-11 items-center justify-center rounded-lg px-4 py-2 text-sm transition'

/**
 * The way out once the game is over. The host can run the same room again (same length and difficulty,
 * fresh puzzles) or pick another game; anyone else just goes home.
 */
function EndingActions({ stage, isHost }: { stage: EscapeStageView; isHost: boolean }) {
  const again = new URLSearchParams({ game: 'escape', room: stage.roomId, minutes: String(stage.timeLimitMinutes), difficulty: stage.difficulty })
  return (
    <nav className="mt-8 flex flex-wrap justify-center gap-3" aria-label="What next">
      {isHost && (
        <>
          <Link to={`/host/new?${again}`} className={`${actionClass} bg-accent font-semibold text-bg hover:brightness-110`}>
            🔁 Play this room again
          </Link>
          <Link to="/host/new?game=escape" className={`${actionClass} border border-line bg-surface hover:border-accent`}>
            🗝️ Host another game
          </Link>
        </>
      )}
      <Link to="/" className={`${actionClass} text-muted underline hover:text-ink`}>
        Home
      </Link>
    </nav>
  )
}
