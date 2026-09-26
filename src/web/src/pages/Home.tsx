import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Shell } from '../components/ui'
import { api } from '../lib/api'
import { useThemes } from '../lib/theme'
import type { PartyInfo } from '../lib/types'
import { useMe } from '../lib/useMe'

const MODE_LABEL = { sharedScreen: 'Dinner party', remote: 'Video call', passAndPlay: 'Pass & play' } as const

/** What "Remove" does depends on the party, so the confirmation says exactly that. */
function removeWarning(p: PartyInfo) {
  if (p.status === 'finished')
    return `Remove “${p.title}” (${p.code}) from your list? The recap link keeps working, and “Surprise me” still remembers you've played this version.`
  const guests = p.playerCount > 0 ? ` The ${p.playerCount} guest${p.playerCount === 1 ? '' : 's'} who joined will be told the party is over.` : ''
  return `Delete “${p.title}” (${p.code})? The party code stops working and this can't be undone.${guests}`
}

export default function Home() {
  const { me, setMe } = useMe()
  const { themes } = useThemes()
  const [parties, setParties] = useState<PartyInfo[]>([])
  const [removeError, setRemoveError] = useState<string | null>(null)
  const navigate = useNavigate()

  useEffect(() => {
    if (me) api.myParties().then(setParties, () => {})
  }, [me])

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
        <Eyebrow>An evening of murder, most civilised</Eyebrow>
        <h1 className="font-display mt-4 text-5xl leading-none text-ink sm:text-7xl">
          The Butler <span className="text-accent italic">Did It</span>
        </h1>
        <p className="mx-auto mt-5 max-w-xl text-lg text-muted">
          Host an interactive murder mystery. Every guest plays a suspect with secrets to keep. Play around the dinner
          table, over a video call, or by passing one device around.
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
        <p className="mt-4 text-sm">
          <Link to="/how-to-play" className="text-muted underline hover:text-ink">
            New to murder mysteries? How to play
          </Link>
        </p>
        {me && (
          <p className="mt-4 text-sm text-muted">
            Signed in as {me.displayName}.{' '}
            <button className="underline hover:text-ink" onClick={() => api.logout().then(() => setMe(null))}>
              Sign out
            </button>
            {' · '}
            <Link to="/mysteries" className="underline hover:text-ink">
              My mysteries
            </Link>
            {me.isAdmin && (
              <>
                {' · '}
                <Link to="/admin/ai" className="underline hover:text-ink">
                  AI settings
                </Link>
                {' · '}
                <Link to="/admin/hosts" className="underline hover:text-ink">
                  Hosts
                </Link>
              </>
            )}
          </p>
        )}
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
                      {MODE_LABEL[p.mode]} · {p.playerCount}/{p.maxPlayers} guests · {p.status === 'inProgress' ? 'in progress' : p.status}
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

      <section>
        <h2 className="font-display mb-1 text-2xl">Choose your mystery</h2>
        <p className="mb-5 text-sm text-muted">Each theme is a different world. More mysteries arrive with the AI storyteller.</p>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {themes?.map(({ theme, scenarios }) => (
            <article
              key={theme.slug}
              className="relative overflow-hidden rounded-xl border border-line p-5"
              style={{ background: `linear-gradient(160deg, ${theme.palette.surface}, ${theme.palette.background})`, color: theme.palette.ink }}
            >
              <p className="text-xs tracking-widest uppercase" style={{ color: theme.palette.accent }}>
                {theme.era}
              </p>
              <h3 className="font-display mt-2 text-2xl">{theme.name}</h3>
              <p className="mt-2 text-sm opacity-80">{theme.tagline}</p>
              <div className="mt-4 flex flex-wrap gap-1 text-xs">
                {scenarios.length > 0 ? (
                  <span className="rounded-full px-2 py-1 font-semibold" style={{ background: theme.palette.accent, color: theme.palette.background }}>
                    {scenarios.length} {scenarios.length === 1 ? 'mystery' : 'mysteries'} ready to play
                  </span>
                ) : (
                  <span className="rounded-full border px-2 py-1 opacity-80" style={{ borderColor: theme.palette.accent }}>
                    {me ? 'Generate a mystery with AI' : 'AI-generated mysteries'}
                  </span>
                )}
                {/* Which catalog(s) this theme has something on. */}
                {scenarios.some((x) => x.contentRating === 'family') && (
                  <span className="rounded-full border px-2 py-1" style={{ borderColor: theme.palette.accent }}>
                    🧸 Family
                  </span>
                )}
                {scenarios.some((x) => x.contentRating === 'mature') && (
                  <span className="rounded-full border px-2 py-1" style={{ borderColor: theme.palette.accent }}>
                    🍷 Adults
                  </span>
                )}
              </div>
            </article>
          ))}
        </div>
      </section>
    </Shell>
  )
}
