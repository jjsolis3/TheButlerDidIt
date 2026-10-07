import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { Bar, Feel, Section, Stat } from '../components/Stats'
import { ErrorText, Heading } from '../components/ui'
import { api } from '../lib/api'
import { bytes, money } from '../lib/numbers'
import type { AccessPlan, AdminOverview as Overview, AiRole } from '../lib/types'

const PLANS: Record<AccessPlan, string> = {
  trial: 'Free trial',
  trialEnded: 'Trial ended',
  free: 'Free access',
  subscription: 'Subscription',
  pass: 'Party pass',
  none: 'No plan',
  admin: 'Admin',
}

const ROLES: Record<AiRole, string> = {
  storyteller: 'Storyteller',
  actor: 'Actor',
  inspector: 'Inspector',
  voice: 'Voice',
  illustrator: 'Illustrator',
  filmmaker: 'Filmmaker',
}

const weekLabel = (iso: string) => new Date(`${iso}T00:00:00Z`).toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' })
const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/**
 * Games played to the end each week, as columns: one series (the total), so no legend. The split between mysteries
 * and escape rooms is in each column's label, which screen readers read, and in its tooltip.
 */
function Weeks({ weeks }: { weeks: Overview['weeks'] }) {
  const max = Math.max(1, ...weeks.map((w) => w.mysteries + w.escapeRooms))
  return (
    <ol className="grid h-44 grid-cols-8 items-end gap-1.5 sm:gap-3" data-testid="weekly-games">
      {weeks.map((w, i) => {
        const total = w.mysteries + w.escapeRooms
        const label = `${i === weeks.length - 1 ? 'This week' : `Week of ${weekLabel(w.week)}`}: ${plural(total, 'game')} (${plural(w.mysteries, 'mystery', 'mysteries')}, ${plural(w.escapeRooms, 'escape room')})`
        return (
          <li key={w.week} aria-label={label} title={label} className="flex h-full flex-col items-center justify-end gap-1">
            <span className="text-xs text-muted tabular-nums">{total || ''}</span>
            <span
              className="w-full rounded-t-[4px]"
              style={{ height: `${(total / max) * 100}%`, minHeight: total ? 4 : 1, background: total ? 'var(--theme-accent)' : 'var(--color-line)' }}
            />
            {/* Eight dates don't fit side by side on a phone: there, every other one, counting back from "Now". */}
            <span className={`text-[11px] whitespace-nowrap text-muted ${(weeks.length - 1 - i) % 2 ? 'max-sm:invisible' : ''}`}>
              {i === weeks.length - 1 ? 'Now' : weekLabel(w.week)}
            </span>
          </li>
        )
      })}
    </ol>
  )
}

/** A line of the server checklist: a mark and words, never colour alone. */
function Check({ ok, title, children }: { ok: boolean; title: string; children: ReactNode }) {
  return (
    <li className="flex gap-3 text-sm">
      <span aria-hidden className={ok ? 'text-accent' : 'text-red-300'}>
        {ok ? '✓' : '⚠'}
      </span>
      <span>
        <span className="font-semibold">{title}</span> <span className="text-muted">{children}</span>
      </span>
    </li>
  )
}

