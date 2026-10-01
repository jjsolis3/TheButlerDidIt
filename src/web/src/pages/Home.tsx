import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Shell } from '../components/ui'
import { api } from '../lib/api'
import { DEFAULT_PALETTE, ESCAPE_PALETTE, useThemes } from '../lib/theme'
import type { EscapeRoomSummary, PartyInfo, ThemePalette } from '../lib/types'
import { useMe } from '../lib/useMe'

const MODE_LABEL = { sharedScreen: 'Dinner party', remote: 'Video call', passAndPlay: 'Pass & play' } as const

/** How a party reads in "Your parties": which game, and how it's played. */
function partyKind(p: PartyInfo) {
  if (p.kind === 'escapeRoom') return `🔐 Escape room · ${p.mode === 'sharedScreen' ? 'Together' : MODE_LABEL[p.mode]}`
  return `🔍 Mystery · ${MODE_LABEL[p.mode]}`
}

/** "Family & Adults", "Adults only"…: which catalogs a game has something on. */
function catalogs(ratings: string[]) {
  const family = ratings.includes('family')
  const adults = ratings.includes('mature')
  return family && adults ? 'Family & Adults' : family ? 'Family' : adults ? 'Adults' : ''
}

/** One of the two games on the front door, drawn in that game's own colours. */
function GameDoor({ to, palette, icon, eyebrow, title, body, facts }: { to: string; palette: ThemePalette; icon: string; eyebrow: string; title: string; body: string; facts: string }) {
  return (
    <Link
      to={to}
      className="group block rounded-2xl border p-6 transition hover:-translate-y-0.5 focus-visible:outline-2 focus-visible:outline-offset-2 sm:p-8"
      style={{ background: `linear-gradient(160deg, ${palette.surface}, ${palette.background})`, color: palette.ink, borderColor: `${palette.accent}55`, outlineColor: palette.accent }}
    >
      <span className="block text-5xl" aria-hidden="true">
        {icon}
      </span>
      <span className="mt-4 block text-xs font-semibold tracking-[0.2em] uppercase" style={{ color: palette.accent }}>
        {eyebrow}
      </span>
      <span className="font-display mt-1 block text-3xl sm:text-4xl">{title}</span>
      <span className="mt-2 block text-sm opacity-80">{body}</span>
      <span className="mt-5 block text-sm font-semibold group-hover:underline" style={{ color: palette.accent }}>
        {facts}
        {facts && ' · '}Explore →
      </span>
    </Link>
  )
}

/** What "Remove" does depends on the party, so the confirmation says exactly that. */
function removeWarning(p: PartyInfo) {
  if (p.status === 'finished')
    return `Remove “${p.title}” (${p.code}) from your list? The recap link keeps working, and “Surprise me” still remembers you've played this version.`
  const guests = p.playerCount > 0 ? ` The ${p.playerCount} guest${p.playerCount === 1 ? '' : 's'} who joined will be told the party is over.` : ''
  return `Delete “${p.title}” (${p.code})? The party code stops working and this can't be undone.${guests}`
}

/**
 * The front door: the two games (murder mysteries and escape rooms), each with its own page,
 * plus a party code box for guests and the host's own parties.
 */
