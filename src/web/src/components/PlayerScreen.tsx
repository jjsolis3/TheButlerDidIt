import { useEffect, useRef, useState } from 'react'
import { api } from '../lib/api'
import { useParty } from '../lib/hub'
import type { PlayerView, StageView } from '../lib/types'
import { Portrait } from './Portrait'
import { ClueCard, Countdown, FeedToasts } from './Scene'
import { Button, ErrorText, StatusPill } from './ui'

type Tab = 'dossier' | 'secrets' | 'clues' | 'question' | 'notes' | 'accuse' | 'vote' | 'results'

const PHASE_LABEL: Record<StageView['phase'], string> = {
  lobby: 'Before the party',
  castReveal: 'Meet the suspects',
  prologue: 'The prologue',
  act: 'Investigation',
  accusation: 'Accusations',
  reveal: 'The reveal',
  awards: 'Awards',
  finished: 'The end',
}

/**
 * Everything one guest sees on their phone. Also used by pass-and-play, where
 * the host's device shows it for one local seat at a time.
 */
export function PlayerScreen({ code, token, onLeave }: { code: string; token: string; onLeave: () => void }) {
  const [removed, setRemoved] = useState(false)
  const { player, status, fatal, invoke } = useParty({ code, token, joinSeat: true, onRemoved: () => setRemoved(true) })

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

  return (
    <>
      <StatusPill status={status} />
      <PlayerBody view={player} invoke={invoke} token={token} />
      <FeedToasts feed={player.stage.feed} offset="top-28" />
    </>
  )
}

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

function PlayerBody({ view, invoke, token }: { view: PlayerView; invoke: Invoke; token: string }) {
  const stage = view.stage
  const [error, setError] = useState<string | null>(null)
  const run = (method: string, ...args: unknown[]) => {
    setError(null)
    return invoke(method, ...args).catch((e: Error) => setError(e.message))
  }

  if (stage.phase === 'lobby') return <LobbyPlayer view={view} run={run} error={error} token={token} />
  return <InGame view={view} run={run} invoke={invoke} error={error} />
}

// ------------------------------------------------------------------ lobby

function LobbyPlayer({
  view,
  run,
  error,
  token,
}: {
  view: PlayerView
  run: (m: string, ...a: unknown[]) => Promise<unknown>
  error: string | null
  token: string
}) {
  const stage = view.stage
  const mine = view.dossier?.character
  const [picking, setPicking] = useState(!mine)

  useEffect(() => {
    if (!mine) setPicking(true)
  }, [mine])

  return (
    <div className="mx-auto max-w-xl space-y-5 px-4 py-6">
      <header>
        <p className="text-xs tracking-widest text-accent uppercase">You're invited</p>
        <h1 className="font-display mt-1 text-3xl">{stage.scenario.title}</h1>
        <p className="mt-2 text-sm text-muted">{stage.scenario.synopsis}</p>
      </header>

      {mine && !picking ? (
        <section className="rounded-xl border border-accent/50 bg-surface p-5">
          <div className="flex gap-4">
            <Portrait id={mine.characterId} name={mine.name} src={mine.portrait} size={84} />
            <div>
              <p className="text-xs tracking-widest text-accent uppercase">You will play</p>
              <h2 className="font-display text-2xl leading-tight">{mine.name}</h2>
              <p className="text-sm text-muted">
                {mine.title} · {mine.pronouns}
              </p>
            </div>
          </div>
          <p className="mt-4 text-sm leading-relaxed">{mine.publicBio}</p>
          <div className="mt-4 rounded-lg bg-bg/60 p-3">
            <p className="text-xs font-semibold tracking-wider text-accent uppercase">What to wear</p>
            <p className="mt-1 text-sm">{mine.costumeTips}</p>
          </div>
          <CostumeSelfie token={token} photoUrl={stage.players.find((p) => p.seatId === view.seatId)?.photoUrl ?? null} />
          <p className="mt-4 text-xs text-muted">
            Your {view.dossier?.lockedSecrets} secret{view.dossier?.lockedSecrets === 1 ? '' : 's'} unlock when the evening begins.
          </p>
          <div className="mt-4 flex flex-wrap gap-2">
            <Button variant={view.ready ? 'ghost' : 'primary'} onClick={() => run('SetReady', !view.ready)}>
              {view.ready ? "✓ I'm ready" : "I'm ready"}
            </Button>
            <Button variant="quiet" onClick={() => setPicking(true)}>
              Choose someone else
            </Button>
          </div>
        </section>
      ) : (
        <section>
          <h2 className="font-display mb-3 text-xl">Choose your character</h2>
          <p className="mb-3 text-sm text-muted">Or leave it to the host, and a character will be assigned when the game starts.</p>
          <div className="grid gap-3">
            {stage.cast.map((c) => {
              const takenBySomeoneElse = !!c.playedBy && c.characterId !== mine?.characterId
              return (
                <button
                  key={c.characterId}
                  disabled={takenBySomeoneElse}
                  onClick={async () => {
                    await run('ChooseCharacter', c.characterId)
                    setPicking(false)
                  }}
                  className={`flex gap-3 rounded-xl border p-3 text-left transition ${c.characterId === mine?.characterId ? 'border-accent bg-accent/10' : 'border-line bg-surface hover:border-accent/60'} disabled:opacity-40`}
                >
                  <Portrait id={c.characterId} name={c.name} src={c.portrait} size={56} dim={takenBySomeoneElse} />
                  <div className="min-w-0">
                    <p className="font-display text-lg leading-tight">{c.name}</p>
                    <p className="text-xs text-accent">
                      {c.title}
                      {c.required ? '' : ' · optional'}
                    </p>
                    <p className="mt-1 line-clamp-2 text-xs text-muted">{c.publicBio}</p>
                    {takenBySomeoneElse && <p className="mt-1 text-xs">Taken by {c.playedBy}</p>}
                  </div>
                </button>
              )
            })}
          </div>
        </section>
      )}
      <ErrorText>{error}</ErrorText>

      <section className="text-sm text-muted">
        <p>
          {stage.players.length} guest{stage.players.length === 1 ? '' : 's'} here: {stage.players.map((p) => p.name).join(', ')}
        </p>
      </section>
    </div>
  )
}

