import { Link } from 'react-router'
import { buttonClass } from '../components/buttonClass'
import { Eyebrow, Shell } from '../components/ui'
import { useThemes } from '../lib/theme'
import { useMe } from '../lib/useMe'

/**
 * The murder mysteries' front door (/mystery): what an evening is like, and every theme on the
 * shelf with which catalogs (Family, Adults) it has something on.
 */
export default function MysteryLanding() {
  const { me } = useMe()
  const { themes } = useThemes()
  const signedOut = me === null

  return (
    <Shell wide>
      <section className="py-10 text-center sm:py-16">
        <Eyebrow>An evening of murder, most civilised</Eyebrow>
        <h1 className="font-display mt-4 text-5xl leading-none text-ink sm:text-7xl">
          Murder <span className="text-accent italic">Mysteries</span>
        </h1>
        <p className="mx-auto mt-5 max-w-xl text-lg text-muted">
          Host an interactive murder mystery. Every guest plays a suspect with secrets to keep. Play around the dinner table, over a video call,
          or by passing one device around.
        </p>
        <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
          <Link to="/join" className={buttonClass('primary', 'w-full px-8 text-base sm:w-auto')}>
            I have a party code
          </Link>
          <Link to={signedOut ? '/login' : '/host/new'} className={buttonClass('ghost', 'w-full px-8 text-base sm:w-auto')}>
            {signedOut ? 'Sign in to host' : 'Host a mystery'}
          </Link>
        </div>
        <p className="mt-4 text-sm">
          <Link to="/how-to-play" className="text-muted underline hover:text-ink">
            New to murder mysteries? How to play
          </Link>
        </p>
      </section>

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

      <p className="mt-12 text-center text-sm text-muted">
        Rather work together than accuse each other?{' '}
        <Link to="/escape" className="text-accent underline">
          Escape rooms
        </Link>
      </p>
    </Shell>
  )
}
