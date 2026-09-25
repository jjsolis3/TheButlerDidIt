import { useEffect, useState, type ReactNode } from 'react'
import { useParams } from 'react-router'
import { Portrait } from '../components/Portrait'
import { Shell } from '../components/ui'
import { api } from '../lib/api'
import { useThemePalette } from '../lib/theme'
import type { RecapPage } from '../lib/types'

/**
 * The shared after-party page: who played whom, whodunit and how, who guessed right,
 * the awards, everyone's secrets and the interrogation log. Reached by a private link
 * the host chose to share; no sign-in needed.
 */
export default function Recap() {
  const { slug = '' } = useParams()
  const [page, setPage] = useState<RecapPage | null>(null)
  const [missing, setMissing] = useState(false)
  useThemePalette(page?.recap.scenario.themeSlug)
  useNoIndex()

  useEffect(() => {
    let cancelled = false
    api.publicRecap(slug).then(
      (p) => !cancelled && setPage(p),
      () => !cancelled && setMissing(true),
    )
    return () => {
      cancelled = true
    }
  }, [slug])

  if (missing)
    return (
      <Shell>
        <h1 className="font-display mt-10 text-3xl">This recap isn't available</h1>
        <p className="mt-3 text-muted">The link may be mistyped, or the host has stopped sharing it.</p>
      </Shell>
    )
  if (!page) return <p className="p-10 text-center text-muted">Opening the case file…</p>
  return <RecapBody page={page} />
}

/** Asks search engines not to index the page while it's open (the API response says the same). */
function useNoIndex() {
  useEffect(() => {
    const meta = document.createElement('meta')
    meta.name = 'robots'
    meta.content = 'noindex'
    document.head.appendChild(meta)
    return () => meta.remove()
  }, [])
}

export function RecapBody({ page }: { page: RecapPage }) {
  const { recap } = page
  const s = recap.scenario
  const reveal = recap.reveal
  const played = new Date(page.playedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })

  return (
    <Shell wide>
      <header className="mt-6 text-center">
        <p className="text-xs tracking-widest text-accent uppercase">The case file</p>
        <h1 className="font-display mt-2 text-4xl sm:text-5xl">{s.title}</h1>
        <p className="mt-2 text-muted">
          {s.place}, {s.era} · played {played} · hosted by {page.hostName}
        </p>
        {s.settingImage && <img src={s.settingImage} alt={s.place} className="mt-6 aspect-video w-full rounded-2xl object-cover" />}
      </header>

      <Section title="The suspects">
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {recap.cast.map((c) => (
            <div key={c.characterId} className={`flex gap-3 rounded-xl border p-3 ${c.isMurderer ? 'border-blood bg-blood/15' : 'border-line bg-surface'}`}>
              <Portrait id={c.characterId} name={c.name} src={c.portrait} size={64} />
              <div className="min-w-0">
                <p className="font-display text-lg leading-tight">{c.name}</p>
                <p className="text-xs text-accent">{c.title}</p>
                <p className="mt-1 text-sm text-muted">{c.playedBy ? `played by ${c.playedBy}` : 'played by the narrator'}</p>
                {c.isMurderer && <p className="mt-1 text-xs font-semibold tracking-wider text-red-200 uppercase">The killer</p>}
              </div>
              {c.photoUrl && <img src={c.photoUrl} alt={`${c.playedBy} in costume`} className="ml-auto h-16 w-16 shrink-0 rounded-full border border-accent object-cover" />}
            </div>
          ))}
        </div>
      </Section>

      <Section title="Whodunit">
        <p className="font-display text-2xl">
          <span className="text-accent">{reveal.murdererName}</span> killed {s.victimName}.
        </p>
        <p className="mt-2 text-muted">
          <span className="text-ink">Why:</span> {reveal.motive} <span className="ml-2 text-ink">How:</span> {reveal.method}
        </p>
        <div className="mt-4 space-y-3 leading-relaxed">
          {reveal.explanation.map((p) => (
            <p key={p}>{p}</p>
          ))}
        </div>
        {reveal.timeline.length > 0 && (
          <ol className="mt-6 space-y-2 border-l border-accent/50 pl-4">
            {reveal.timeline.map((t) => (
              <li key={t.time + t.event} className="text-sm">
                <span className="font-mono text-accent">{t.time}</span> {t.event}
              </li>
            ))}
          </ol>
        )}
      </Section>

      <Section title="Who guessed what">
        <div className="space-y-2">
          {reveal.guesses.map((g) => (
            <div key={g.playerName} className="rounded-lg border border-line bg-surface p-3 text-sm">
              <p>
                <span className="font-semibold">{g.playerName}</span>
                {g.suspectName ? (
                  <>
                    {' '}accused <span className="text-accent">{g.suspectName}</span>
                    {g.correct === true ? ' ✓' : g.correct === false ? ' ✗' : ''}
                  </>
                ) : (
                  <span className="text-muted"> made no accusation</span>
                )}
              </p>
              {g.verdict && <p className="font-display mt-1 text-muted italic">“{g.verdict}”</p>}
            </div>
          ))}
        </div>
      </Section>

      <Section title="Scores and awards">
        <div className="grid gap-3 sm:grid-cols-3">
          {recap.awards.bestDetective && <Award title="Best Detective" winners={[recap.awards.bestDetective.playerName]} />}
          {recap.awards.results?.map((r) => <Award key={r.awardId} title={r.title} winners={r.winners} />)}
        </div>
        <ol className="mt-4 space-y-1 text-sm">
          {reveal.scores.map((sc, i) => (
            <li key={sc.seatId} className="flex justify-between border-b border-line/60 py-1">
              <span>
                {i + 1}. {sc.playerName} {sc.characterName && <span className="text-muted">({sc.characterName})</span>}
              </span>
              <span className="font-mono text-accent">{sc.points}</span>
            </li>
          ))}
        </ol>
      </Section>

      <Section title="Everyone's secrets">
        <div className="grid gap-3 sm:grid-cols-2">
          {recap.cast
            .filter((c) => c.secrets.length > 0)
            .map((c) => (
              <div key={c.characterId} className="rounded-xl border border-line bg-surface p-4 text-sm">
                <p className="font-semibold">{c.name}</p>
                <ul className="mt-1 list-disc space-y-1 pl-5 text-muted">
                  {c.secrets.map((x) => (
                    <li key={x}>{x}</li>
                  ))}
                </ul>
              </div>
            ))}
        </div>
      </Section>

      {recap.interrogations.length > 0 && (
        <Section title="The interrogation">
          <div className="space-y-3">
            {recap.interrogations.map((i) => (
              <div key={i.id} className="rounded-xl border border-line bg-surface p-3 text-sm">
                <p className="text-muted">
                  <span className="text-ink">{i.askerName}</span> asked {i.characterName}: “{i.question}”
                </p>
                {i.answer && <p className="mt-1">{i.answer}</p>}
              </div>
            ))}
          </div>
        </Section>
      )}

      <p className="font-display my-12 text-center text-xl text-muted italic">Thank you for a killer evening.</p>
    </Shell>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-10">
      <h2 className="font-display mb-4 text-2xl">{title}</h2>
      {children}
    </section>
  )
}

function Award({ title, winners }: { title: string; winners: string[] }) {
  return (
    <div className="rounded-xl border border-accent/60 bg-surface p-4 text-center">
      <p className="text-xs tracking-widest text-accent uppercase">{title}</p>
      <p className="font-display mt-1 text-2xl">{winners.length ? winners.join(' & ') : '—'}</p>
    </div>
  )
}