/**
 * Guests snap a photo in costume; it replaces their name on the big screen's cast list.
 * `capture="user"` opens the front camera on phones; on a laptop it's a normal file picker.
 * The server shrinks the photo and strips its location data before anyone else sees it.
 * We don't keep our own copy of the URL: the upload changes the party state, and the new
 * snapshot arrives over SignalR like every other change, so all screens stay in step.
 */
function CostumeSelfie({ token, photoUrl }: { token: string; photoUrl: string | null }) {
  const input = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const upload = async (file: File | undefined) => {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      await api.uploadPhoto(token, file)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
      if (input.current) input.current.value = '' // allow choosing the same file again
    }
  }

  return (
    <div className="mt-4 flex items-center gap-3 rounded-lg bg-bg/60 p-3">
      {photoUrl ? (
        <img src={photoUrl} alt="Your costume selfie" className="h-16 w-16 shrink-0 rounded-full border border-accent object-cover" />
      ) : (
        <span className="flex h-16 w-16 shrink-0 items-center justify-center rounded-full border border-dashed border-line text-2xl">📸</span>
      )}
      <div className="min-w-0 space-y-1">
        <p className="text-xs font-semibold tracking-wider text-accent uppercase">Costume selfie</p>
        <p className="text-xs text-muted">Show off your outfit on the big screen. Optional.</p>
        <div className="flex flex-wrap gap-2">
          <label className={`cursor-pointer text-sm text-accent underline ${busy ? 'pointer-events-none opacity-50' : ''}`}>
            {busy ? 'Uploading…' : photoUrl ? 'Retake' : 'Take a selfie'}
            <input
              ref={input}
              type="file"
              accept="image/*"
              capture="user"
              className="sr-only"
              aria-label="Costume selfie"
              onChange={(e) => void upload(e.target.files?.[0])}
            />
          </label>
          {photoUrl && !busy && (
            <button className="text-sm text-muted underline hover:text-ink" onClick={() => void api.removePhoto(token)}>
              Remove
            </button>
          )}
        </div>
        <ErrorText>{error}</ErrorText>
      </div>
    </div>
  )
}

// ------------------------------------------------------------------ in game

