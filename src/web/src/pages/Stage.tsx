import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { CuePlayer } from '../components/CuePlayer'
import { Portrait } from '../components/Portrait'
import { ClueCard, Countdown, FeedToasts, QrCode } from '../components/Scene'
import { Button, ErrorText, StatusPill } from '../components/ui'
import { api } from '../lib/api'
import { useParty } from '../lib/hub'
import { seats } from '../lib/seats'
import { narrator } from '../lib/speech'
import { useThemePalette } from '../lib/theme'
import type { PartyInfo, StageView } from '../lib/types'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

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
    return (
      <Centered>
        <p className="mb-4">Only the host or seated guests can watch the stage.</p>
        <Link className="text-accent underline" to={`/join/${code}`}>
          Join this party
        </Link>
      </Centered>
    )
  }
  return <StageScreen info={info} token={info.isHost ? undefined : guestSeat?.token} />
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="grid min-h-dvh place-items-center p-6 text-center text-muted">{children}</div>
}

function StageScreen({ info, token }: { info: PartyInfo; token?: string }) {
  const { stage, status, fatal, invoke } = useParty({ code: info.code, token, watchStage: true })
  const [begun, setBegun] = useState(false)
  const [muted, setMuted] = useState(false)
  useThemePalette(info.themeSlug)

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

  if (fatal) return <Centered>{fatal}</Centered>
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
    <div className="grain flex min-h-dvh flex-col">
      <StatusPill status={status} />
      <TopBar stage={stage} info={info} muted={muted} onMute={() => setMuted((m) => !m)} />
      <main className="mx-auto w-full max-w-6xl flex-1 px-4 pt-4 pb-32 sm:px-8">
        <PhaseView stage={stage} info={info} invoke={invoke} muted={muted} begun={begun || stage.phase === 'lobby'} onBegin={() => setBegun(true)} />
      </main>
      {info.isHost && <HostBar stage={stage} info={info} invoke={invoke} />}
      <FeedToasts feed={stage.feed} offset="top-20" />
    </div>
  )
}

function TopBar({ stage, info, muted, onMute }: { stage: StageView; info: PartyInfo; muted: boolean; onMute: () => void }) {
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
}: {
  stage: StageView
  info: PartyInfo
  invoke: Invoke
  muted: boolean
  begun: boolean
  onBegin: () => void
}) {
  switch (stage.phase) {
    case 'lobby':
      return <LobbyView stage={stage} info={info} invoke={invoke} />
    case 'castReveal':
      return <CastView stage={stage} />
    case 'prologue':
      return <CuePlayer cues={stage.cues} runKey="prologue" muted={muted} enabled={begun} />
    case 'act':
      return stage.actStep === 'cinematic' ? (
        <div className="space-y-4">
          <h2 className="font-display text-center text-3xl sm:text-4xl">{stage.actTitle}</h2>
          <CuePlayer cues={stage.cues} runKey={`act-${stage.actNumber}`} muted={muted} enabled={begun} />
        </div>
      ) : (
        <MingleView stage={stage} />
      )
    case 'accusation':
      return <AccusationView stage={stage} />
    case 'reveal':
      return <RevealView stage={stage} muted={muted} begun={begun} />
    case 'awards':
    case 'finished':
      return <AwardsView stage={stage} />
  }
}

// ------------------------------------------------------------------ lobby

