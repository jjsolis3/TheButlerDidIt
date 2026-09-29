import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button, ErrorText } from '../components/ui'
import { api } from '../lib/api'
import type { EscapeRoomSummary, PartyMode } from '../lib/types'

/** The escape-room shelf on the create-party page: pick a room, pick how you'll play, open the lobby. */
export function NewEscapeParty() {
  const navigate = useNavigate()
  const [rooms, setRooms] = useState<EscapeRoomSummary[] | null>(null)
  const [chosen, setChosen] = useState<string>()
  const [mode, setMode] = useState<PartyMode>('sharedScreen')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.escapeRooms().then(setRooms, (e: Error) => setError(e.message))
  }, [])

  const roomId = chosen ?? rooms?.[0]?.id

  const create = async () => {
    if (!roomId) return
    setBusy(true)
    setError(null)
    try {
      const party = await api.createEscapeParty(roomId, mode)
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
            </button>
          ))}
        </div>
      </section>

      <section className="space-y-3">
        <h2 className="font-display text-xl">2. How will you play?</h2>
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

      <div>
        <Button onClick={create} disabled={busy || !roomId}>
          {busy ? 'Opening the room…' : 'Create the escape room and get the invite code'}
        </Button>
        <ErrorText>{error}</ErrorText>
      </div>
    </div>
  )
}