function InGame({
  view,
  run,
  invoke,
  error,
}: {
  view: PlayerView
  run: (m: string, ...a: unknown[]) => Promise<unknown>
  invoke: Invoke
  error: string | null
}) {
  const stage = view.stage
  const dossier = view.dossier
  const [tab, setTab] = useState<Tab>('dossier')
  const privateClueIds = useRef<Set<string> | null>(null)
  const [newClue, setNewClue] = useState(false)

  // Jump to the tab that matters when the phase changes.
  useEffect(() => {
    if (stage.phase === 'accusation') setTab('accuse')
    else if (stage.phase === 'awards') setTab('vote')
    else if (stage.phase === 'reveal' || stage.phase === 'finished') setTab('results')
    else setTab((t) => (t === 'accuse' || t === 'vote' || t === 'results' || (t === 'question' && stage.phase !== 'act') ? 'dossier' : t))
  }, [stage.phase])

  // Buzz the phone when a private clue arrives.
  useEffect(() => {
    const ids = new Set(view.myClues.filter((c) => c.isPrivate).map((c) => c.id))
    if (privateClueIds.current && [...ids].some((id) => !privateClueIds.current!.has(id))) {
      navigator.vibrate?.([80, 60, 80])
      setNewClue(true)
    }
    privateClueIds.current = ids
  }, [view.myClues])

  const tabs: { id: Tab; label: string }[] = [
    { id: 'dossier', label: 'Dossier' },
    { id: 'secrets', label: 'Secrets' },
    { id: 'clues', label: `Clues${view.myClues.length ? ` (${view.myClues.length})` : ''}` },
    { id: 'notes', label: 'Notes' },
  ]
  // Questioning NPCs only makes sense while there are narrator-played characters and an act is running.
  if (stage.ai.npcQuestions && stage.phase === 'act' && stage.cast.some((c) => c.isNpc)) tabs.splice(3, 0, { id: 'question', label: 'Question' })
  if (stage.phase === 'accusation') tabs.push({ id: 'accuse', label: 'Accuse' })
  if (stage.phase === 'awards') tabs.push({ id: 'vote', label: 'Vote' })
  if (stage.phase === 'reveal' || stage.phase === 'finished') tabs.push({ id: 'results', label: 'Results' })

  return (
    <div className="mx-auto flex min-h-dvh max-w-xl flex-col">
      <header className="sticky top-0 z-30 border-b border-line bg-bg/95 px-4 pt-3 backdrop-blur">
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate text-xs tracking-widest text-accent uppercase">
              {stage.phase === 'act' ? stage.actTitle : PHASE_LABEL[stage.phase]}
            </p>
            <p className="font-display truncate text-lg">{dossier?.character.name ?? view.name}</p>
          </div>
          <Countdown timer={stage.timer} />
        </div>
        <nav className="-mx-1 mt-2 flex gap-1 overflow-x-auto pb-2">
          {tabs.map((t) => (
            <button
              key={t.id}
              onClick={() => {
                setTab(t.id)
                if (t.id === 'clues') setNewClue(false)
              }}
              className={`relative shrink-0 rounded-full px-3 py-1.5 text-sm ${tab === t.id ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
            >
              {t.label}
              {t.id === 'clues' && newClue && <span className="absolute -top-0.5 -right-0.5 h-2.5 w-2.5 rounded-full bg-red-400" />}
            </button>
          ))}
        </nav>
      </header>

      <main className="flex-1 space-y-4 px-4 py-5">
        <ErrorText>{error}</ErrorText>
        {tab === 'dossier' && dossier && <DossierTab view={view} />}
        {tab === 'secrets' && dossier && <SecretsTab view={view} run={run} />}
        {tab === 'clues' && (
          <>
            {stage.ai.hints && stage.phase === 'act' && <HintBox view={view} invoke={invoke} />}
            <CluesTab view={view} invoke={invoke} run={run} />
          </>
        )}
        {tab === 'question' && <QuestionTab view={view} invoke={invoke} />}
        {tab === 'notes' && <NotesTab invoke={invoke} />}
        {tab === 'accuse' && <AccuseTab view={view} run={run} />}
        {tab === 'vote' && <VoteTab view={view} run={run} />}
        {tab === 'results' && <ResultsTab view={view} />}
      </main>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-xl border border-line bg-surface p-4">
      <h3 className="mb-2 text-xs font-semibold tracking-widest text-accent uppercase">{title}</h3>
      {children}
    </section>
  )
}

function DossierTab({ view }: { view: PlayerView }) {
  const d = view.dossier!
  const c = d.character
  return (
    <div className="space-y-4">
      {d.isMurderer && (
        <div className="rounded-xl border border-blood bg-blood/20 p-4 text-center">
          <p className="font-display text-2xl text-red-200">You are the murderer.</p>
          <p className="mt-1 text-sm text-red-100/80">Keep it to yourself. Lie, deflect, and point the finger elsewhere.</p>
        </div>
      )}
      <div className="flex gap-4">
        <Portrait id={c.characterId} name={c.name} src={c.portrait} size={90} />
        <div>
          <h2 className="font-display text-2xl leading-tight">{c.name}</h2>
          <p className="text-sm text-accent">{c.title}</p>
          <p className="text-xs text-muted">{c.pronouns}</p>
        </div>
      </div>
      {d.linesThisAct.length > 0 && (
        <Section title="Say this aloud during this act">
          {d.linesThisAct.map((l) => (
            <p key={l} className="font-display text-lg leading-snug italic">
              “{l}”
            </p>
          ))}
        </Section>
      )}
      <Section title="Who you are">
        <p className="text-sm leading-relaxed">{d.backstory}</p>
      </Section>
      <Section title="Your alibi">
        <p className="text-sm leading-relaxed">{d.alibi}</p>
      </Section>
      <Section title="Your goals tonight">
        <ul className="list-disc space-y-1 pl-5 text-sm">
          {d.objectives.map((o) => (
            <li key={o}>{o}</li>
          ))}
        </ul>
      </Section>
      {d.knows.length > 0 && (
        <Section title="What you know">
          <ul className="list-disc space-y-1 pl-5 text-sm">
            {d.knows.map((k) => (
              <li key={k}>{k}</li>
            ))}
          </ul>
        </Section>
      )}
      <Section title="What everyone knows about you">
        <p className="text-sm leading-relaxed text-muted">{c.publicBio}</p>
      </Section>
    </div>
  )
}

function SecretsTab({ view, run }: { view: PlayerView; run: (m: string, ...a: unknown[]) => Promise<unknown> }) {
  const d = view.dossier!
  const others = view.stage.revealedSecrets.filter((s) => s.characterId !== d.character.characterId)
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">
        Guard your secrets, or reveal one to everyone if it helps clear your name. Revealed secrets appear on the big screen.
      </p>
      {d.secrets.map((s) => (
        <div key={s.id} className="rounded-xl border border-line bg-surface p-4">
          <p className="text-sm leading-relaxed">{s.text}</p>
          <div className="mt-3">
            {s.revealed ? (
              <span className="text-xs text-accent">Revealed to everyone</span>
            ) : (
              <button
                className="text-xs text-muted underline hover:text-ink"
                onClick={() => {
                  if (confirm('Reveal this secret to everyone? You cannot take it back.')) void run('RevealSecret', s.id)
                }}
              >
                Reveal to everyone
              </button>
            )}
          </div>
        </div>
      ))}
      {d.lockedSecrets > 0 && (
        <p className="text-center text-sm text-muted">
          🔒 {d.lockedSecrets} more secret{d.lockedSecrets === 1 ? '' : 's'} will surface later tonight.
        </p>
      )}
      {others.length > 0 && (
        <Section title="Secrets others have revealed">
          <ul className="space-y-2 text-sm">
            {others.map((s) => (
              <li key={s.text}>
                <span className="text-accent">{s.characterName}:</span> {s.text}
              </li>
            ))}
          </ul>
        </Section>
      )}
    </div>
  )
}

function CluesTab({ view, invoke, run }: { view: PlayerView; invoke: Invoke; run: (m: string, ...a: unknown[]) => Promise<unknown> }) {
  const clues = [...view.myClues].reverse()
  if (clues.length === 0) return <p className="py-10 text-center text-muted">No clues yet. They'll appear here as the evening unfolds.</p>
  return (
    <div className="space-y-3">
      {clues.map((c) => (
        <ClueCard
          key={c.id}
          clue={c}
          onShare={() => {
            if (confirm('Show this clue to everyone?')) void run('ShareClue', c.id)
          }}
          onSolve={(answer) => invoke('SolvePuzzle', c.id, answer)}
        />
      ))}
    </div>
  )
}

function NotesTab({ invoke }: { invoke: Invoke }) {
  const [text, setText] = useState<string | null>(null)
  const [saved, setSaved] = useState(true)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)

  useEffect(() => {
    invoke<string>('GetNotes').then(setText, () => setText(''))
  }, [invoke])

  // Save shortly after typing stops rather than on every keystroke.
  const change = (value: string) => {
    setText(value)
    setSaved(false)
    clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      invoke('SaveNotes', value).then(() => setSaved(true), () => {})
    }, 800)
  }

  if (text === null) return <p className="text-muted">Loading notes…</p>
  return (
    <div className="space-y-2">
      <textarea
        value={text}
        onChange={(e) => change(e.target.value)}
        rows={14}
        placeholder="Who was where at 9:47? Who's lying?"
        className="w-full rounded-xl border border-line bg-surface p-4 text-base leading-relaxed focus:border-accent focus:outline-none"
      />
      <p className="text-right text-xs text-muted">{saved ? 'Saved. Only you can see these.' : 'Saving…'}</p>
    </div>
  )
}

function AccuseTab({ view, run }: { view: PlayerView; run: (m: string, ...a: unknown[]) => Promise<unknown> }) {
  const form = view.accusationForm
  const [suspect, setSuspect] = useState(form?.current?.suspectId ?? '')
  const [motive, setMotive] = useState(form?.current?.motiveId ?? '')
  const [method, setMethod] = useState(form?.current?.methodId ?? '')
  if (!form) return null
  const cast = view.stage.cast

  return (
    <div className="space-y-5">
      <h2 className="font-display text-2xl">Who killed {view.stage.scenario.victimName}?</h2>
      <div className="grid grid-cols-2 gap-2">
        {form.suspects.map((s) => {
          const c = cast.find((x) => x.characterId === s.id)
          return (
            <button
              key={s.id}
              onClick={() => setSuspect(s.id)}
              className={`flex flex-col items-center gap-2 rounded-xl border p-3 ${suspect === s.id ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}
            >
              <Portrait id={s.id} name={s.text} src={c?.portrait} size={56} />
              <span className="text-center text-sm leading-tight">{s.text}</span>
            </button>
          )
        })}
      </div>
      <Choice title="Why?" options={form.motives} value={motive} onChange={setMotive} />
      <Choice title="How?" options={form.methods} value={method} onChange={setMethod} />
      <Button className="w-full text-base" disabled={!suspect || !motive || !method} onClick={() => run('SubmitAccusation', suspect, motive, method)}>
        {form.current ? 'Update my accusation' : 'Lock in my accusation'}
      </Button>
      {form.current && <p className="text-center text-sm text-accent">Locked in. You can change it until the host reveals the truth.</p>}
    </div>
  )
}

