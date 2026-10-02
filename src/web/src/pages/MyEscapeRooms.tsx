import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { buttonClass } from '../components/buttonClass'
import { Button, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { backdrop, moodIcon } from '../escape/moods'
import { api } from '../lib/api'
import type { EscapeLibraryItem, EscapeRoomSource, SharingRequest } from '../lib/types'
import { useMe } from '../lib/useMe'

const SOURCE_LABEL: Record<EscapeRoomSource, string> = {
  builtIn: 'Built-in',
  generated: '✨ Written by AI for you',
  copy: '📄 Your copy',
  shared: '🌍 Shared by the admin',
}

/**
 * My escape rooms (/escape/rooms): every room this host can manage or copy, like My mysteries.
 *
 * Built-in rooms are read from files at every start, so they're never edited in place: you make your own copy and
 * edit that. The admin can then share the copy with every host and take the original off the shelf, which is how
 * an improved room reaches everyone.
 */
export default function MyEscapeRooms() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [items, setItems] = useState<EscapeLibraryItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(() => api.escapeLibrary().then(setItems, (e: Error) => setError(e.message)), [])

  useEffect(() => {
    if (me === null) navigate('/login')
    // reload() only sets state after awaiting the network; the rule can't see into it.
    // oxlint-disable-next-line react/set-state-in-effect
    else if (me) void reload()
  }, [me, navigate, reload])

  // Every action reloads the list afterwards, so what's shown is always what the server has.
  const act = async (action: () => Promise<unknown>) => {
    setError(null)
    try {
      await action()
      await reload()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const copy = (id: string) =>
    act(async () => {
      const made = await api.duplicateEscapeRoom(id)
      navigate(`/escape/rooms/${made.id}`) // the copy opens in the editor
    })

  const share = (item: EscapeLibraryItem, change: SharingRequest) => act(() => api.shareEscapeRoom(item.room.id, change))

  const mine = items?.filter((i) => i.source === 'generated' || i.source === 'copy') ?? []
  const ready = items?.filter((i) => i.source === 'builtIn' || i.source === 'shared') ?? []
  const admin = me?.isAdmin ?? false

  return (
    <Shell wide>
      <Eyebrow>Your library</Eyebrow>
      <Heading className="mt-2 mb-2">My escape rooms</Heading>
      <p className="mb-2 max-w-2xl text-muted">
        Read, improve and host your escape rooms. Rooms written by AI for you, and your copies, can be edited directly. Built-in rooms are copied first: change a
        riddle, rename things, put your family in it, add your own pictures and video.
      </p>
      {admin && (
        <p className="mb-6 max-w-2xl rounded-lg border border-line bg-surface p-3 text-sm text-muted">
          <strong className="text-ink">As the admin:</strong> to improve a built-in room for everyone, make your own copy and edit it, then{' '}
          <em>share it with every host</em> and <em>take the original off the shelf</em>. The original isn't deleted: its parties and leaderboards stay, and you can
          put it back any time. Your copy is a new room, so it starts with its own leaderboards.
        </p>
      )}
      <ErrorText>{error}</ErrorText>
      {!items && !error && <p className="text-muted">Loading your rooms…</p>}

      {items && (
        <>
          <h2 className="font-display mt-6 mb-3 text-2xl">Your rooms</h2>
          {mine.length === 0 ? (
            <p className="text-muted">
              None yet. Make your own copy of a room below, or <Link to="/host/new?game=escape" className="text-accent underline">write one with AI</Link> on the new
              party page.
            </p>
          ) : (
            <div className="space-y-3">
              {mine.map((i) => (
                <Row key={i.room.id} item={i}>
                  <Link to={`/escape/rooms/${encodeURIComponent(i.room.id)}`} className={buttonClass('ghost')}>
                    Edit
                  </Link>
                  <HostIt item={i} />
                  <Button variant="ghost" onClick={() => copy(i.room.id)}>
                    Duplicate
                  </Button>
                  {i.canShare && (
                    <Button variant="ghost" onClick={() => share(i, { shared: !i.room.shared })} aria-pressed={i.room.shared}>
                      {i.room.shared ? 'Stop sharing' : '🌍 Share with every host'}
                    </Button>
                  )}
                  <Button
                    variant="quiet"
                    disabled={i.inUse}
                    onClick={() => {
                      if (confirm(`Delete “${i.room.title}”? Its best times stay on the leaderboards.`)) void act(() => api.deleteEscapeRoom(i.room.id))
                    }}
                  >
                    Delete
                  </Button>
                </Row>
              ))}
            </div>
          )}

          <h2 className="font-display mt-10 mb-3 text-2xl">Ready-made rooms</h2>
          <div className="space-y-3">
            {ready.map((i) => (
              <Row key={i.room.id} item={i}>
                <Link to={`/escape/rooms/${encodeURIComponent(i.room.id)}`} className={buttonClass('ghost')}>
                  Read
                </Link>
                <Button variant="ghost" onClick={() => copy(i.room.id)}>
                  📄 Make my own copy
                </Button>
                {!i.hidden && <HostIt item={i} />}
                {i.canHide && (
                  <>
                    <Link to={`/escape/rooms/${encodeURIComponent(i.room.id)}#media`} className={buttonClass('ghost')}>
                      🎬 Pictures, video & sound
                    </Link>
                    <Button variant="quiet" onClick={() => share(i, { hidden: !i.hidden })} aria-pressed={i.hidden}>
                      {i.hidden ? 'Put back on the shelf' : 'Take off the shelf'}
                    </Button>
                  </>
                )}
              </Row>
            ))}
          </div>
        </>
      )}
    </Shell>
  )
}

/** One room: a small picture, its title, where it comes from, and its actions. */
function Row({ item: i, children }: { item: EscapeLibraryItem; children: ReactNode }) {
  const r = i.room
  return (
    <div className={`flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-surface p-4 ${i.hidden ? 'border-dashed border-line opacity-75' : 'border-line'}`}>
      <div className="flex min-w-0 items-center gap-3">
        <span
          aria-hidden="true"
          className="grid h-12 w-20 shrink-0 place-items-center overflow-hidden rounded-md text-2xl"
          style={r.coverUrl ? undefined : { background: backdrop(r.soundscape) }}
        >
          {r.coverUrl ? <img src={r.coverUrl} alt="" loading="lazy" className="h-full w-full object-cover" /> : moodIcon(r.soundscape)}
        </span>
        <div className="min-w-0">
          <p className="font-display text-xl">{r.title}</p>
          <p className="text-sm text-muted">
            {SOURCE_LABEL[i.source]} · {r.contentRating === 'family' ? 'Family' : 'Adults'} · {r.timeLimitMinutes} min · played {i.timesPlayed} time
            {i.timesPlayed === 1 ? '' : 's'}
            {i.room.shared && i.source !== 'shared' && ' · 🌍 shared with every host'}
            {i.hidden && ' · 🙈 off the shelf'}
            {i.inUse && ' · a party is using it now'}
          </p>
        </div>
      </div>
      <div className="flex flex-wrap gap-2">{children}</div>
    </div>
  )
}

/** Opens the new party page with this room picked. */
function HostIt({ item }: { item: EscapeLibraryItem }) {
  return (
    <Link to={`/host/new?game=escape&room=${encodeURIComponent(item.room.id)}`} className={buttonClass('ghost')}>
      Host it
    </Link>
  )
}
