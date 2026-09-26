import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { nextAction, nextSpeaker, spotlightTime, useHostCall, type Invoke } from '../components/HostControls'
import { Countdown } from '../components/Scene'
import { Button, ErrorText, StatusPill } from '../components/ui'
import { api } from '../lib/api'
import { stageGuide } from '../lib/guide'
import { useParty } from '../lib/hub'
import { useThemePalette } from '../lib/theme'
import type { PartyInfo, StageView } from '../lib/types'
import { useMe } from '../lib/useMe'

/**
 * The host's remote: every host control from the TV's bottom bar, laid out for a phone, plus
 * the host's to-do list for this moment. The TV can then hide its controls and show only the
 * show. It uses the same live connection as the stage (the host's sign-in cookie), so it needs
 * nothing new on the server: guests can't use it, because the server only accepts host
 * commands from the party's host.
 */
export default function Remote() {
  const code = (useParams().code ?? '').toUpperCase()
  const { me } = useMe()
  const navigate = useNavigate()
  const [info, setInfo] = useState<PartyInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (me === null) navigate(`/login?next=${encodeURIComponent(`/remote/${code}`)}`)
  }, [me, code, navigate])

  useEffect(() => {
    if (me) api.party(code).then(setInfo, (e: Error) => setError(e.message))
  }, [me, code])

  if (error) return <Centered>{error}</Centered>
  if (!info) return <Centered>Finding your party…</Centered>
  if (!info.isHost) return <Centered>Only the host of party {code} can use its remote.</Centered>
  return <RemoteScreen info={info} />
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="grid min-h-dvh place-items-center p-6 text-center text-muted">{children}</div>
}

function RemoteScreen({ info }: { info: PartyInfo }) {
  const { stage, status, fatal, invoke } = useParty({ code: info.code, watchStage: true })
  useThemePalette(info.themeSlug)
  if (fatal) return <Centered>{fatal}</Centered>
  if (!stage) return <Centered>Connecting…</Centered>
  return <Controls stage={stage} info={info} invoke={invoke} status={status} />
}

function Controls({ stage, info, invoke, status }: { stage: StageView; info: PartyInfo; invoke: Invoke; status: string }) {
  const { call, busy, error } = useHostCall(info.code, invoke)
  const next = nextAction(stage)
  const mingle = stage.phase === 'act' && stage.actStep === 'mingle'
  const guide = stageGuide(stage, true)

  return (
    <div className="mx-auto min-h-dvh max-w-md space-y-4 px-4 py-4">
      <StatusPill status={status} />
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs tracking-widest text-accent uppercase">📱 Host remote · {info.code}</p>
          <h1 className="font-display truncate text-2xl">{stage.scenario.title}</h1>
          <p className="text-sm text-muted">{guide.title}</p>
        </div>
        {mingle && <Countdown timer={stage.timer} />}
      </header>

      {stage.tailoring && (
        <p className="candle rounded-xl border border-accent/60 bg-accent/10 p-3 text-sm" role="status">
          ✨ The AI is tailoring tonight's mystery to your cast (about a minute).
        </p>
      )}

      {next && (
        <Button className="w-full py-4 text-lg" disabled={busy || next.disabled} onClick={() => call(next.method)}>
          {next.label}
        </Button>
      )}
      <ErrorText>{error}</ErrorText>

      {/* Big, thumb-sized buttons: the host is holding a drink in the other hand. */}
      <div className="grid grid-cols-2 gap-2">
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
        {mingle && (
          <>
            <Button variant="ghost" disabled={busy} onClick={() => call(stage.timer?.paused ? 'ResumeTimer' : 'PauseTimer')}>
              {stage.timer?.paused ? '▶ Resume' : '⏸ Pause'}
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => call('ExtendTimer', 5)}>
              +5 min
            </Button>
            <Button variant="ghost" className="col-span-2" disabled={busy || stage.pendingClues === 0} onClick={() => call('DropNextClue')}>
              Drop next clue{stage.pendingClues ? ` (${stage.pendingClues} left)` : ''}
            </Button>
          </>
        )}
      </div>

      {spotlightTime(stage) && stage.players.length > 0 && (
        <section className="rounded-xl border border-line bg-surface p-3">
          <div className="flex items-center justify-between">
            <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">🎤 Spotlight</h2>
            <button type="button" className="text-xs text-muted underline" disabled={busy} onClick={() => call('Spotlight', nextSpeaker(stage))}>
              {stage.spotlight ? 'Next speaker' : 'Start from the first'}
            </button>
          </div>
          <div className="mt-2 grid grid-cols-2 gap-2">
            {stage.players.map((p) => {
              const on = stage.spotlight?.seatId === p.seatId
              return (
                <button
                  key={p.seatId}
                  type="button"
                  disabled={busy}
                  aria-pressed={on}
                  onClick={() => call('Spotlight', on ? null : p.seatId)}
                  className={`min-h-11 rounded-lg border px-2 text-left text-sm ${on ? 'border-accent bg-accent/15' : 'border-line'}`}
                >
                  {p.name}
                </button>
              )
            })}
          </div>
        </section>
      )}

      {stage.phase === 'lobby' && (
        <section className="rounded-xl border border-line bg-surface p-3">
          <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">
            Guests ({stage.players.length} of {stage.scenario.minPlayers}+ needed)
          </h2>
          <ul className="mt-2 space-y-1 text-sm">
            {stage.players.map((p) => {
              const character = stage.cast.find((c) => c.characterId === p.characterId)?.name
              return (
                <li key={p.seatId} className="flex justify-between gap-2">
                  <span>
                    {p.name}
                    {character && <span className="text-muted"> as {character}</span>}
                  </span>
                  <span className={p.ready ? 'text-accent' : 'text-muted'}>{p.ready ? '✓ ready' : 'choosing…'}</span>
                </li>
              )
            })}
            {stage.players.length === 0 && <li className="text-muted">Nobody has joined yet.</li>}
          </ul>
        </section>
      )}

      {/* Private to the host's phone, so it can say everything the host needs to do. */}
      <section className="rounded-xl border border-line bg-surface p-3">
        <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">What now?</h2>
        <ol className="mt-2 list-decimal space-y-1 pl-5 text-sm leading-relaxed">
          {guide.steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
        {guide.tip && <p className="mt-2 text-xs text-muted">{guide.tip}</p>}
      </section>

      <p className="text-center text-xs text-muted">
        <Link to={`/stage/${info.code}`} className="underline">
          Open the big screen on this device
        </Link>
      </p>
    </div>
  )
}