/** Admin → Overview: how the site is doing this week, and anything in its setup that needs attention. */
export default function AdminOverview() {
  const [view, setView] = useState<Overview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.admin.overview().then(setView, (e: Error) => setError(e.message))
  }, [])

  if (!view) return error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Counting…</p>

  const { hosts, parties, ai, ratings, signUps, server } = view
  const thisWeek = view.weeks[view.weeks.length - 1]
  const voted = ratings.tooEasy + ratings.justRight + ratings.tooHard
  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-baseline justify-between gap-3">
        <Heading>Overview</Heading>
        {parties.liveNow > 0 && (
          <p className="rounded-full border border-accent/50 bg-accent/10 px-3 py-1 text-sm" data-testid="live-now">
            <span aria-hidden>● </span>
            {plural(parties.liveNow, 'game')} under way right now
          </p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4" data-testid="overview-stats">
        <Stat label="Hosts" value={hosts.total} detail={`${hosts.newThisWeek} new this week · ${hosts.active} hosted this month`} />
        <Stat
          label="Parties this week"
          value={parties.mysteries + parties.escapeRooms}
          detail={`🔎 ${parties.mysteries} · 🔐 ${parties.escapeRooms} · ${plural(parties.guests, 'guest')}`}
        />
        <Stat label="Rating, 30 days" value={ratings.average === null ? '—' : `★ ${ratings.average}`} detail={plural(ratings.count, 'rating')} />
        <Stat
          label="AI this month"
          value={money(ai.spentUsd)}
          detail={`${plural(ai.calls, 'call')}${ai.failed ? ` · ${ai.failed} failed` : ''}${ai.budgetPerHostUsd > 0 ? ` · ${money(ai.budgetPerHostUsd)} per host` : ''}`}
        />
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <Section
          title="Games played"
          hint={`Each week (Monday to Sunday, UTC), the mysteries that reached the reveal and the escape rooms whose clock stopped. This week so far: ${plural(thisWeek.mysteries, 'mystery', 'mysteries')}, ${plural(thisWeek.escapeRooms, 'escape room')}.`}
        >
          <Weeks weeks={view.weeks} />
        </Section>

        <Section title="Plans" hint="What each host has now. You don't count: you have everything.">
          {view.plans.length === 0 ? (
            <p className="text-sm text-muted">No hosts yet besides you.</p>
          ) : (
            <ul className="space-y-1.5" data-testid="plans">
              {view.plans.map((p) => (
                <Bar key={p.plan} label={PLANS[p.plan]} value={p.hosts} max={Math.max(...view.plans.map((x) => x.hosts))} />
              ))}
            </ul>
          )}
          <p className="text-sm">
            <Link to="/admin/hosts" className="text-accent underline">
              Hosts
            </Link>
            <span className="text-muted"> gives or removes free access.</span>
          </p>
        </Section>

        <Section title="How games felt" hint={`Guests' votes from their phones in the last 30 days${voted ? '' : ', once a game ends'}.`}>
          <Feel votes={ratings} />
          <p className="text-sm">
            <Link to="/admin/games" className="text-accent underline">
              Games
            </Link>
            <span className="text-muted"> shows each mystery and room, and which ones need a look.</span>
          </p>
        </Section>

        <Section title="Sign-ups">
          <p data-testid="signups-mode">
            <span className="font-semibold">{signUps.open ? '🌐 Anyone can sign up' : '✉️ Invites only'}</span>
            <span className="text-sm text-muted">
              {' '}
              · {signUps.switch === null ? "as the server's settings say" : 'your choice'} · {plural(signUps.openInvites, 'invite')} waiting
            </span>
          </p>
          <p className="text-sm">
            <Link to="/admin/signups" className="text-accent underline">
              Sign-ups
            </Link>
            <span className="text-muted"> switches it and makes invite links.</span>
          </p>
        </Section>
      </div>

      <Section title="Server" hint="How this server is set up. These come from its environment settings (in Coolify, the app's Environment Variables).">
        <ul className="space-y-2" data-testid="server-checks">
          <Check ok={server.emailEnabled} title="Email">
            {server.emailEnabled
              ? 'is set up: hosts get confirmation and password-reset emails.'
              : "isn't set up, so hosts who forget their password need a reset link from you (Hosts). Set Email__Host, Email__Username, Email__Password and Email__From."}
          </Check>
          <Check ok={server.aiProviders > 0 && server.aiRoles.length > 0} title="AI">
            {server.aiProviders === 0 ? (
              <>
                has no provider, so the AI game master is off.{' '}
                <Link to="/admin/ai" className="underline">
                  Add one
                </Link>
                .
              </>
            ) : server.aiRoles.length === 0 ? (
              <>
                has {plural(server.aiProviders, 'provider')} but no roles chosen.{' '}
                <Link to="/admin/ai" className="underline">
                  Choose who does what
                </Link>
                .
              </>
            ) : (
              `${plural(server.aiProviders, 'provider')}; playing ${server.aiRoles.map((r) => ROLES[r]).join(', ')}.`
            )}
          </Check>
          <Check ok title="Media">
            {`${bytes(server.mediaBytes)} in ${plural(server.mediaFiles, 'file')}, ${server.mediaStorage === 'S3' ? 'in S3 storage' : "in the server's media folder (back it up with the database)"}.`}
          </Check>
          <Check ok={!server.paymentsProblem} title="Payments">
            {server.paymentsProblem ? (
              server.paymentsProblem
            ) : server.payments === null ? (
              <>
                are off: hosts get their games from the free trial and from you.{' '}
                <Link to="/admin/billing" className="underline">
                  Selling plans
                </Link>
              </>
            ) : server.payments === 'Stripe' ? (
              `through Stripe, ${server.paymentsLive ? 'live: real cards are charged.' : 'in test mode: no real cards are charged.'}`
            ) : (
              'through the fake provider, for testing: no money moves.'
            )}
          </Check>
          <Check ok title="Servers">
            {server.severalServers ? 'Several, sharing one database (Scale__MultiInstance).' : 'One.'}
          </Check>
        </ul>
      </Section>
    </div>
  )
}
