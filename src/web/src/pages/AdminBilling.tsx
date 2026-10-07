import { useEffect, useState, type ReactNode } from 'react'
import { Button, Card, ErrorText, Heading } from '../components/ui'
import { formatPrice, planGames } from '../lib/access'
import { api } from '../lib/api'
import { ago } from '../lib/numbers'
import type { AdminBillingView, AdminPaidRow, BillingPlan } from '../lib/types'

const PLAN_NAMES: Record<BillingPlan, string> = {
  mysteriesMonthly: 'Murder mysteries, monthly',
  mysteriesYearly: 'Murder mysteries, yearly',
  escapeRoomsMonthly: 'Escape rooms, monthly',
  escapeRoomsYearly: 'Escape rooms, yearly',
  bothMonthly: 'Both games, monthly',
  bothYearly: 'Both games, yearly',
  mysteriesPass: 'Party pass: murder mysteries',
  escapeRoomsPass: 'Party pass: escape rooms',
  bothPass: 'Party pass: both games',
}

const STATUS: Record<NonNullable<AdminPaidRow['status']>, string> = {
  active: 'Active',
  trialing: 'In its free days',
  ending: 'Cancelled, paid until the end',
  pastDue: 'Payment failed, retrying',
  ended: 'Ended',
}

const day = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

/** A line of the setup list: a mark and words, never colour alone (as on the Overview). */
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

/**
 * Admin → Plans & billing (#101): whether payments work, each plan with its price as Stripe has it (and anything wrong
 * with it), and every subscription and party pass, linked to the Stripe dashboard. Prices and plans are changed in
 * Stripe and the server's settings, not here: this page is for checking them.
 */
export default function AdminBilling() {
  const [view, setView] = useState<AdminBillingView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  useEffect(() => {
    api.admin.billing().then(setView, (e: Error) => setError(e.message))
  }, [])

  const act = async (key: string, action: () => Promise<unknown>) => {
    setBusy(key)
    setError(null)
    try {
      await action()
      setView(await api.admin.billing(key === 'refresh'))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  if (!view) return error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Loading…</p>

  return (
    <div className="space-y-6">
      <Heading>Plans &amp; billing</Heading>
      <ErrorText>{error}</ErrorText>

      <Card>
        <ul className="space-y-2" data-testid="billing-checks">
          <Check ok={view.enabled} title="Payments">
            {view.problem ??
              (!view.enabled
                ? "are off: nothing is for sale, and hosts get their games from the free trial and from you. To sell plans, set Stripe's keys and prices (see “Payments with Stripe” in docs/deploy-coolify.md)."
                : view.provider === 'Stripe'
                  ? `through Stripe, ${view.liveMode ? 'live: real cards are charged.' : 'in test mode: no real cards are charged.'}`
                  : 'through the fake provider, for testing: no money moves.')}
          </Check>
          {view.enabled && (
            <>
              <Check ok={view.webhooksVerified} title="Webhooks">
                {!view.webhooksVerified
                  ? "can't be checked: set Billing__Stripe__WebhookSecret (the endpoint's signing secret), or payments won't reach hosts' accounts."
                  : view.lastEvent
                    ? `last one ${ago(view.lastEvent.at)} (${view.lastEvent.type}); ${view.eventsThisWeek} this week.`
                    : 'none yet. Stripe sends them to /api/billing/webhook once someone pays.'}
              </Check>
              <Check ok title="Rules">
                {`A party pass lasts ${view.passHours} hours. After a failed renewal, a host keeps their games ${view.graceDays} days while Stripe retries the card. Stripe Tax is ${view.automaticTax ? 'on' : 'off'}.`}
              </Check>
            </>
          )}
        </ul>
      </Card>

      {view.plans.length > 0 && (
        <section className="space-y-3">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <h2 className="font-display text-2xl">Plans</h2>
            <Button variant="ghost" disabled={busy !== null} onClick={() => act('refresh', async () => undefined)}>
              {busy === 'refresh' ? 'Reading…' : 'Read the prices from Stripe again'}
            </Button>
          </div>
          <ul className="grid gap-3 sm:grid-cols-2" data-testid="billing-plans">
            {view.plans.map((p) => (
              <li key={p.id} className={`rounded-xl border p-4 ${p.forSale ? 'border-line bg-surface' : 'border-blood/50 bg-blood/5'}`}>
                <p className="font-semibold">{PLAN_NAMES[p.id]}</p>
                <p className="text-sm text-muted">
                  {p.amount !== null && p.currency ? formatPrice(p.amount, p.currency) : '—'}
                  {p.interval ? ` a ${p.interval}` : p.amount !== null ? ', once' : ''} · <code className="text-xs break-all">{p.priceId}</code>
                </p>
                <p className="mt-2 text-sm">{p.forSale ? '✓ For sale' : `⚠ Not for sale: ${p.problem}`}</p>
              </li>
            ))}
          </ul>
        </section>
      )}

      {view.enabled && (
        <section className="space-y-3">
          <h2 className="font-display text-2xl">Subscriptions and passes</h2>
          {view.paid.length === 0 ? (
            <p className="text-sm text-muted">Nobody has paid yet.</p>
          ) : (
            // A wide table scrolls inside its own box on a phone; the page never scrolls sideways.
            <div className="relative overflow-x-auto rounded-xl border border-line bg-surface">
              <table className="w-full min-w-[44rem] text-left text-sm" data-testid="billing-paid">
                <thead className="text-xs tracking-widest text-muted uppercase">
                  <tr>
                    <th className="px-4 py-3 font-normal">Host</th>
                    <th className="px-3 py-3 font-normal">What</th>
                    <th className="px-3 py-3 font-normal">State</th>
                    <th className="px-3 py-3 font-normal">Until</th>
                    <th className="px-4 py-3 font-normal">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {view.paid.map((row, i) => (
                    <tr key={`${row.userId}:${i}`} className="border-t border-line align-top">
                      <td className="px-4 py-3">
                        <p className="font-semibold text-ink">{row.displayName ?? 'A deleted account'}</p>
                        {row.email && <p className="text-xs text-muted">{row.email}</p>}
                      </td>
                      <td className="px-3 py-3">
                        {row.kind === 'subscription' ? 'Subscription' : 'Party pass'}: {planGames(row)}
                      </td>
                      <td className="px-3 py-3">{row.status ? STATUS[row.status] : '—'}</td>
                      <td className="px-3 py-3 whitespace-nowrap">
                        {row.renewsAt ? `renews ${day(row.renewsAt)}` : row.endsAt ? `${row.inEffect ? '' : 'ended '}${day(row.endsAt)}` : '—'}
                      </td>
                      <td className="px-4 py-3 text-right whitespace-nowrap">
                        {row.dashboardUrl && (
                          <a href={row.dashboardUrl} target="_blank" rel="noreferrer" className="text-accent underline">
                            Stripe ↗
                          </a>
                        )}
                        {row.displayName && (
                          <button
                            type="button"
                            className="ml-3 text-accent underline disabled:opacity-50"
                            disabled={busy !== null}
                            onClick={() => act(`sync:${row.userId}`, () => api.admin.syncBilling(row.userId))}
                            aria-label={`Check ${row.displayName}'s payments with Stripe`}
                          >
                            {busy === `sync:${row.userId}` ? 'Checking…' : 'Sync'}
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="text-xs text-muted">
            Sync reads a host's subscription from Stripe now, for when a webhook went missing. Refunds and plan changes are made in the Stripe dashboard
            or by the host on Stripe's billing page.
          </p>
        </section>
      )}
    </div>
  )
}