function LobbyView({ stage, info, invoke }: { stage: StageView; info: PartyInfo; invoke: Invoke }) {
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
    <div className="grid gap-8 lg:grid-cols-[1fr_1.2fr]">
      <section className="space-y-5">
        <div>
          <p className="text-xs tracking-[0.3em] text-accent uppercase">{stage.scenario.era}</p>
          <h1 className="font-display mt-2 text-4xl leading-tight sm:text-5xl">{stage.scenario.title}</h1>
          <p className="mt-3 text-muted">{stage.scenario.synopsis}</p>
        </div>
        <div className="flex flex-col items-center gap-4 rounded-2xl border border-accent/40 bg-surface p-6 sm:flex-row">
          <QrCode url={joinUrl} size={180} />
          <div className="text-center sm:text-left">
            <p className="text-sm text-muted">Scan to join, or visit</p>
            <p className="font-medium break-all">{window.location.host}/join</p>
            <p className="mt-3 text-sm text-muted">and enter the code</p>
            <p className="font-mono text-5xl tracking-[0.2em] text-accent">{info.code}</p>
          </div>
        </div>
        {info.isHost && (
          <div className="flex flex-wrap gap-2">
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
      </section>

      <section>
        <h2 className="font-display mb-3 text-2xl">
          The suspects <span className="text-base text-muted">({stage.players.length} of up to {stage.scenario.maxPlayers} guests)</span>
        </h2>
        <div className="grid gap-3 sm:grid-cols-2">
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
                      <span className="text-accent">
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
        <p className="mt-2 text-muted">Everyone: read your dossier now. Your secrets have been unlocked.</p>
      </div>
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-4">
        {stage.cast.map((c) => (
          <div key={c.characterId} className="flex flex-col items-center rounded-xl border border-line bg-surface p-4 text-center">
            <Portrait id={c.characterId} name={c.name} src={c.portrait} size={110} />
            <p className="font-display mt-3 text-lg leading-tight">{c.name}</p>
            <p className="text-xs text-accent">{c.title}</p>
            <p className="mt-1 text-xs text-muted">{c.isNpc ? 'Played by the narrator' : `Played by ${c.playedBy}`}</p>
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

function MingleView({ stage }: { stage: StageView }) {
  const [promptIndex, setPromptIndex] = useState(0)
  useEffect(() => {
    if (stage.prompts.length < 2) return
    const id = setInterval(() => setPromptIndex((i) => (i + 1) % stage.prompts.length), 25_000)
    return () => clearInterval(id)
  }, [stage.prompts.length])

  const clues = [...stage.clues].reverse()
  return (
    <div className="space-y-6">
      <div className="text-center">
        <p className="text-xs tracking-[0.3em] text-accent uppercase">{stage.actTitle}</p>
        <div className="mt-2 flex justify-center">
          <Countdown timer={stage.timer} large />
        </div>
        {stage.prompts.length > 0 && (
          <p key={promptIndex} className="font-display mx-auto mt-4 max-w-3xl text-2xl text-ink/90 italic sm:text-3xl">
            “{stage.prompts[promptIndex % stage.prompts.length]}”
          </p>
        )}
      </div>
      <div className="grid gap-6 lg:grid-cols-[2fr_1fr]">
        <section>
          <h2 className="font-display mb-3 text-2xl">Evidence</h2>
          {clues.length === 0 ? (
            <p className="text-muted">No evidence has come to light yet.</p>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2">
              {clues.map((c) => (
                <ClueCard key={c.id} clue={c} />
              ))}
            </div>
          )}
        </section>
        <section>
          <h2 className="font-display mb-3 text-2xl">Secrets exposed</h2>
          {stage.revealedSecrets.length === 0 ? (
            <p className="text-sm text-muted">Nobody has confessed anything… yet.</p>
          ) : (
            <ul className="space-y-3">
              {stage.revealedSecrets.map((s) => (
                <li key={s.text} className="rounded-xl border border-line bg-surface p-3 text-sm">
                  <span className="text-accent">{s.characterName}</span>
                  <p className="mt-1">{s.text}</p>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  )
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

function RevealView({ stage, muted, begun }: { stage: StageView; muted: boolean; begun: boolean }) {
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
    return (
      <div className="space-y-8">
        <CuePlayer cues={stage.cues} runKey="finale" muted={muted} enabled={begun} />
        <Scores stage={stage} />
        <section>
          <h2 className="font-display mb-3 text-2xl">What really happened</h2>
          <ol className="space-y-2 border-l border-accent/50 pl-5">
            {r.timeline.map((t) => (
              <li key={t.time + t.event}>
                <span className="font-mono text-accent">{t.time}</span> <span className="text-ink/90">{t.event}</span>
              </li>
            ))}
          </ol>
        </section>
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
        <section className="flex flex-col items-center gap-4 text-center">
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
          <div className="mt-4 max-w-3xl space-y-4 text-left">
            {r.explanation.map((p, i) => (
              <p key={i} className={`font-display text-xl leading-relaxed ${i === r.explanation.length - 1 ? 'text-ink' : 'text-ink/60'}`}>
                {p}
              </p>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}

function Scores({ stage }: { stage: StageView }) {
  const scores = stage.reveal?.scores ?? []
  return (
    <section>
      <h2 className="font-display mb-3 text-2xl">The detectives' scores</h2>
      <ol className="space-y-2">
        {scores.map((s, i) => (
          <li key={s.seatId} className="flex items-center justify-between gap-3 rounded-xl border border-line bg-surface p-3">
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

function AwardsView({ stage }: { stage: StageView }) {
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
  return (
    <div className="space-y-8 py-6 text-center">
      <h1 className="font-display text-5xl">And the winners are…</h1>
      <div className="grid gap-4 sm:grid-cols-3">
        {a.bestDetective && (
          <Award title="Best Detective" winners={[a.bestDetective.playerName]} detail={`${a.bestDetective.points} points`} />
        )}
        {a.results.map((r) => (
          <Award key={r.awardId} title={r.title} winners={r.winners} detail={r.votes ? `${r.votes} vote${r.votes === 1 ? '' : 's'}` : 'No votes'} />
        ))}
      </div>
      <Scores stage={stage} />
      <p className="font-display text-2xl text-muted italic">Thank you for a killer evening.</p>
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

function HostBar({ stage, info, invoke }: { stage: StageView; info: PartyInfo; invoke: Invoke }) {
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const call = async (method: string, ...args: unknown[]) => {
    setBusy(true)
    setError(null)
    try {
      await invoke(method, info.code, ...args)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const next = nextAction(stage)
  const mingle = stage.phase === 'act' && stage.actStep === 'mingle'

  return (
    <div className="fixed inset-x-0 bottom-0 z-30 border-t border-line bg-bg/95 px-4 py-3 backdrop-blur sm:px-8">
      <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="mr-1 text-xs tracking-widest text-muted uppercase">Host</span>
          {stage.phase === 'lobby' && (
            <Button variant="ghost" disabled={busy} onClick={() => call('AutoAssign')}>
              Auto-assign characters
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

function nextAction(stage: StageView): { label: string; method: string; disabled?: boolean } | null {
  switch (stage.phase) {
    case 'lobby':
      return {
        label: stage.players.length < stage.scenario.minPlayers ? `Need ${stage.scenario.minPlayers} guests to start` : 'Begin the evening',
        method: 'StartGame',
        disabled: stage.players.length < stage.scenario.minPlayers,
      }
    case 'castReveal':
      return { label: 'Play the prologue', method: 'Advance' }
    case 'prologue':
      return { label: 'Begin Act One', method: 'Advance' }
    case 'act':
      if (stage.actStep === 'cinematic') return { label: 'Start mingling', method: 'Advance' }
      return { label: stage.actNumber < stage.actCount ? `End Act ${stage.actNumber}` : 'Time for accusations', method: 'Advance' }
    case 'accusation':
      return { label: 'Reveal the truth', method: 'Advance' }
    case 'reveal': {
      const r = stage.reveal!
      if (r.step === r.stepCount - 1) return { label: 'On to the awards', method: 'Advance' }
      return { label: r.step === 0 ? 'Unmask the killer' : 'Continue', method: 'Advance' }
    }
    case 'awards':
      return { label: 'Close voting & announce', method: 'Advance' }
    default:
      return null
  }
}
