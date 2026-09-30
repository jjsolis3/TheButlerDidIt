import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { Button, ErrorText, FilterChip, inputClass } from '../components/ui'
import { api } from '../lib/api'
import type { AiStatus, ContentRating, EscapeDifficulty, EscapeRoomSummary, GenerationJob, PartyMode, PuzzleChoice } from '../lib/types'
import { GenerateEscapeRoom } from './GenerateEscapeRoom'
import { formatDuration } from './time'

const isHalloween = (room: EscapeRoomSummary) => room.seasons.includes('halloween')

/** The escape-room shelf on the create-party page: pick a room, pick how you'll play, open the lobby. */
export function NewEscapeParty() {
  const navigate = useNavigate()
  const [rooms, setRooms] = useState<EscapeRoomSummary[] | null>(null)
  // "Play this room again" links here with the room, length and difficulty just played (?room=…&minutes=…&difficulty=…).
  const [params] = useSearchParams()
  const askedRoom = params.get('room')
  const [chosen, setChosen] = useState<string | undefined>(askedRoom ?? undefined)
  // Adults and Family shelves, and a Halloween filter within them, like the mystery shelf.
  const [shelf, setShelf] = useState<ContentRating>('mature')
  const [halloweenOnly, setHalloweenOnly] = useState(false)
  const [spookySeason] = useState(() => new Date().getMonth() === 9) // October; read once, not on every render
  // The game's length. Only kept while the chosen room offers it (worked out during render below).
  const [chosenMinutes, setChosenMinutes] = useState<number | undefined>(() => Number(params.get('minutes')) || undefined)
  const [difficulty, setDifficulty] = useState<EscapeDifficulty>(() => {
    const d = params.get('difficulty')
    return d === 'easy' || d === 'hard' ? d : 'normal'
  })
  const [mode, setMode] = useState<PartyMode>('sharedScreen')
  // Fresh puzzles every time by default; today's challenge races every other group; a puzzle set
  // number (shown at the end of every game) replays exactly the same puzzles.
  const [puzzles, setPuzzles] = useState<PuzzleChoice>('fresh')
  const [puzzleSet, setPuzzleSet] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // The AI game master is offered only when an admin has set up the models it uses.
  const [ai, setAi] = useState<AiStatus | null>(null)
  const [useAi, setUseAi] = useState(true)

  useEffect(() => {
    api.escapeRooms().then((all) => {
      setRooms(all)
      // A room asked for in the link opens on its own shelf. Unknown ids are ignored (the shelf's first room is picked).
      const asked = all.find((r) => r.id === askedRoom)
      if (asked) setShelf(asked.contentRating)
    }, (e: Error) => setError(e.message))
    api.aiStatus().then(setAi, () => setAi(null))
  }, [askedRoom])

  // A room written by AI goes to the top of the shelf, selected, ready to play.
  const onWritten = async (job: GenerationJob) => {
    const all = await api.escapeRooms()
    setRooms(all)
    const written = all.find((r) => r.id === job.scenarioId)
    if (!written) return
    setShelf(written.contentRating) // show it on its own shelf
    setHalloweenOnly(false)
    setChosen(written.id)
  }

  const remove = async (room: EscapeRoomSummary) => {
    if (!window.confirm(`Delete “${room.title}”? Its best times stay on the leaderboards.`)) return
    setError(null)
    try {
      await api.deleteEscapeRoom(room.id)
      setRooms((all) => all?.filter((r) => r.id !== room.id) ?? null)
      if (chosen === room.id) setChosen(undefined)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const byRating = rooms?.filter((r) => r.contentRating === shelf) ?? []
  const halloweenCount = byRating.filter(isHalloween).length
  // The filter only applies while this shelf has Halloween rooms, so switching shelves never leaves it empty.
  const onShelf = halloweenOnly && halloweenCount > 0 ? byRating.filter(isHalloween) : byRating
  // The host's pick if it's on this shelf, otherwise the shelf's first room: worked out during render,
  // so switching shelves can never leave a room from the other shelf selected.
  const room = onShelf.find((r) => r.id === chosen) ?? onShelf[0]
  const roomId = room?.id
  const minutes = room && room.lengths.some((l) => l.minutes === chosenMinutes) ? chosenMinutes! : room?.timeLimitMinutes
  const aiAvailable = !!ai && (ai.actor || ai.inspector)

  const create = async () => {
    if (!roomId) return
    setBusy(true)
    setError(null)
    try {
      const party = await api.createEscapeParty(roomId, mode, puzzles, puzzles === 'replay' ? Number(puzzleSet) : null, aiAvailable && useAi, minutes ?? null, difficulty)
      navigate(`/stage/${party.code}`)
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  return (
    <div className="space-y-8">
      <section className="space-y-3">
        <h2 className="font-display text-xl">1. Choose a room</h2>
        <p className="text-xs text-muted">
          Work together against the clock: every phone holds different clues, so talk! Hints help, but each one costs time.
        </p>
        <div className="grid grid-cols-2 gap-2 rounded-xl border border-line bg-surface p-1" role="tablist" aria-label="Escape room catalog">
          {(['mature', 'family'] as const).map((s) => (
            <button
              key={s}
              role="tab"
              aria-selected={shelf === s}
              onClick={() => setShelf(s)}
              className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${shelf === s ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
            >
              {s === 'mature' ? '🍷 Adults' : '🧸 Family'} <span className="font-normal opacity-80">({rooms?.filter((r) => r.contentRating === s).length ?? 0})</span>
            </button>
          ))}
        </div>
        <p className="text-xs text-muted">
          {shelf === 'mature' ? 'For grown-ups: horror in the style of the Saw films. Tense and creepy, never graphic.' : 'For all ages: spooky or silly, never scary. Great with kids.'}
        </p>
        {halloweenCount > 0 && (
          <div className="flex flex-wrap gap-2" role="group" aria-label="Filter rooms">
            <FilterChip on={!halloweenOnly} onClick={() => setHalloweenOnly(false)}>
              All rooms
            </FilterChip>
            <FilterChip on={halloweenOnly} glow={spookySeason && !halloweenOnly} onClick={() => setHalloweenOnly(true)}>
              🎃 Halloween <span className="font-normal opacity-80">({halloweenCount})</span>
              {spookySeason && <span className="font-normal"> · It's spooky season!</span>}
            </FilterChip>
          </div>
        )}
        {!rooms && !error && <p className="text-muted">Loading rooms…</p>}
        {rooms && onShelf.length === 0 && (
          <p className="text-sm text-muted">No {shelf === 'family' ? 'Family' : 'Adult'} rooms yet.{ai?.storyteller ? ' Write one with AI below.' : ''}</p>
        )}
        {ai?.storyteller && <GenerateEscapeRoom onReady={onWritten} />}
        <div className="grid gap-3 sm:grid-cols-2">
          {onShelf.map((r) => (
            <div key={r.id} className="flex flex-col">
              <button
                onClick={() => setChosen(r.id)}
                aria-pressed={roomId === r.id}
                className={`flex-1 rounded-xl border p-4 text-left transition ${roomId === r.id ? 'border-accent bg-accent/10' : 'border-line bg-surface hover:border-accent/60'}`}
              >
                {r.generated && <p className="text-xs font-semibold tracking-widest text-accent uppercase">✨ Written by AI for you</p>}
                <p className="font-display text-lg">{r.title}</p>
                <p className="mt-1 text-xs text-muted">
                  {r.contentRating === 'mature' ? '🍷 Adults' : '🧸 Family'} · {r.lengths.map((l) => l.minutes).join('/')} min · {r.minPlayers}–{r.maxPlayers} players ·{' '}
                  {r.stageCount} rooms, {r.puzzleCount} puzzles{isHalloween(r) && ' · 🎃 Halloween'}
                </p>
                <p className="mt-2 text-sm text-ink/90">{r.synopsis}</p>
                {r.bestScore !== null && <p className="mt-2 text-xs text-accent">🏆 Best escape: {formatDuration(r.bestScore)}</p>}
              </button>
              {/* Outside the card: a button can't sit inside another button. */}
              {r.generated && (
                <button onClick={() => remove(r)} className="mt-1 self-end text-xs text-muted underline hover:text-ink">
                  Delete this room
                </button>
              )}
            </div>
          ))}
        </div>
        {room && room.lengths.length > 1 && (
          <div className="space-y-2">
            <h3 className="text-sm font-semibold">How long?</h3>
            <div className="grid gap-2 sm:grid-cols-3">
              {room.lengths.map((l) => (
                <label key={l.minutes} className={`cursor-pointer rounded-xl border p-3 ${minutes === l.minutes ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}>
                  <input type="radio" name="escape-length" className="sr-only" checked={minutes === l.minutes} onChange={() => setChosenMinutes(l.minutes)} />
                  <span className="font-semibold">⏱️ {l.minutes} minutes</span>
                  <span className="mt-1 block text-xs text-muted">
                    {l.puzzleCount} puzzles{l.minutes === room.timeLimitMinutes ? ' · the standard game' : l.minutes < room.timeLimitMinutes ? ' · a quicker game' : ' · the extended cut'}
                  </span>
                </label>
              ))}
            </div>
            <p className="text-xs text-muted">Each length has its own leaderboard.</p>
          </div>
        )}
        {room && (
          <div className="space-y-2">
            <h3 className="text-sm font-semibold">How hard?</h3>
            <div className="grid gap-2 sm:grid-cols-3">
              {(
                [
                  ['easy', '🙂 Easy', 'Fewer clues to gather, and hints cost half the time.'],
                  ['normal', '😐 Normal', 'The room as it was written.'],
                  ['hard', '😈 Hard', 'More clues, tougher codes, hints that only nudge, and empty hiding places cost time.'],
                ] as const
              ).map(([value, label, text]) => (
                <label key={value} className={`cursor-pointer rounded-xl border p-3 ${difficulty === value ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}>
                  <input type="radio" name="escape-difficulty" className="sr-only" checked={difficulty === value} onChange={() => setDifficulty(value)} />
                  <span className="font-semibold">{label}</span>
                  <span className="mt-1 block text-xs text-muted">{text}</span>
                </label>
              ))}
            </div>
            <p className="text-xs text-muted">Each difficulty has its own leaderboard too.</p>
          </div>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="font-display text-xl">2. Which puzzles?</h2>
        <p className="text-xs text-muted">Every room has its codes, riddles and passwords shuffled, so you can play it again and again.</p>
        <div className="grid gap-2 sm:grid-cols-3">
          {(
            [
              ['fresh', '🎲 Fresh puzzles', 'A new mix nobody has seen.'],
              ['daily', "📅 Today's challenge", 'The same puzzles for every group today. Race them on the daily leaderboard.'],
              ['replay', '🔁 Replay a puzzle set', 'Every game ends with its puzzle set number. Enter one to play the same puzzles.'],
            ] as const
          ).map(([value, label, text]) => (
            <label key={value} className={`cursor-pointer rounded-xl border p-3 ${puzzles === value ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}>
              <input type="radio" name="escape-puzzles" className="sr-only" checked={puzzles === value} onChange={() => setPuzzles(value)} />
              <span className="font-semibold">{label}</span>
              <span className="mt-1 block text-xs text-muted">{text}</span>
            </label>
          ))}
        </div>
        {puzzles === 'replay' && (
          <label className="block max-w-xs text-sm">
            Puzzle set number
            <input
              value={puzzleSet}
              onChange={(e) => setPuzzleSet(e.target.value.replace(/\D/g, '').slice(0, 6))}
              inputMode="numeric"
              placeholder="e.g. 48213"
              className={`${inputClass} mt-1`}
            />
          </label>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="font-display text-xl">3. How will you play?</h2>
        <div className="grid gap-2 sm:grid-cols-2">
          {(
            [
              ['sharedScreen', '📺 Together', 'A TV or laptop shows the room and the clock; everyone uses their phone.'],
              ['remote', '💻 On a video call', 'Share the room tab on the call; everyone joins on their own device.'],
            ] as const
          ).map(([value, label, text]) => (
            <label key={value} className={`cursor-pointer rounded-xl border p-3 ${mode === value ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}>
              <input type="radio" name="escape-mode" className="sr-only" checked={mode === value} onChange={() => setMode(value)} />
              <span className="font-semibold">{label}</span>
              <span className="mt-1 block text-xs text-muted">{text}</span>
            </label>
          ))}
        </div>
      </section>

      {aiAvailable && ai && (
        <section>
          <label className="flex items-start gap-3 rounded-xl border border-line bg-surface p-4">
            <input type="checkbox" className="mt-1 accent-[var(--theme-accent)]" checked={useAi} onChange={(e) => setUseAi(e.target.checked)} />
            <span>
              <span className="font-semibold">Use the AI game master{room ? ` (${room.gameMaster})` : ''}</span>
              <span className="block text-xs text-muted">
                {[ai.actor && `reacts out loud on the TV${ai.voice ? ' in its own voice' : ''}`, ai.inspector && 'writes hints for exactly where you are stuck (never the answer)']
                  .filter(Boolean)
                  .join('; ')
                  .replace(/^./, (c) => c.toUpperCase())}
                . This month's AI spend: ${ai.spentThisMonthUsd.toFixed(2)}
                {ai.budgetUsd > 0 ? ` of $${ai.budgetUsd.toFixed(2)}` : ''}.
              </span>
            </span>
          </label>
        </section>
      )}

      <div>
        <Button onClick={create} disabled={busy || !roomId || (puzzles === 'replay' && puzzleSet === '')}>
          {busy ? 'Opening the room…' : 'Create the escape room and get the invite code'}
        </Button>
        <ErrorText>{error}</ErrorText>
      </div>
    </div>
  )
}