export default function Home() {
  const { me } = useMe()
  const { themes } = useThemes()
  const [rooms, setRooms] = useState<EscapeRoomSummary[] | null>(null)
  const [parties, setParties] = useState<PartyInfo[]>([])
  const [removeError, setRemoveError] = useState<string | null>(null)
  const navigate = useNavigate()

  useEffect(() => {
    if (me) api.myParties().then(setParties, () => {})
  }, [me])

  useEffect(() => {
    api.escapeRooms().then(setRooms, () => setRooms(null))
  }, [])

  const mysteries = themes?.flatMap((t) => t.scenarios) ?? []
  const handWrittenRooms = rooms?.filter((r) => !r.generated) ?? []

  const remove = async (p: PartyInfo) => {
    if (!window.confirm(removeWarning(p))) return
    setRemoveError(null)
    try {
      await api.removeParty(p.code)
      // Drop it from the list straight away rather than reloading everything.
      setParties((all) => all.filter((x) => x.code !== p.code))
    } catch (e) {
      setRemoveError((e as Error).message)
    }
  }

  return (
    <Shell wide>
      <section className="py-10 text-center sm:py-16">
        <Eyebrow>Game night, most civilised</Eyebrow>
        <h1 className="font-display mt-4 text-5xl leading-none text-ink sm:text-7xl">
          The Butler <span className="text-accent italic">Did It</span>
        </h1>
        <p className="mx-auto mt-5 max-w-xl text-lg text-muted">
          Host a murder mystery where every guest is a suspect, or an escape room where everyone holds a piece of the puzzle. Play around the
          table or over a video call, each on your own phone.
        </p>
        <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
          <Button onClick={() => navigate('/join')} className="w-full px-8 text-base sm:w-auto">
            I have a party code
          </Button>
          {me ? (
            <Button variant="ghost" onClick={() => navigate('/host/new')} className="w-full px-8 text-base sm:w-auto">
              Host a new party
            </Button>
          ) : (
            <Button variant="ghost" onClick={() => navigate('/login')} className="w-full px-8 text-base sm:w-auto">
              Sign in to host
            </Button>
          )}
        </div>
      </section>

      <section className="mb-12 grid gap-4 md:grid-cols-2" aria-label="Choose a game">
        <GameDoor
          to="/mystery"
          palette={DEFAULT_PALETTE}
          icon="🔍"
          eyebrow="Whodunnit?"
          title="Murder Mystery"
          body="Every guest plays a suspect with secrets to keep. Question each other, follow the clues, and unmask the killer before the night is out."
          facts={mysteries.length > 0 ? `${mysteries.length} mysteries · ${catalogs(mysteries.map((m) => m.contentRating))}` : ''}
        />
        <GameDoor
          to="/escape"
          palette={ESCAPE_PALETTE}
          icon="🔐"
          eyebrow="Can you get out?"
          title="Escape Room"
          body="Split the clues, crack the codes and beat the clock. Every phone holds a different piece, so you only get out together."
          facts={handWrittenRooms.length > 0 ? `${handWrittenRooms.length} rooms · ${catalogs(handWrittenRooms.map((r) => r.contentRating))}` : ''}
        />
      </section>

      {me && parties.length > 0 && (
        <section className="mb-12">
          <h2 className="font-display mb-4 text-2xl">Your parties</h2>
          <ErrorText>{removeError}</ErrorText>
          <div className="grid gap-3 sm:grid-cols-2">
            {parties.map((p) => (
              // The card isn't one big link: a button inside a link is invalid HTML, and a
              // mis-tap on "Remove" would open the party. So the title is the link instead.
              <Card key={p.code} className="transition hover:border-accent">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <Link to={`/stage/${p.code}`} className="font-display text-lg hover:text-accent">
                      {p.title}
                    </Link>
                    <p className="text-sm text-muted">
                      {partyKind(p)} · {p.playerCount}/{p.maxPlayers} guests · {p.status === 'inProgress' ? 'in progress' : p.status}
                    </p>
                  </div>
                  <span className="rounded-md border border-line px-2 py-1 font-mono text-sm tracking-widest text-accent">{p.code}</span>
                </div>
                <div className="mt-3 flex items-center justify-between gap-3 text-sm">
                  <Link to={`/stage/${p.code}`} className="text-accent underline">
                    {p.status === 'finished' ? 'Open' : 'Continue'}
                  </Link>
                  <button type="button" onClick={() => remove(p)} className="text-muted underline hover:text-red-300" aria-label={`Remove ${p.title} (${p.code})`}>
                    {p.status === 'finished' ? 'Remove from list' : 'Delete'}
                  </button>
                </div>
              </Card>
            ))}
          </div>
        </section>
      )}

      <p className="text-center text-sm text-muted">
        New here? How to play a{' '}
        <Link to="/how-to-play" className="underline hover:text-ink">
          murder mystery
        </Link>{' '}
        or an{' '}
        <Link to="/how-to-play/escape" className="underline hover:text-ink">
          escape room
        </Link>
        .
      </p>
    </Shell>
  )
}
