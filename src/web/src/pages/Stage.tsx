import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { CuePlayer } from '../components/CuePlayer'
import { FitToScreen } from '../components/FitToScreen'
import { GuideButton } from '../components/Guide'
import { nextAction, nextSpeaker, spotlightTime, turnSecondsLeft, useHostCall, type Invoke } from '../components/HostControls'
import { CheerBar, CheerOverlay } from '../components/Cheers'
import { Portrait } from '../components/Portrait'
import { RecapShare } from '../components/RecapShare'
import { ClueCard, Countdown, FeedToasts, QrCode } from '../components/Scene'
import { Button, ErrorText, StatusPill } from '../components/ui'
import { WatchersPanel } from '../components/WatchersPanel'
import { api } from '../lib/api'
import { stageGuide } from '../lib/guide'
import { useCheers } from '../lib/cheers'
import { NpcTypingContext, useJobUpdates, useParty } from '../lib/hub'
import { NpcAnswer } from '../components/NpcAnswer'
import { UnsupportedGame } from '../components/UnsupportedGame'
import { EscapeStage } from '../escape/EscapeStage'
import { seats, type WatchingAs } from '../lib/seats'
import { narrator } from '../lib/speech'
import { useThemePalette, useThemes } from '../lib/theme'
import { useBackgroundMusic } from '../lib/useBackgroundMusic'
import { TV_LAYOUT, useMediaQuery } from '../lib/useMediaQuery'
import { useSoundscape } from '../lib/useSoundscape'
import type { InterrogationView, MediaJob, PartyInfo, SpotlightView, StageView } from '../lib/types'


/**
 * The shared screen: the TV at a dinner party, or the tab the host shares on a
 * video call. The host gets controls along the bottom; guests who open it on
 * their own device (useful on video calls) just watch.
 */
export default function Stage() {
  const code = (useParams().code ?? '').toUpperCase()
  const [info, setInfo] = useState<PartyInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.party(code).then(setInfo, (e: Error) => setError(e.message))
  }, [code])

  const guestSeat = seats.mine(code)
  if (error) return <Centered>{error}</Centered>
  if (!info) return <Centered>Opening the manor doors…</Centered>
  if (!info.isHost && !guestSeat) {
    // Anyone else watches through /watch, as themselves (#112).
    return (
      <Centered>
        <p className="mb-4">This screen is for the host and the guests.</p>
        <div className="flex flex-wrap justify-center gap-4">
          <Link className="text-accent underline" to={`/join/${code}`}>
            Join this party
          </Link>
          {info.allowSpectators && info.status !== 'finished' && (
            <Link className="text-accent underline" to={`/watch/${code}`}>
              Just watch
            </Link>
          )}
        </div>
      </Centered>
    )
  }
  const token = info.isHost ? undefined : guestSeat?.token
  if (info.kind === 'escapeRoom') return <EscapeStage info={info} token={token} />
  if (info.kind !== 'mystery') return <UnsupportedGame kind={info.kind} />
  return <StageScreen info={info} token={token} />
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="grid min-h-dvh place-items-center p-6 text-center text-muted">{children}</div>
}

/** The mystery's TV. `watcher` is set when it's someone watching on their own phone (#112), not the TV itself. */
export function StageScreen({ info, token, watcher }: { info: PartyInfo; token?: string; watcher?: WatchingAs }) {
  const cheers = useCheers()
  // Bumped whenever someone starts or stops watching, so the host's list refreshes.
  const [audience, setAudience] = useState(0)
  const { stage, status, fatal, invoke, typing } = useParty({
    code: info.code,
    token,
    watchStage: true,
    onRemoved: watcher?.onRemoved,
    onCheer: cheers.add,
    onAudience: info.isHost ? () => setAudience((n) => n + 1) : undefined,
  })
  const [begun, setBegun] = useState(false)
  const [muted, setMuted] = useState(false)
  // A TV-sized screen gets a frame that fills it and never scrolls (#129); phones and small windows scroll as a page.
  const tv = useMediaQuery(TV_LAYOUT)
  useThemePalette(info.themeSlug)
  useSpeakNewAnswers(stage?.interrogations ?? [], begun && !muted, stage?.ai.voices ?? false)
  useSpeakNpcSpotlight(stage, begun && !muted)
  // The host's background music, softer while a scene plays (stage.cues is what the scene plays now). Without it, the
  // mystery's made-up background sound (its theme's, or its own, or the act's) plays the same way.
  const scene = (stage?.cues.length ?? 0) > 0
  const music = useBackgroundMusic(stage?.musicUrl ?? null, begun && !muted, scene)
  useSoundscape(stage && !stage.musicUrl ? stage.soundscape : 'silence', begun && !muted, scene)

  // Keep the TV or laptop from going to sleep mid-mystery.
  useEffect(() => {
    if (!begun || !('wakeLock' in navigator)) return
    let lock: WakeLockSentinel | undefined
    const request = () => navigator.wakeLock.request('screen').then((l) => (lock = l), () => {})
    void request()
    const onVisible = () => document.visibilityState === 'visible' && void request()
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      document.removeEventListener('visibilitychange', onVisible)
      void lock?.release()
    }
  }, [begun])

  if (fatal)
    return (
      <Centered>
        {watcher ? (
          <div>
            <p className="font-display text-2xl text-ink">You're no longer watching this party.</p>
            <Button className="mt-4" onClick={watcher.onLeave}>
              Back home
            </Button>
          </div>
        ) : (
          fatal
        )}
      </Centered>
    )
  if (!stage) return <Centered>Lighting the candles…</Centered>

  // Browsers only allow sound after the user interacts with the page, so the
  // stage starts with one big button. Tapping it unlocks narration and music.
  if (!begun && stage.phase !== 'lobby') {
    return (
      <div className="grain grid min-h-dvh place-items-center p-6 text-center">
        <div>
          <p className="text-xs tracking-[0.3em] text-accent uppercase">{stage.scenario.era}</p>
          <h1 className="font-display mt-3 text-5xl sm:text-7xl">{stage.scenario.title}</h1>
          <Button
            className="mt-10 px-10 py-4 text-lg"
            onClick={() => {
              narrator.speak(' ', null, false)
              setBegun(true)
            }}
          >
            Tap to begin the evening
          </Button>
          <p className="mt-4 text-sm text-muted">Turn the sound up. On a video call, share this tab with audio.</p>
        </div>
      </div>
    )
  }

  return (
    <NpcTypingContext.Provider value={typing}>
      <div
        className={`grain flex flex-col ${tv ? 'h-dvh overflow-hidden' : 'min-h-dvh'}`}
        data-layout={tv ? 'tv' : 'page'}
        data-soundscape={stage.musicUrl ? 'music' : stage.soundscape}
      >
        <StatusPill status={status} />
        <TopBar
          stage={stage}
          info={info}
          muted={muted}
          onMute={() => setMuted((m) => !m)}
          watchers={tv && info.isHost ? <WatchersPanel code={info.code} refresh={audience} variant="chip" /> : null}
        />
        {/* On the TV the phase gets exactly the space between the bars, and fits itself into it. */}
        <main
          className={
            tv
              ? `min-h-0 w-full flex-1 px-8 pt-2 xl:px-12 ${watcher ? 'pb-28' : 'pb-4'}`
              : `mx-auto w-full max-w-6xl flex-1 px-4 pt-4 sm:px-8 ${watcher ? 'pb-40' : 'pb-32'}`
          }
        >
          {(() => {
            const phase = (
              <PhaseView
                stage={stage}
                info={info}
                invoke={invoke}
                muted={muted}
                begun={begun || stage.phase === 'lobby'}
                soundOn={begun}
                tv={tv}
                onBegin={() => {
                  narrator.speak(' ', null, false)
                  setBegun(true)
                }}
              />
            )
            // Keyed on the phase, so a new phase measures afresh rather than starting at the last one's scale.
            return tv ? <FitToScreen key={`${stage.phase}-${stage.actNumber}-${stage.actStep}`}>{phase}</FitToScreen> : phase
          })()}
          {info.isHost && !tv && (
            <div className="mt-10">
              <WatchersPanel code={info.code} refresh={audience} open={stage.phase === 'lobby'} />
            </div>
          )}
        </main>
        {info.isHost && <HostBar stage={stage} info={info} invoke={invoke} tv={tv} />}
        <FeedToasts feed={stage.feed} offset="top-20" />
        <CheerOverlay cheers={cheers.cheers} />
        {watcher && <CheerBar invoke={invoke} name={watcher.name} onLeave={watcher.onLeave} />}
        <audio ref={music} loop hidden data-testid="background-music" />
      </div>
    </NpcTypingContext.Provider>
  )
}

