import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button, ErrorText, inputClass } from '../components/ui'
import { api } from '../lib/api'
import type { AiStatus, EscapeRoomSummary, PartyMode, PuzzleChoice } from '../lib/types'
import { formatDuration } from './time'

/** The escape-room shelf on the create-party page: pick a room, pick how you'll play, open the lobby. */
export function NewEscapeParty() {
  const navigate = useNavigate()
  const [rooms, setRooms] = useState<EscapeRoomSummary[] | null>(null)
  const [chosen, setChosen] = useState<string>()
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
    api.escapeRooms().then(setRooms, (e: Error) => setError(e.message))
    api.aiStatus().then(setAi, () => setAi(null))
  }, [])

  const roomId = chosen ?? rooms?.[0]?.id
  const room = rooms?.find((r) => r.id === roomId)
  const aiAvailable = !!ai && (ai.actor || ai.inspector)

  const create = async () => {
    if (!roomId) return
    setBusy(true)
    setError(null)
    try {
      const party = await api.createEscapeParty(roomId, mode, puzzles, puzzles === 'replay' ? Number(puzzleSet) : null, aiAvailable && useAi)
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
        {!rooms && !error && <p className="text-muted">Loading rooms…</p>}
        <div className="grid gap-3 sm:grid-cols-2">
          {rooms?.map((r) => (
            <button
              key={r.id}
              onClick={() => setChosen(r.id)}
              aria-pressed={roomId === r.id}
              className={`rounded-xl border p-4 text-left transition ${roomId === r.id ? 'border-accent bg-accent/10' : 'border-line bg-surface hover:border-accent/60'}`}
            >
              <p className="font-display text-lg">{r.title}</p>
              <p className="mt-1 text-xs text-muted">
                {r.contentRating === 'mature' ? '🍷 Adults' : '🧸 Family'} · {r.timeLimitMinutes} min · {r.minPlayers}–{r.maxPlayers} players · {r.stageCount} rooms,{' '}
                {r.puzzleCount} puzzles
              </p>
              <p className="mt-2 text-sm text-ink/90">{r.synopsis}</p>
              {r.bestScore !== null && <p className="mt-2 text-xs text-accent">🏆 Best escape: {formatDuration(r.bestScore)}</p>}
            </button>
          ))}
        </div>
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
