import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { buttonClass } from '../components/buttonClass'
import { ErrorText, Eyebrow, inputClass, Shell } from '../components/ui'
import { RoomBoards } from '../escape/RoomBoards'
import { RoomCardBody, ShelfControls } from '../escape/RoomShelf'
import { useRoomShelf } from '../escape/useRoomShelf'
import { api } from '../lib/api'
import { ESCAPE_PALETTE, usePalette } from '../lib/theme'
import type { EscapeRoomSummary } from '../lib/types'
import { useMe } from '../lib/useMe'

const STEPS = [
  {
    icon: '📺',
    title: 'Gather round',
    body: 'One screen shows the room and the clock. Everyone joins on their own phone with the party code: no app to install, no account needed.',
  },
  {
    icon: '🧩',
    title: 'Split the clues',
    body: 'Each phone holds different pieces. Search the scene, look closer at what you find, put things together and crack the codes, but only by talking to each other.',
  },
  {
    icon: '⏱️',
    title: 'Beat the clock',
    body: 'Play for 30, 45 or 60 minutes, on Easy, Normal or Hard. Stuck? A hint helps, but costs time. The fastest escapes make the leaderboard.',
  },
]

const LENGTHS = [30, 45, 60]

/** Where "Host" goes: the host page with the room picked, through sign-in first for a visitor. */
function hostHref(signedOut: boolean, roomId?: string) {
  const page = `/host/new?game=escape${roomId ? `&room=${encodeURIComponent(roomId)}` : ''}`
  return signedOut ? `/login?next=${encodeURIComponent(page)}` : page
}

/**
 * The escape rooms' front door (/escape): what they are, how a game goes, and every room on the
 * shelf, for anyone to browse before signing in. It has its own colours (ESCAPE_PALETTE), so it
 * doesn't look like the murder mysteries.
 */
