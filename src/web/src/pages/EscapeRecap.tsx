import { useEffect, useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { buttonClass } from '../components/buttonClass'
import { Shell } from '../components/ui'
import { DIFFICULTY, KIND_ICON } from '../escape/labels'
import { joinNames } from '../escape/shareCard'
import { ShareCardButton } from '../escape/ShareCardButton'
import { formatDuration } from '../escape/time'
import { api } from '../lib/api'
import { ESCAPE_PALETTE, usePalette } from '../lib/theme'
import type { EscapeRecapPage, EscapeRecapStage } from '../lib/types'
import { useNoIndex } from '../lib/useNoIndex'

/**
 * The shared escape room recap (/escape/recap/:slug, #111): the result, the team, who opened what and when,
 * the highlights and the game master's lines. Reached by a private link the host chose to share; no sign-in.
 * It never shows answers, so it's safe to send to friends who haven't played the room yet.
 */
export default function EscapeRecap() {
  const { slug = '' } = useParams()
  const [page, setPage] = useState<EscapeRecapPage | null>(null)
  const [missing, setMissing] = useState(false)
  usePalette(ESCAPE_PALETTE)
  useNoIndex()

  useEffect(() => {
    let cancelled = false
    api.publicEscapeRecap(slug).then(
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
        <Link to="/escape" className={buttonClass('ghost', 'mt-6')}>
          See the escape rooms
        </Link>
      </Shell>
    )
  if (!page) return <p className="p-10 text-center text-muted">Opening the escape file…</p>
  return <EscapeRecapBody page={page} link={window.location.href} />
}

export function EscapeRecapBody({ page, link }: { page: EscapeRecapPage; link: string | null }) {
  const r = page.recap
  const played = new Date(r.startedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
  const hintCost = r.hintsUsed * r.hintPenaltySeconds

  return (
    <Shell wide>
      <header className="mt-6 text-center">
        <p className="text-xs tracking-widest text-accent uppercase">Escape room recap</p>
        <h1 className="font-display mt-2 text-4xl sm:text-5xl">{r.roomTitle}</h1>
        <p className="mt-2 text-muted">
          Played {played} · hosted by {page.hostName}
        </p>
        <div className="relative mt-6 overflow-hidden rounded-2xl border border-line">
          {r.coverUrl ? (
            <img src={r.coverUrl} alt="" className={`aspect-video w-full object-cover ${r.escaped ? '' : 'grayscale'}`} />
          ) : (
            <div className="grid aspect-[21/9] w-full place-items-center bg-surface text-7xl" aria-hidden>
              {r.escaped ? '🗝️' : '🔒'}
            </div>
          )}
          <p
            data-testid="recap-outcome"
            className={`absolute top-4 left-4 rounded-full px-4 py-1 text-sm font-semibold tracking-widest uppercase ${r.escaped ? 'bg-accent text-bg' : 'bg-blood text-ink'}`}
          >
            {r.escaped ? '🏁 Escaped' : '⏰ Trapped'}
          </p>
        </div>
      </header>

      <dl className="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Tile label="Time" value={formatDuration(r.elapsedSeconds)} detail={r.escaped ? `${formatDuration(r.secondsLeft)} to spare` : `of ${r.timeLimitMinutes}:00`} />
        <Tile label="Puzzles" value={`${r.solvedCount}/${r.puzzleCount}`} detail={`${r.stages.length} of ${r.stageCount} rooms reached`} />
        <Tile label="Hints" value={String(r.hintsUsed)} detail={hintCost > 0 ? `+${formatDuration(hintCost)} on the clock` : 'none needed'} />
        <Tile
          label={page.rank ? 'Rank' : 'Score'}
          value={page.rank ? `#${page.rank}` : '—'}
          detail={page.rank ? `${r.daily ? "on the day's challenge" : 'on the leaderboard'} · score ${formatDuration(r.score)}` : 'only escapes are ranked'}
        />
      </dl>
      <p className="mt-3 text-center text-sm text-muted">
        {DIFFICULTY[r.difficulty]} · {r.timeLimitMinutes} minutes · {r.wrongAttempts} wrong {r.wrongAttempts === 1 ? 'try' : 'tries'}
      </p>

      <blockquote className="mx-auto mt-8 max-w-3xl border-l-2 border-accent pl-4 text-lg text-ink/90 italic">{r.endText}</blockquote>

      {r.highlights.length > 0 && (
        <Section title="Highlights">
          <ul className="grid gap-3 sm:grid-cols-2">
            {r.highlights.map((h) => (
              <li key={h.title} className="flex gap-3 rounded-xl border border-accent/40 bg-surface p-4">
                <span className="text-3xl" aria-hidden>
                  {h.icon}
                </span>
                <div>
                  <p className="text-xs tracking-widest text-accent uppercase">{h.title}</p>
                  <p className="mt-1">{h.detail}</p>
                </div>
              </li>
            ))}
          </ul>
        </Section>
      )}

      <Section title="The team">
        <ul className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {r.team.map((p) => (
            <li key={p.name} className="flex flex-col items-center rounded-xl border border-line bg-surface p-4 text-center">
              {p.photoUrl ? (
                <img src={p.photoUrl} alt={p.name} className="h-20 w-20 rounded-full border border-accent object-cover" />
              ) : (
                <span className="font-display grid h-20 w-20 place-items-center rounded-full border border-line bg-bg text-3xl" aria-hidden>
                  {p.name.slice(0, 1).toUpperCase()}
                </span>
              )}
              <p className="mt-2 font-semibold">{p.name}</p>
              <p className="text-sm text-muted">
                {p.solved} {p.solved === 1 ? 'puzzle' : 'puzzles'} opened
              </p>
            </li>
          ))}
        </ul>
      </Section>

      <Section title="How it went">
        <ol className="space-y-6" data-testid="recap-timeline">
          {r.stages.map((s) => (
            <StageStep key={s.number} stage={s} total={r.stageCount} />
          ))}
        </ol>
        {r.escaped ? (
          <p className="mt-6 font-semibold text-accent">🏁 Out at {formatDuration(r.elapsedSeconds)}</p>
        ) : (
          <p className="mt-6 font-semibold text-red-200">⏰ Time ran out at {formatDuration(r.elapsedSeconds)}</p>
        )}
      </Section>

      {r.gameMasterLines.length > 0 && (
        <Section title={`${r.gameMasterName ?? 'The game master'} said`}>
          <div className="space-y-3">
            {/* The last few are plenty: they end on its line about the escape (or the trap). */}
            {r.gameMasterLines.slice(-5).map((line, i) => (
              <p key={i} className="font-display rounded-xl border border-line bg-surface p-4 text-lg italic">
                “{line}”
              </p>
            ))}
          </div>
        </Section>
      )}

      <Section title="Share it">
        <ShareCardButton page={page} link={link} />
      </Section>

      <section className="my-12 rounded-2xl border border-line bg-surface p-6 text-center">
        <p className="font-display text-2xl">Think you can beat {joinNames(r.team.map((p) => p.name)) || 'them'}?</p>
        <p className="mt-2 text-muted">
          Play {r.roomTitle} with puzzle set <span className="font-display text-ink">#{r.puzzleSet}</span> for the very same puzzles.
        </p>
        <Link to="/escape" className={buttonClass('primary', 'mt-4')}>
          See the escape rooms
        </Link>
      </section>
    </Shell>
  )
}

/** One stage on the timeline: when the group got in and out, and who opened what. */
function StageStep({ stage, total }: { stage: EscapeRecapStage; total: number }) {
  const took = stage.clearedAt === null ? null : stage.clearedAt - stage.openedAt
  return (
    <li className="rounded-xl border border-line bg-surface p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h3 className="font-display text-xl">
          <span className="text-sm text-muted">
            Room {stage.number} of {total} ·{' '}
          </span>
          {stage.title}
        </h3>
        <p className="font-mono text-sm text-muted">
          {formatDuration(stage.openedAt)} → {stage.clearedAt === null ? '…' : formatDuration(stage.clearedAt)}
          {took !== null && <span className="text-ink"> ({formatDuration(took)})</span>}
          {stage.hints > 0 && <span> · 💡{stage.hints}</span>}
        </p>
      </div>
      <ul className="mt-3 space-y-2 border-l border-accent/40 pl-4">
        {stage.puzzles.map((p) => (
          <li key={p.title} className="flex flex-wrap items-baseline gap-x-2 text-sm">
            <span aria-hidden>{p.solvedAt === null ? '🔒' : KIND_ICON[p.kind]}</span>
            <span className={p.solvedAt === null ? 'text-muted' : 'font-semibold'}>{p.title}</span>
            {p.solvedAt === null ? (
              <span className="text-muted">still locked</span>
            ) : (
              <span className="text-muted">
                {p.solvedBy} · <span className="font-mono">{formatDuration(p.solvedAt)}</span>
              </span>
            )}
            {p.hints > 0 && (
              <span className="text-muted">
                · 💡{p.hints} {p.hints === 1 ? 'hint' : 'hints'}
              </span>
            )}
          </li>
        ))}
      </ul>
    </li>
  )
}

function Tile({ label, value, detail }: { label: string; value: string; detail: string }) {
  return (
    <div className="rounded-xl border border-line bg-surface p-4 text-center">
      <dt className="text-xs tracking-widest text-muted uppercase">{label}</dt>
      <dd className="font-display mt-1 text-3xl">{value}</dd>
      <dd className="mt-1 text-xs text-muted">{detail}</dd>
    </div>
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