function Choice({ title, options, value, onChange }: { title: string; options: { id: string; text: string }[]; value: string; onChange: (v: string) => void }) {
  return (
    <fieldset className="space-y-2">
      <legend className="mb-2 text-xs font-semibold tracking-widest text-accent uppercase">{title}</legend>
      {options.map((o) => (
        <label key={o.id} className={`flex cursor-pointer items-center gap-3 rounded-lg border p-3 text-sm ${value === o.id ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}>
          <input type="radio" name={title} checked={value === o.id} onChange={() => onChange(o.id)} className="accent-[var(--theme-accent)]" />
          {o.text}
        </label>
      ))}
    </fieldset>
  )
}

function VoteTab({ view, run }: { view: PlayerView; run: (m: string, ...a: unknown[]) => Promise<unknown> }) {
  const ballot = view.awardBallot
  if (!ballot) return null
  return (
    <div className="space-y-6">
      <h2 className="font-display text-2xl">Cast your votes</h2>
      {ballot.awards.map((a) => (
        <fieldset key={a.id} className="space-y-2">
          <legend className="mb-2 text-xs font-semibold tracking-widest text-accent uppercase">{a.title}</legend>
          {ballot.nominees.map((n) => (
            <button
              key={n.id}
              onClick={() => run('CastAwardVote', a.id, n.id)}
              className={`block w-full rounded-lg border p-3 text-left text-sm ${ballot.myVotes[a.id] === n.id ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}
            >
              {n.text}
            </button>
          ))}
        </fieldset>
      ))}
    </div>
  )
}