export default function EscapeLanding() {
  usePalette(ESCAPE_PALETTE)
  const { me } = useMe()
  const signedOut = me === null
  const [rooms, setRooms] = useState<EscapeRoomSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const shelf = useRoomShelf(rooms)
  const [players, setPlayers] = useState<number | null>(null)
  const [minutes, setMinutes] = useState<number | null>(null)
  // One room's leaderboards open at a time, so the page stays short on a phone.
  const [boardsFor, setBoardsFor] = useState<string | null>(null)

  useEffect(() => {
    // The sign-in cookie goes with it, so a host also gets the rooms the AI wrote for them.
    api.escapeRooms().then(setRooms, (e: Error) => setError(e.message))
  }, [])

  const mostPlayers = Math.max(0, ...(rooms ?? []).map((r) => r.maxPlayers))
  const fits = shelf.onShelf.filter(
    (r) => (players === null || (r.minPlayers <= players && players <= r.maxPlayers)) && (minutes === null || r.lengths.some((l) => l.minutes === minutes)),
  )

  return (
    <Shell wide>
      <section className="py-10 text-center sm:py-16">
        <Eyebrow>Locked in, together</Eyebrow>
        <h1 className="font-display mt-4 text-5xl leading-none text-ink sm:text-7xl">
          Escape <span className="text-accent italic">Rooms</span>
        </h1>
        <p className="mx-auto mt-5 max-w-xl text-lg text-muted">
          A locked room, a ticking clock, and clues split between everyone's phones. Search, decode and argue your way out before time runs out,
          around the table or over a video call.
        </p>
        <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
          <Link to="/join" className={buttonClass('primary', 'w-full px-8 text-base sm:w-auto')}>
            I have a party code
          </Link>
          <Link to={hostHref(signedOut)} className={buttonClass('ghost', 'w-full px-8 text-base sm:w-auto')}>
            {signedOut ? 'Sign in to host' : 'Host an escape room'}
          </Link>
        </div>
        <p className="mt-4 text-sm">
          <Link to="/how-to-play/escape" className="text-muted underline hover:text-ink">
            New to escape rooms? How to play
          </Link>
        </p>
      </section>

      <section aria-labelledby="how-it-works" className="mb-12">
        <h2 id="how-it-works" className="font-display mb-4 text-2xl">
          How it works
        </h2>
        <ol className="grid gap-3 sm:grid-cols-3">
          {STEPS.map((step, i) => (
            <li key={step.title} className="rounded-xl border border-line bg-surface p-5">
              <p className="text-3xl" aria-hidden="true">
                {step.icon}
              </p>
              <h3 className="font-display mt-2 text-xl">
                <span className="text-accent">{i + 1}.</span> {step.title}
              </h3>
              <p className="mt-2 text-sm text-muted">{step.body}</p>
            </li>
          ))}
        </ol>
      </section>

      <section aria-labelledby="rooms" className="space-y-3">
        <h2 id="rooms" className="font-display text-2xl">
          Choose your room
        </h2>
        <p className="max-w-2xl text-sm text-muted">
          Every room shuffles its codes, riddles and passwords, so you can play it again and again. Or take on 📅 today's challenge: the same
          puzzles for every group today, raced on a daily leaderboard.
        </p>
        <ShelfControls shelf={shelf} />
        {/* Label above each list, side by side: fits a phone without wrapping mid-label. */}
        <div className="grid grid-cols-2 gap-3 sm:max-w-md" role="group" aria-label="Narrow the shelf">
          <label className="block space-y-1 text-sm">
            <span className="block text-muted">How many of you?</span>
            <select className={`${inputClass} py-2`} value={players ?? ''} onChange={(e) => setPlayers(e.target.value ? Number(e.target.value) : null)}>
              <option value="">Any number</option>
              {Array.from({ length: mostPlayers }, (_, i) => i + 1).map((n) => (
                <option key={n} value={n}>
                  {n} {n === 1 ? 'player' : 'players'}
                </option>
              ))}
            </select>
          </label>
          <label className="block space-y-1 text-sm">
            <span className="block text-muted">How long?</span>
            <select className={`${inputClass} py-2`} value={minutes ?? ''} onChange={(e) => setMinutes(e.target.value ? Number(e.target.value) : null)}>
              <option value="">Any length</option>
              {LENGTHS.map((m) => (
                <option key={m} value={m}>
                  {m} minutes
                </option>
              ))}
            </select>
          </label>
        </div>

        <ErrorText>{error}</ErrorText>
        {!rooms && !error && <p className="text-muted">Loading rooms…</p>}
        {rooms && fits.length === 0 && (
          <p className="text-sm text-muted">
            {shelf.onShelf.length === 0 ? `No ${shelf.shelf === 'family' ? 'Family' : 'Adult'} rooms yet.` : 'No room on this shelf fits that.'}{' '}
            {(players !== null || minutes !== null) && (
              <button
                className="text-accent underline"
                onClick={() => {
                  setPlayers(null)
                  setMinutes(null)
                }}
              >
                Show every room
              </button>
            )}
          </p>
        )}

        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {fits.map((r) => (
            <article key={r.id} aria-label={r.title} className="flex flex-col rounded-xl border border-line bg-surface p-3">
              <RoomCardBody room={r} picture="cover" />
              <div className="mt-auto flex flex-wrap gap-2 pt-4">
                <Link to={hostHref(signedOut, r.id)} className={buttonClass('primary', 'flex-1')}>
                  Host this room
                </Link>
                <button
                  className={buttonClass('ghost')}
                  aria-expanded={boardsFor === r.id}
                  onClick={() => setBoardsFor(boardsFor === r.id ? null : r.id)}
                >
                  🏆 Leaderboards
                </button>
              </div>
              {boardsFor === r.id && <RoomBoards room={r} />}
            </article>
          ))}
        </div>
      </section>

      <p className="mt-12 text-center text-sm text-muted">
        Fancy a whodunnit instead?{' '}
        <Link to="/mystery" className="text-accent underline">
          Murder mysteries
        </Link>
      </p>
    </Shell>
  )
}