function TopBar({
  stage,
  info,
  muted,
  onMute,
  watchers,
}: {
  stage: StageView
  info: PartyInfo
  muted: boolean
  onMute: () => void
  watchers?: React.ReactNode
}) {
  return (
    <header className="flex items-center justify-between gap-4 px-4 py-3 sm:px-8">
      <div className="min-w-0">
        <p className="truncate text-xs tracking-widest text-accent uppercase">
          {stage.phase === 'act' ? `Act ${stage.actNumber} of ${stage.actCount}` : stage.scenario.place}
        </p>
        <p className="font-display truncate text-lg">{stage.scenario.title}</p>
      </div>
      <div className="flex items-center gap-3">
        {stage.phase === 'act' && stage.actStep === 'mingle' && <Countdown timer={stage.timer} />}
        {watchers}
        <GuideButton guide={stageGuide(stage, info.isHost)} phase={stage.phase} />
        <span className="hidden rounded-md border border-line px-2 py-1 font-mono text-sm tracking-widest text-accent sm:inline">{info.code}</span>
        <button onClick={onMute} className="rounded-md border border-line px-2 py-1 text-sm text-muted hover:text-ink" aria-label={muted ? 'Unmute' : 'Mute'}>
          {muted ? '🔇' : '🔊'}
        </button>
      </div>
    </header>
  )
}

function PhaseView({
  stage,
  info,
  invoke,
  muted,
  begun,
  soundOn,
  tv,
  onBegin,
}: {
  stage: StageView
  info: PartyInfo
  invoke: Invoke
  muted: boolean
  begun: boolean
  soundOn: boolean
  /** The TV layout: everything on one screen (#129). */
  tv: boolean
  onBegin: () => void
}) {
  switch (stage.phase) {
    case 'lobby':
      return <LobbyView stage={stage} info={info} invoke={invoke} soundOn={soundOn} onEnableSound={onBegin} tv={tv} />
    case 'castReveal':
      return <CastView stage={stage} />
    case 'prologue':
      return (
        <Cinematic tv={tv}>
          <CuePlayer cues={stage.cues} runKey="prologue" muted={muted} enabled={begun} />
        </Cinematic>
      )
    case 'act':
      return stage.actStep === 'cinematic' ? (
        <div className="space-y-4">
          <h2 className="font-display text-center text-3xl sm:text-4xl">{stage.actTitle}</h2>
          <Cinematic tv={tv} titled>
            <CuePlayer cues={stage.cues} runKey={`act-${stage.actNumber}`} muted={muted} enabled={begun} />
          </Cinematic>
        </div>
      ) : (
        <MingleView stage={stage} tv={tv} />
      )
    case 'accusation':
      return <AccusationView stage={stage} />
    case 'reveal':
      return <RevealView stage={stage} muted={muted} begun={begun} tv={tv} />
    case 'awards':
      return <AwardsView stage={stage} tv={tv} />
    case 'finished': {
      const after = info.isHost && (
        <>
          <RecapShare code={info.code} load={api.recap} blurb="A page with the cast, the solution, everyone's secrets, the scores and the costume photos." />
          <p className="mt-6 text-center">
            <Link to="/host/new" className="inline-flex min-h-11 items-center justify-center rounded-lg border border-line bg-surface px-4 py-2 text-sm hover:border-accent">
              🎭 Host another game
            </Link>
          </p>
        </>
      )
      return tv ? (
        <AwardsView stage={stage} tv after={after} />
      ) : (
        <>
          <AwardsView stage={stage} tv={false} />
          {after}
        </>
      )
    }
  }
}

/**
 * A scene on the TV: as wide as the screen allows while its 16:9 picture still fits between the bars, so a wide TV
 * shows it big without it running off the bottom (#129). `titled` leaves room for the act's title above it.
 */
function Cinematic({ tv, titled = false, children }: { tv: boolean; titled?: boolean; children: React.ReactNode }) {
  if (!tv) return <>{children}</>
  return <div className={`mx-auto w-full ${titled ? 'max-w-[calc((100dvh-19rem)*16/9)]' : 'max-w-[calc((100dvh-15rem)*16/9)]'}`}>{children}</div>
}

// ------------------------------------------------------------------ lobby