function ResultsTab({ view }: { view: PlayerView }) {
  const r = view.stage.reveal
  const mine = r?.scores.find((s) => s.seatId === view.seatId)
  const awards = view.stage.awards
  return (
    <div className="space-y-4 text-center">
      <p className="font-display text-2xl">All eyes on the big screen.</p>
      {r?.murdererName && (
        <p className="text-lg">
          The killer was <span className="text-accent">{r.murdererName}</span>.
        </p>
      )}
      {mine && (
        <div className="rounded-xl border border-accent/50 bg-surface p-4">
          <p className="font-display text-4xl text-accent">{mine.points} pts</p>
          <ul className="mt-2 text-sm text-muted">
            {mine.breakdown.map((b) => (
              <li key={b}>{b}</li>
            ))}
          </ul>
        </div>
      )}
      {awards?.results && (
        <div className="space-y-2">
          {awards.bestDetective && <p>🔍 Best Detective: {awards.bestDetective.playerName}</p>}
          {awards.results.map((a) => (
            <p key={a.awardId}>
              🏆 {a.title}: {a.winners.length ? a.winners.join(' & ') : 'no votes'}
            </p>
          ))}
        </div>
      )}
    </div>
  )
}

// ------------------------------------------------------------------ AI game master

/** Question a character the narrator is playing. Everyone sees the question and the answer. */
function QuestionTab({ view, invoke }: { view: PlayerView; invoke: Invoke }) {
  const stage = view.stage
  const npcs = stage.cast.filter((c) => c.isNpc)
  const [characterId, setCharacterId] = useState(npcs[0]?.characterId ?? '')
  const [question, setQuestion] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const log = [...stage.interrogations].reverse()

  const ask = async () => {
    if (!question.trim() || !characterId) return
    setBusy(true)
    setError(null)
    try {
      await invoke('AskNpc', characterId, question.trim())
      setQuestion('')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">
        Question the characters nobody is playing. They answer in character, and the whole room hears it. They may lie, just like
        everyone else.
      </p>
      <div className="flex gap-2 overflow-x-auto pb-1">
        {npcs.map((c) => (
          <button
            key={c.characterId}
            onClick={() => setCharacterId(c.characterId)}
            className={`flex shrink-0 flex-col items-center gap-1 rounded-xl border p-2 text-xs ${characterId === c.characterId ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}
          >
            <Portrait id={c.characterId} name={c.name} src={c.portrait} size={44} />
            <span className="max-w-20 truncate">{c.name}</span>
          </button>
        ))}
      </div>
      <div className="space-y-2">
        <textarea
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          maxLength={300}
          rows={3}
          placeholder={`Ask ${npcs.find((c) => c.characterId === characterId)?.name ?? 'them'} something…`}
          className="w-full rounded-xl border border-line bg-surface p-3 text-base focus:border-accent focus:outline-none"
        />
        <div className="flex items-center justify-between gap-2">
          <span className="text-xs text-muted">
            {view.questionsLeft} question{view.questionsLeft === 1 ? '' : 's'} left this act
          </span>
          <Button onClick={ask} disabled={busy || !question.trim() || view.questionsLeft === 0}>
            {busy ? 'Waiting for an answer…' : 'Ask'}
          </Button>
        </div>
        <ErrorText>{error}</ErrorText>
      </div>
      {log.length > 0 && (
        <section className="space-y-3">
          <h3 className="text-xs font-semibold tracking-widest text-accent uppercase">The interrogation so far</h3>
          {log.map((i) => (
            <div key={i.id} className="rounded-xl border border-line bg-surface p-3 text-sm">
              <p className="text-muted">
                <span className="text-ink">{i.askerName}</span> asked {i.characterName}: “{i.question}”
              </p>
              <p className={`mt-2 ${i.answer ? '' : 'candle text-muted italic'}`}>{i.answer ?? `${i.characterName} is thinking…`}</p>
            </div>
          ))}
        </section>
      )}
    </div>
  )
}

/** A private nudge from the Inspector, limited per act. */
function HintBox({ view, invoke }: { view: PlayerView; invoke: Invoke }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const hints = [...view.myHints].reverse()
  return (
    <section className="rounded-xl border border-accent/40 bg-accent/5 p-4">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm">
          <span className="font-semibold">Stuck?</span> <span className="text-muted">The Inspector can nudge you. Only you will see it.</span>
        </p>
        <Button
          variant="ghost"
          className="min-h-9 shrink-0 py-1"
          disabled={busy || view.hintsLeft === 0}
          onClick={async () => {
            setBusy(true)
            setError(null)
            try {
              await invoke('RequestHint')
            } catch (e) {
              setError((e as Error).message)
            } finally {
              setBusy(false)
            }
          }}
        >
          {busy ? 'Thinking…' : view.hintsLeft === 0 ? 'Hint used' : 'Ask the Inspector'}
        </Button>
      </div>
      {hints.map((h) => (
        <p key={h.id} className={`font-display mt-3 text-base italic ${h.text ? '' : 'candle text-muted'}`}>
          {h.text ?? 'The Inspector is thinking…'}
        </p>
      ))}
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