function LobbyView({
  stage,
  info,
  invoke,
  soundOn,
  onEnableSound,
  tv,
}: {
  stage: StageView
  info: PartyInfo
  invoke: Invoke
  soundOn: boolean
  onEnableSound: () => void
  tv: boolean
}) {
  const joinUrl = `${window.location.origin}/join/${info.code}`
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const hostSeat = seats.mine(info.code)

  const addSeat = async (isLocal: boolean) => {
    const name = prompt(isLocal ? 'Name of the guest sharing this device:' : 'Your name as a player:')
    if (!name?.trim()) return
    setAdding(true)
    setError(null)
    try {
      const seat = await api.addSeat(info.code, name.trim(), isLocal)
      const stored = { seatId: seat.seatId, token: seat.token, name: name.trim() }
      if (isLocal) seats.addLocal(info.code, stored)
      else seats.setMine(info.code, stored)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setAdding(false)
    }
  }

  return (
    <div className={`grid gap-8 ${tv ? 'grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]' : 'lg:grid-cols-[1fr_1.2fr]'}`}>
      {stage.tailoring && (
        // Says nothing about who: once the AI is done, one of the guests is the killer either way.
        <div className="candle rounded-2xl border-2 border-accent bg-accent/10 p-5 text-center lg:col-span-2" role="status">
          <p className="font-display text-2xl">✨ Tailoring tonight's mystery to your cast…</p>
          <p className="mt-1 text-sm text-muted">The Storyteller is rewriting the story around the characters you chose. This takes about a minute.</p>
        </div>
      )}
      <section className="space-y-5">
        <div>
          <p className="text-xs tracking-[0.3em] text-accent uppercase">{stage.scenario.era}</p>
          <h1 className={`font-display mt-2 text-4xl leading-tight ${tv ? '2xl:text-5xl' : 'sm:text-5xl'}`}>{stage.scenario.title}</h1>
          <p className={`mt-3 text-muted ${tv ? 'line-clamp-4' : ''}`}>{stage.scenario.synopsis}</p>
        </div>
        <div className="flex flex-col items-center gap-4 rounded-2xl border border-accent/40 bg-surface p-6 sm:flex-row">
          <QrCode url={joinUrl} size={tv ? 150 : 180} />
          <div className="text-center sm:text-left">
            <p className="text-sm text-muted">Scan to join, or visit</p>
            <p className="font-medium break-all">{window.location.host}/join</p>
            <p className="mt-3 text-sm text-muted">and enter the code</p>
            <p className="font-mono text-5xl tracking-[0.2em] text-accent">{info.code}</p>
          </div>
        </div>
        {info.isHost && (
          <div className="flex flex-wrap gap-2">
            {/* Browsers only play sound after a tap on this screen. Doing it now means the host can
                start the evening from the phone remote without walking back to the TV. */}
            {!soundOn && (
              <Button variant="ghost" onClick={onEnableSound}>
                🔊 Enable sound on this screen
              </Button>
            )}
            {!hostSeat && (
              <Button variant="ghost" disabled={adding} onClick={() => addSeat(false)}>
                I'm playing too
              </Button>
            )}
            {hostSeat && (
              <Link to={`/play/${info.code}`} target="_blank" className="inline-flex min-h-11 items-center rounded-lg border border-line px-4 text-sm hover:border-accent">
                Open my dossier ↗
              </Link>
            )}
            <Button variant="ghost" disabled={adding} onClick={() => addSeat(true)}>
              Add a pass-and-play guest
            </Button>
            {seats.local(info.code).length > 0 && (
              <Link to={`/pass/${info.code}`} className="inline-flex min-h-11 items-center rounded-lg border border-line px-4 text-sm hover:border-accent">
                Pass-and-play dossiers
              </Link>
            )}
          </div>
        )}
        <ErrorText>{error}</ErrorText>
        {info.isHost && <MediaPanel code={info.code} />}
        {info.isHost && <KitPanel code={info.code} dealLater={info.dealAtStart} />}
        {stage.options.drinkingPrompts && <Cocktails themeSlug={stage.scenario.themeSlug} />}
      </section>

      <section>
        <h2 className="font-display mb-3 text-2xl">
          The suspects <span className="text-base text-muted">({stage.players.length} of up to {stage.scenario.maxPlayers} guests)</span>
        </h2>
        <div className={`grid gap-3 sm:grid-cols-2 ${tv ? '2xl:grid-cols-3' : ''}`}>
          {stage.cast.map((c) => {
            const player = stage.players.find((p) => p.characterId === c.characterId)
            return (
              <div key={c.characterId} className={`flex gap-3 rounded-xl border p-3 ${player ? 'border-accent/60 bg-surface' : 'border-line bg-surface/60'}`}>
                <Portrait id={c.characterId} name={c.name} src={c.portrait} size={52} dim={!player} />
                <div className="min-w-0">
                  <p className="font-display leading-tight">{c.name}</p>
                  <p className="text-xs text-muted">{c.title}</p>
                  <p className="mt-1 text-xs">
                    {player ? (
                      <span className="inline-flex items-center gap-1.5 text-accent">
                        <GuestPhoto url={player.photoUrl} name={player.name} />
                        {player.name}
                        {player.ready ? ' ✓' : ''}
                      </span>
                    ) : (
                      <span className="text-muted">{c.required ? 'Open. NPC if nobody takes it' : 'Open (optional)'}</span>
                    )}
                  </p>
                </div>
              </div>
            )
          })}
        </div>
        {stage.players.some((p) => !p.characterId) && (
          <p className="mt-3 text-sm text-muted">
            Still choosing: {stage.players.filter((p) => !p.characterId).map((p) => p.name).join(', ')}
          </p>
        )}
        {info.isHost && stage.players.length > 0 && (
          <details className="mt-4 rounded-xl border border-line p-3 text-sm">
            <summary className="cursor-pointer text-muted">Manage guests</summary>
            <ul className="mt-3 space-y-2">
              {stage.players.map((p) => (
                <li key={p.seatId} className="flex items-center justify-between gap-2">
                  <span>
                    {p.name} {p.isLocal && <span className="text-xs text-muted">(pass & play)</span>} {p.isHost && <span className="text-xs text-muted">(host)</span>}
                  </span>
                  <span className="flex gap-2">
                    <select
                      className="rounded border border-line bg-bg px-2 py-1 text-xs"
                      value={p.characterId ?? ''}
                      onChange={(e) => invoke('AssignCharacter', info.code, p.seatId, e.target.value || null).catch((err: Error) => setError(err.message))}
                    >
                      <option value="">No character yet</option>
                      {stage.cast.map((c) => (
                        <option key={c.characterId} value={c.characterId}>
                          {c.name}
                        </option>
                      ))}
                    </select>
                    <button
                      className="text-xs text-red-300 underline"
                      onClick={() => confirm(`Remove ${p.name}?`) && invoke('RemoveSeat', info.code, p.seatId).catch((err: Error) => setError(err.message))}
                    >
                      Remove
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </details>
        )}
      </section>
    </div>
  )
}

// ------------------------------------------------------------------ in play

function CastView({ stage }: { stage: StageView }) {
  return (
    <div className="space-y-6">
      <div className="text-center">
        <h1 className="font-display text-4xl sm:text-5xl">The suspects</h1>
        <p className="mt-2 text-muted">Everyone: read your dossier now. Then take turns introducing your character.</p>
      </div>
      <SpotlightBanner stage={stage} />
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-4">
        {stage.cast.map((c) => (
          <div key={c.characterId} className="flex flex-col items-center rounded-xl border border-line bg-surface p-4 text-center">
            <Portrait id={c.characterId} name={c.name} src={c.portrait} size={110} />
            <p className="font-display mt-3 text-lg leading-tight">{c.name}</p>
            <p className="text-xs text-accent">{c.title}</p>
            <p className="mt-1 inline-flex items-center gap-1.5 text-xs text-muted">
              {!c.isNpc && <GuestPhoto url={stage.players.find((p) => p.characterId === c.characterId)?.photoUrl ?? null} name={c.playedBy ?? ''} size={28} />}
              {c.isNpc ? 'Played by the narrator' : `Played by ${c.playedBy}`}
            </p>
          </div>
        ))}
      </div>
      <div className="rounded-xl border border-line bg-surface/70 p-5 text-center">
        <p className="text-sm text-muted">The victim</p>
        <p className="font-display text-2xl">{stage.scenario.victimName}</p>
        <p className="mx-auto mt-1 max-w-2xl text-sm text-muted">{stage.scenario.victimDescription}</p>
      </div>
    </div>
  )
}

/**
 * On the TV, how much of each list the evening shows: the newest. Every phone keeps the whole of each (#129). A short
 * screen (a 720p TV) has room for one row of clue cards, a taller one for two.
 */
const TV_CLUES = { roomy: 4, short: 2 }
const TV_SECRETS = 3
const ROOMY = '(min-height: 860px)'

function MingleView({ stage, tv }: { stage: StageView; tv: boolean }) {
  const [promptIndex, setPromptIndex] = useState(0)
  useEffect(() => {
    if (stage.prompts.length < 2) return
    const id = setInterval(() => setPromptIndex((i) => (i + 1) % stage.prompts.length), 25_000)
    return () => clearInterval(id)
  }, [stage.prompts.length])

  const roomy = useMediaQuery(ROOMY)
  const clues = [...stage.clues].reverse()
  // The TV shows the newest evidence in full and names the rest; the phones' Clues tab has every one.
  const cards = roomy ? TV_CLUES.roomy : TV_CLUES.short
  const shown = tv ? clues.slice(0, cards) : clues
  const earlier = tv ? clues.slice(cards) : []
  const secrets = tv ? [...stage.revealedSecrets].reverse().slice(0, TV_SECRETS) : stage.revealedSecrets
  const moreSecrets = stage.revealedSecrets.length - secrets.length
  return (
    <div className={tv ? 'space-y-4' : 'space-y-6'}>
      <div className="text-center">
        <p className="text-xs tracking-[0.3em] text-accent uppercase">{stage.actTitle}</p>
        <div className="mt-2 flex justify-center">
          <Countdown timer={stage.timer} large />
        </div>
        <SpotlightBanner stage={stage} />
        {/* While someone has the floor, their question card is the prompt. */}
        {!stage.spotlight && stage.prompts.length > 0 && (
          <p key={promptIndex} className={`font-display mx-auto max-w-3xl text-2xl text-ink/90 italic sm:text-3xl ${tv ? 'mt-2 line-clamp-2' : 'mt-4'}`}>
            “{stage.prompts[promptIndex % stage.prompts.length]}”
          </p>
        )}
      </div>
      <div className={`grid gap-6 ${tv ? 'grid-cols-[minmax(0,2fr)_minmax(0,1fr)]' : 'lg:grid-cols-[2fr_1fr]'}`}>
        <section>
          <h2 className="font-display mb-3 text-2xl">Evidence</h2>
          {clues.length === 0 ? (
            <p className="text-muted">No evidence has come to light yet.</p>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2">
              {shown.map((c) => (
                <ClueCard key={c.id} clue={c} compact={tv} />
              ))}
            </div>
          )}
          {earlier.length > 0 && (
            <p className="mt-3 text-sm text-muted" data-testid="earlier-evidence">
              <span className="text-ink/80">Earlier evidence:</span> {earlier.map((c) => c.title).join(' · ')}. Every clue is on your phones.
            </p>
          )}
        </section>
        <section className={tv ? 'space-y-5' : 'space-y-8'}>
          <SuspicionMeter stage={stage} />
          {stage.ai.npcQuestions && stage.cast.some((c) => c.isNpc) && <InterrogationRoom stage={stage} latest={tv ? 2 : 4} />}
          {stage.options.drinkingPrompts && <Cocktails themeSlug={stage.scenario.themeSlug} />}
          <div>
            <h2 className="font-display mb-3 text-2xl">Secrets exposed</h2>
            {secrets.length === 0 ? (
              <p className="text-sm text-muted">Nobody has confessed anything… yet.</p>
            ) : (
              <ul className="space-y-3">
                {secrets.map((s) => (
                  <li key={s.text} className="rounded-xl border border-line bg-surface p-3 text-sm">
                    <span className="text-accent">{s.characterName}</span>
                    <p className="mt-1">{s.text}</p>
                  </li>
                ))}
              </ul>
            )}
            {moreSecrets > 0 && (
              <p className="mt-2 text-xs text-muted">
                …and {moreSecrets} more. Every secret is on your phones.
              </p>
            )}
          </div>
        </section>
      </div>
    </div>
  )
}

/** The latest questions guests put to NPCs, with the characters' answers. */
function InterrogationRoom({ stage, latest: count }: { stage: StageView; latest: number }) {
  const latest = [...stage.interrogations].filter((i) => i.act === stage.actNumber).reverse().slice(0, count)
  return (
    <div>
      <h2 className="font-display mb-1 text-2xl">The interrogation room</h2>
      <p className="mb-3 text-xs text-muted">
        Question {stage.cast.filter((c) => c.isNpc).map((c) => c.name).join(', ')} from your phone ({stage.ai.questionsPerAct} per guest per act).
      </p>
      {latest.length === 0 ? (
        <p className="text-sm text-muted">No one has been questioned yet this act.</p>
      ) : (
        <ul className="space-y-3">
          {latest.map((i) => (
            <li key={i.id} className="rounded-xl border border-line bg-surface p-3 text-sm">
              <p className="text-muted">
                {i.askerName} → <span className="text-accent">{i.characterName}</span>: “{i.question}”
              </p>
              <NpcAnswer interrogation={i} placeholder="Thinking…" className="font-display mt-2 text-lg leading-snug" />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/**
 * Reads each newly answered NPC question aloud. When a Voice provider is set up,
 * the recorded clip usually arrives a moment after the text, so wait briefly for
 * it before falling back to the browser's own voice.
 */
function useSpeakNewAnswers(interrogations: InterrogationView[], enabled: boolean, voices: boolean) {
  const spoken = useRef<Set<string> | null>(null)
  const waiting = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map())
  const latest = useRef(interrogations)
  useEffect(() => {
    latest.current = interrogations
  }, [interrogations])

  useEffect(() => {
    const answered = interrogations.filter((i) => i.answer)
    // The first time we see the list, treat existing answers as already heard.
    if (spoken.current === null) {
      spoken.current = new Set(answered.map((i) => i.id))
      return
    }
    const heard = spoken.current
    const say = (i: InterrogationView) => {
      heard.add(i.id)
      clearTimeout(waiting.current.get(i.id))
      waiting.current.delete(i.id)
      if (!enabled) return
      if (i.audioUrl) void new Audio(i.audioUrl).play().catch(() => narrator.speak(`${i.characterName}: ${i.answer}`, i.voice))
      else void narrator.speak(`${i.characterName}: ${i.answer}`, i.voice)
    }
    for (const i of answered) {
      if (heard.has(i.id)) continue
      if (i.audioUrl || !voices) say(i)
      else if (!waiting.current.has(i.id)) {
        waiting.current.set(
          i.id,
          setTimeout(() => {
            const current = latest.current.find((x) => x.id === i.id) ?? i
            if (!heard.has(i.id)) say(current)
          }, 8000),
        )
      }
    }
  }, [interrogations, enabled, voices])
}

function AccusationView({ stage }: { stage: StageView }) {
  const progress = stage.accusation
  return (
    <div className="space-y-8 py-6 text-center">
      <h1 className="font-display text-4xl sm:text-6xl">
        Who killed <span className="text-accent italic">{stage.scenario.victimName}</span>?
      </h1>
      <p className="text-lg text-muted">Lock in your accusation on your phone: who, why, and how.</p>
      {progress && (
        <p className="font-display text-3xl">
          {progress.submitted} of {progress.total} accusations locked in
        </p>
      )}
      <div className="flex flex-wrap justify-center gap-3">
        {stage.players.map((p) => (
          <span key={p.seatId} className={`rounded-full border px-4 py-2 text-sm ${p.hasAccused ? 'border-accent text-accent' : 'border-line text-muted'}`}>
            {p.hasAccused ? '✓ ' : ''}
            {p.name}
          </span>
        ))}
      </div>
    </div>
  )
}

function RevealView({ stage, muted, begun, tv }: { stage: StageView; muted: boolean; begun: boolean; tv: boolean }) {
  const r = stage.reveal!
  const final = r.step === r.stepCount - 1
  const murderer = stage.cast.find((c) => c.characterId === r.murdererId)

  // Read each new part of the reveal aloud as the host steps through it.
  const toSpeak = useMemo(() => {
    if (r.step === 1 && r.murdererName) return `The murderer was ${r.murdererName}.`
    if (r.step >= 2 && !final) return r.explanation[r.explanation.length - 1]
    return null
  }, [r.step, r.murdererName, r.explanation, final])
  useEffect(() => {
    if (toSpeak && begun && !muted) void narrator.speak(toSpeak)
    return () => narrator.stop()
  }, [toSpeak, begun, muted])

  if (final) {
    const timeline = (
      <section>
        <h2 className="font-display mb-3 text-2xl">What really happened</h2>
        <ol className={`border-l border-accent/50 pl-5 ${tv ? 'space-y-1 text-sm' : 'space-y-2'}`}>
          {r.timeline.map((t) => (
            <li key={t.time + t.event}>
              <span className="font-mono text-accent">{t.time}</span> <span className="text-ink/90">{t.event}</span>
            </li>
          ))}
        </ol>
      </section>
    )
    // On the TV the finale plays beside the scores and the timeline, rather than above them (#129).
    return tv ? (
      <div className="grid grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)] items-start gap-8">
        <CuePlayer cues={stage.cues} runKey="finale" muted={muted} enabled={begun} />
        <div className="space-y-6">
          <Scores stage={stage} compact />
          {timeline}
        </div>
      </div>
    ) : (
      <div className="space-y-8">
        <CuePlayer cues={stage.cues} runKey="finale" muted={muted} enabled={begun} />
        <Scores stage={stage} />
        {timeline}
      </div>
    )
  }

  return (
    <div className="space-y-8">
      {r.step === 0 ? (
        <section>
          <h1 className="font-display mb-6 text-center text-4xl">The accusations</h1>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {r.guesses.map((g) => (
              <div key={g.playerName} className="rounded-xl border border-line bg-surface p-4">
                <p className="text-sm text-muted">
                  {g.playerName}
                  {g.characterName ? ` as ${g.characterName}` : ''} accuses…
                </p>
                <p className="font-display mt-1 text-2xl">{g.suspectName ?? 'Nobody'}</p>
                {g.suspectName && (
                  <p className="mt-1 text-xs text-muted">
                    {g.motive}. {g.method}.
                  </p>
                )}
              </div>
            ))}
          </div>
        </section>
      ) : (
        <Unmasked stage={stage} murderer={murderer} tv={tv} />
      )}
    </div>
  )
}

/**
 * The killer unmasked, then the explanation paragraph by paragraph. On the TV (#129) they sit side by side: the killer
 * and who guessed right on the left; the Inspector's verdicts, then the explanation, on the right.
 */
function Unmasked({ stage, murderer, tv }: { stage: StageView; murderer: StageView['cast'][number] | undefined; tv: boolean }) {
  const r = stage.reveal!
  const killer = (
    <div className="flex flex-col items-center gap-4 text-center">
      <p className="text-xs tracking-[0.3em] text-accent uppercase">The murderer was</p>
      {murderer && <Portrait id={murderer.characterId} name={murderer.name} src={murderer.portrait} size={150} />}
      <h1 className="font-display text-5xl text-red-200 sm:text-6xl">{r.murdererName}</h1>
      <p className="text-muted">
        {r.motive}. {r.method}.
      </p>
      <div className="flex flex-wrap justify-center gap-2">
        {r.guesses.map((g) => (
          <span key={g.playerName} className={`rounded-full border px-3 py-1 text-sm ${g.correct ? 'border-accent text-accent' : 'border-line text-muted line-through'}`}>
            {g.playerName}
          </span>
        ))}
      </div>
    </div>
  )
  const verdicts = r.step === 1 && r.guesses.some((g) => g.verdict) && (
    <div className={`grid gap-3 text-left sm:grid-cols-2 ${tv ? '' : 'mt-2 max-w-4xl'}`}>
      {r.guesses
        .filter((g) => g.verdict)
        .map((g) => (
          <div key={g.playerName} className="rounded-xl border border-line bg-surface p-3 text-sm">
            <p className="text-xs tracking-widest text-accent uppercase">The Inspector on {g.playerName}</p>
            <p className="font-display mt-1 text-lg italic">{g.verdict}</p>
          </div>
        ))}
    </div>
  )
  const explanation = (
    <div className={`space-y-4 text-left ${tv ? '' : 'mt-4 max-w-3xl'}`}>
      {r.explanation.map((p, i) => (
        <p key={i} className={`font-display text-xl leading-relaxed ${i === r.explanation.length - 1 ? 'text-ink' : 'text-ink/60'}`}>
          {p}
        </p>
      ))}
    </div>
  )
  if (tv)
    return (
      <section className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] items-center gap-10">
        {killer}
        <div className="space-y-4">
          {verdicts}
          {explanation}
        </div>
      </section>
    )
  return (
    <section className="flex flex-col items-center gap-4 text-center">
      {killer}
      {verdicts}
      {explanation}
    </section>
  )
}

function Scores({ stage, compact = false }: { stage: StageView; compact?: boolean }) {
  const scores = stage.reveal?.scores ?? []
  return (
    <section>
      <h2 className="font-display mb-3 text-2xl">The detectives' scores</h2>
      <ol className={compact ? 'space-y-1.5' : 'space-y-2'}>
        {scores.map((s, i) => (
          <li key={s.seatId} className={`flex items-center justify-between gap-3 rounded-xl border border-line bg-surface ${compact ? 'px-3 py-2' : 'p-3'}`}>
            <span>
              <span className="font-display mr-3 text-accent">{i + 1}.</span>
              {s.playerName}
              {s.characterName && <span className="text-sm text-muted"> as {s.characterName}</span>}
              <span className="block text-xs text-muted">{s.breakdown.join(' · ')}</span>
            </span>
            <span className="font-display text-2xl">{s.points}</span>
          </li>
        ))}
      </ol>
    </section>
  )
}

function AwardsView({ stage, tv, after }: { stage: StageView; tv: boolean; after?: React.ReactNode }) {
  const a = stage.awards!
  if (!a.results) {
    return (
      <div className="space-y-6 py-10 text-center">
        <h1 className="font-display text-5xl">Awards</h1>
        <p className="text-lg text-muted">Vote on your phone for {a.awards.map((x) => x.title).join(' and ')}.</p>
        <p className="font-display text-3xl">
          {a.votesCast} of {a.voters} ballots cast
        </p>
      </div>
    )
  }
  const winners = (
    <>
      <h1 className="font-display text-5xl">And the winners are…</h1>
      <div className={`grid gap-4 ${tv ? 'sm:grid-cols-2' : 'sm:grid-cols-3'}`}>
        {a.bestDetective && (
          <Award title="Best Detective" winners={[a.bestDetective.playerName]} detail={`${a.bestDetective.points} points`} />
        )}
        {a.results.map((r) => (
          <Award key={r.awardId} title={r.title} winners={r.winners} detail={r.votes ? `${r.votes} vote${r.votes === 1 ? '' : 's'}` : 'No votes'} />
        ))}
      </div>
    </>
  )
  const thanks = <p className="font-display text-2xl text-muted italic">Thank you for a killer evening.</p>
  // On the TV the winners sit beside the scores (#129); the host's recap and "host another" go under the winners.
  if (tv)
    return (
      <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] items-start gap-10 py-2">
        <div className="space-y-6 text-center">
          {winners}
          {thanks}
          {after}
        </div>
        <Scores stage={stage} compact />
      </div>
    )
  return (
    <div className="space-y-8 py-6 text-center">
      {winners}
      <Scores stage={stage} />
      {thanks}
    </div>
  )
}

function Award({ title, winners, detail }: { title: string; winners: string[]; detail: string }) {
  return (
    <div className="rounded-2xl border border-accent/60 bg-surface p-6">
      <p className="text-xs tracking-widest text-accent uppercase">{title}</p>
      <p className="font-display mt-2 text-3xl">{winners.length ? winners.join(' & ') : '—'}</p>
      <p className="mt-1 text-sm text-muted">{detail}</p>
    </div>
  )
}

// ------------------------------------------------------------------ host controls

/** Remembered per party on this device, so the TV stays clean after a reload. */
const hiddenKey = (code: string) => `butler:hideControls:${code}`
function readHidden(code: string): boolean {
  try {
    return localStorage.getItem(hiddenKey(code)) === '1'
  } catch {
    return false
  }
}
function writeHidden(code: string, hidden: boolean) {
  try {
    if (hidden) localStorage.setItem(hiddenKey(code), '1')
    else localStorage.removeItem(hiddenKey(code))
  } catch {
    // Private browsing can block storage; the toggle then just lasts until reload.
  }
}

function HostBar({ stage, info, invoke, tv }: { stage: StageView; info: PartyInfo; invoke: Invoke; tv: boolean }) {
  const { call, busy, error } = useHostCall(info.code, invoke)
  const [hidden, setHidden] = useState(() => readHidden(info.code))
  const [showRemote, setShowRemote] = useState(false)
  const hide = (value: boolean) => {
    setHidden(value)
    writeHidden(info.code, value)
  }

  const next = nextAction(stage)
  const mingle = stage.phase === 'act' && stage.actStep === 'mingle'

  // With the remote in use, the TV shows only the show. A small corner button brings the controls back.
  if (hidden) {
    return (
      <button
        type="button"
        onClick={() => hide(false)}
        className="fixed right-3 bottom-3 z-30 rounded-full border border-line bg-bg/80 px-3 py-1 text-xs text-muted opacity-60 hover:opacity-100"
      >
        Show host controls
      </button>
    )
  }

  return (
    // On the TV it's the frame's bottom row, so the phase above gets the rest of the screen; on a page it floats.
    <div className={`z-30 border-t border-line bg-bg/95 px-4 py-3 backdrop-blur sm:px-8 ${tv ? 'shrink-0 xl:px-12' : 'fixed inset-x-0 bottom-0'}`}>
      {showRemote && <RemoteQr code={info.code} onClose={() => setShowRemote(false)} onHide={() => hide(true)} />}
      <div className={`mx-auto flex flex-wrap items-center justify-between gap-2 ${tv ? '' : 'max-w-6xl'}`}>
        <div className="flex flex-wrap items-center gap-2">
          <span className="mr-1 text-xs tracking-widest text-muted uppercase">Host</span>
          <Button variant="quiet" onClick={() => setShowRemote(true)}>
            📱 Use my phone as a remote
          </Button>
          {stage.phase === 'lobby' && !stage.tailoring && (
            <Button variant="ghost" disabled={busy} onClick={() => call('AutoAssign')}>
              Auto-assign characters
            </Button>
          )}
          {stage.tailoring && (
            <Button variant="ghost" disabled={busy} onClick={() => call('SkipTailoring')}>
              Start without it
            </Button>
          )}
          {spotlightTime(stage) && stage.cast.length > 0 && (
            <>
              <Button variant="ghost" disabled={busy} onClick={() => call('SpinSpotlight')}>
                🎲 Spin
              </Button>
              <Button variant="ghost" disabled={busy} onClick={() => call('Spotlight', nextSpeaker(stage))}>
                🎤 {stage.spotlight ? 'Next speaker' : 'Spotlight'}
              </Button>
            </>
          )}
          {stage.spotlight && (
            <Button variant="quiet" disabled={busy} onClick={() => call('Spotlight', null)}>
              Clear spotlight
            </Button>
          )}
          {mingle && (
            <>
              <Button variant="ghost" disabled={busy} onClick={() => call(stage.timer?.paused ? 'ResumeTimer' : 'PauseTimer')}>
                {stage.timer?.paused ? 'Resume' : 'Pause'}
              </Button>
              <Button variant="ghost" disabled={busy} onClick={() => call('ExtendTimer', 5)}>
                +5 min
              </Button>
              <Button variant="ghost" disabled={busy || stage.pendingClues === 0} onClick={() => call('DropNextClue')}>
                Drop next clue{stage.pendingClues ? ` (${stage.pendingClues})` : ''}
              </Button>
            </>
          )}
          {info.mode === 'passAndPlay' || seats.local(info.code).length > 0 ? (
            <Link to={`/pass/${info.code}`} className="text-sm text-muted underline hover:text-ink">
              Pass-and-play dossiers
            </Link>
          ) : null}
        </div>
        <div className="flex items-center gap-3">
          <ErrorText>{error}</ErrorText>
          {next && (
            <Button disabled={busy || next.disabled} onClick={() => call(next.method)} className="px-6">
              {next.label}
            </Button>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * A QR code for the host's phone. Showing it on the TV is safe: the remote needs the host's own
 * sign-in, so a guest who scans it only reaches the login page.
 */
function RemoteQr({ code, onClose, onHide }: { code: string; onClose: () => void; onHide: () => void }) {
  const url = `${window.location.origin}/remote/${code}`
  return (
    <div className="mx-auto mb-3 flex max-w-6xl flex-col items-center gap-4 rounded-xl border border-accent/60 bg-surface p-4 sm:flex-row" role="dialog" aria-label="Phone remote">
      <QrCode url={url} size={140} />
      <div className="text-sm">
        <p className="font-semibold">Run the evening from your phone</p>
        <p className="mt-1 text-muted">
          Scan this, or open <span className="break-all text-ink">{url}</span>, and sign in with your host account. Every button here is on your phone
          too, plus your to-do list for each moment.
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <Button onClick={onHide}>Hide the controls on this screen</Button>
          <Button variant="quiet" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>
    </div>
  )
}

/** Whose turn it is to speak, big enough to read across the room. */
function SpotlightBanner({ stage }: { stage: StageView }) {
  const s = stage.spotlight
  const now = useNow(1000)
  if (!s) return null
  const left = turnSecondsLeft(s.endsAt, now)
  return (
    <div className="mx-auto my-4 max-w-3xl rounded-2xl border-2 border-accent bg-accent/10 px-6 py-4 text-center" role="status">
      <p className="text-xs tracking-[0.3em] text-accent uppercase">
        {s.confrontation ? `⚖️ ${s.confrontation.accuserName} confronts` : '🎤 In the spotlight'}
        {left !== null && <span className={`ml-3 font-mono ${left <= 10 ? 'text-red-300' : 'text-muted'}`}>{formatTurn(left)}</span>}
      </p>
      <p className="font-display mt-1 text-3xl sm:text-4xl">
        {s.isNpc ? s.characterName : s.playerName}
        <span className="text-muted">{s.isNpc ? ' (played by the narrator)' : ` as ${s.characterName}`}</span>
      </p>
      {s.confrontation && (
        <div className="mx-auto mt-3 max-w-2xl rounded-xl border border-line bg-bg/60 p-3 text-left">
          <p className="text-xs tracking-widest text-accent uppercase">The evidence: {s.confrontation.clueTitle}</p>
          <p className="mt-1 text-sm">{s.confrontation.clueText}</p>
        </div>
      )}
      {s.isNpc && s.npcLine && <p className="font-display mx-auto mt-3 max-w-2xl text-xl italic">“{s.npcLine}”</p>}
      <p className="mx-auto mt-3 max-w-2xl text-lg">{s.question}</p>
      {s.isNpc && stage.ai.npcQuestions && stage.phase === 'act' && (
        <p className="mt-2 text-xs text-muted">Put the question to {s.characterName} from the Question tab on any phone. The whole room hears the answer.</p>
      )}
    </div>
  )
}

function formatTurn(seconds: number) {
  return seconds === 0 ? "time's up" : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

/** The current time, updated every `ms`, for countdowns that aren't driven by the server. */
function useNow(ms: number) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), ms)
    return () => clearInterval(id)
  }, [ms])
  return now
}

/**
 * When the host spotlights a character nobody is playing, the narrator speaks for them in their
 * own voice. Keyed on the character and the turn's end time, so each turn is spoken once.
 */
function useSpeakNpcSpotlight(stage: StageView | null, enabled: boolean) {
  const spot: SpotlightView | null | undefined = stage?.spotlight
  const key = spot?.isNpc ? `${spot.characterId}@${spot.endsAt}` : null
  const heard = useRef<string | null>(null)
  useEffect(() => {
    if (!key || !spot || key === heard.current) return
    heard.current = key
    if (!enabled) return
    const voice = stage?.cast.find((c) => c.characterId === spot.characterId)?.voice
    void narrator.speak([spot.npcLine, spot.confrontation ? spot.question : null].filter(Boolean).join(' '), voice)
  }, [key, spot, stage, enabled])
}

/** "Who looks guiltiest?": the room's running totals. Only totals: nobody's vote is shown. */
function SuspicionMeter({ stage }: { stage: StageView }) {
  const total = stage.suspicion.reduce((sum, s) => sum + s.votes, 0)
  if (total === 0) return null
  return (
    <section className="rounded-xl border border-line bg-surface p-4">
      <h2 className="font-display mb-2 text-xl">🔥 Who looks guiltiest?</h2>
      <ul className="space-y-2">
        {stage.suspicion.map((s) => (
          <li key={s.characterId}>
            <div className="flex justify-between text-sm">
              <span>{s.name}</span>
              <span className="text-muted">{s.votes}</span>
            </div>
            <div className="mt-1 h-2 rounded-full bg-bg">
              <div className="h-2 rounded-full bg-accent transition-all" style={{ width: `${(s.votes / total) * 100}%` }} />
            </div>
          </li>
        ))}
      </ul>
      <p className="mt-2 text-xs text-muted">Change your pick on your phone at any time. Only totals are shown.</p>
    </section>
  )
}

// ------------------------------------------------------------------ media helpers

function GuestPhoto({ url, name, size = 22 }: { url: string | null; name: string; size?: number }) {
  if (!url) return null
  return <img src={url} alt={`${name}'s costume`} width={size} height={size} className="rounded-full border border-accent/60 object-cover" style={{ width: size, height: size }} />
}

/** Host-only: progress of the AI voices and pictures for this mystery. */
function MediaPanel({ code }: { code: string }) {
  const [state, setState] = useState<{ ready: number; job: MediaJob | null } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const job = state?.job
  const running = !!job && (job.status === 'queued' || job.status === 'running')
  // While a job runs, re-fetch whenever the server signals progress, and every 15 seconds in case a signal is lost.
  const updates = useJobUpdates(running)
  useEffect(() => {
    let active = true
    const load = async () => {
      try {
        const next = await api.partyMedia(code)
        if (active) setState(next)
      } catch {
        /* no media features: hide the panel */
      }
    }
    void load()
    const fallback = running ? setInterval(load, 15_000) : undefined
    return () => {
      active = false
      clearInterval(fallback)
    }
  }, [code, running, updates])

  if (!state || (!job && state.ready === 0)) return null
  return (
    <div className="rounded-xl border border-line bg-surface p-4 text-sm">
      <p className="font-semibold">🎙️🎨 Voices & artwork</p>
      {running ? (
        <p className="candle mt-1 text-accent">
          Preparing… {job.done} of {job.total || '?'} done
        </p>
      ) : (
        <p className="mt-1 text-muted">
          {state.ready} voice clips and pictures ready{job?.failed ? `, ${job.failed} couldn't be made` : ''}.
        </p>
      )}
      {job?.error && !running && <p className="mt-1 text-xs text-red-300">{job.error}</p>}
      {!running && (
        <button
          className="mt-2 text-xs text-accent underline"
          onClick={async () => {
            setError(null)
            try {
              const started = await api.prepareMedia(code)
              setState((s) => ({ ready: s?.ready ?? 0, job: started }))
            } catch (e) {
              setError((e as Error).message)
            }
          }}
        >
          Fill in anything missing
        </button>
      )}
      <ErrorText>{error}</ErrorText>
    </div>
  )
}

/** Host-only: printable PDFs for an in-person party. */
function KitPanel({ code, dealLater }: { code: string; dealLater: boolean }) {
  const links: { kind: 'invitations' | 'booklets' | 'nametags' | 'clues'; label: string; note?: string }[] = [
    { kind: 'invitations', label: 'Invitations' },
    { kind: 'nametags', label: 'Name tags' },
    // With "Surprise me" the killer is only dealt when the evening begins, so these can't exist yet.
    ...(dealLater
      ? []
      : [
          { kind: 'booklets' as const, label: 'Character booklets', note: 'every secret' },
          { kind: 'clues' as const, label: 'Clue cards + sealed solution', note: 'spoilers' },
        ]),
  ]
  return (
    <details className="rounded-xl border border-line bg-surface p-4 text-sm">
      <summary className="cursor-pointer font-semibold">🖨️ Printable party kit</summary>
      <p className="mt-2 text-xs text-muted">For in-person parties. Booklets and clue cards contain spoilers, so if you're playing too, print them without reading.</p>
      <ul className="mt-2 space-y-1">
        <li>
          <a href="/how-to-play" target="_blank" rel="noreferrer" className="text-accent underline">
            How to play (a one-page guide for everyone)
          </a>
        </li>
        {dealLater && (
          <li className="text-xs text-muted">
            🎲 No character booklets or clue cards with “Surprise me”: the killer is dealt from your guests' characters when the evening begins,
            and everyone's phone shows their dossier. To print them, create the party with a specific version.
          </li>
        )}
        {links.map((l) => (
          <li key={l.kind}>
            <a href={api.kitUrl(code, l.kind)} className="text-accent underline" download>
              {l.label} (PDF)
            </a>
            {l.note && <span className="ml-2 text-xs text-red-300">{l.note}</span>}
          </li>
        ))}
      </ul>
    </details>
  )
}

/** The theme's cocktails, when the host switched on drinking prompts. */
function Cocktails({ themeSlug }: { themeSlug: string }) {
  const { themes } = useThemes()
  const cocktails = themes?.find((t) => t.theme.slug === themeSlug)?.theme.cocktails ?? []
  if (cocktails.length === 0) return null
  return (
    <div className="rounded-xl border border-accent/40 bg-accent/5 p-4 text-sm">
      <p className="font-display text-xl text-accent">🍸 Tonight's cocktails</p>
      <ul className="mt-2 space-y-2">
        {cocktails.map((c) => (
          <li key={c.name}>
            <span className="font-semibold">{c.name}</span>: <span className="text-muted">{c.recipe}</span>
            {c.mocktail && <span className="block text-xs text-muted">Alcohol-free: {c.mocktail}</span>}
          </li>
        ))}
      </ul>
    </div>
  )
}
