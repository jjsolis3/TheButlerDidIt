import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import { formatPrice, planGames } from '../lib/access'
import { api } from '../lib/api'
import type { AccessView, BillingView, PlanInterval, PlanOfferView } from '../lib/types'
import { announceMeChanged } from '../lib/useMe'
import { Button, Card, ErrorText } from './ui'

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/** "$12.00 a month", "$120.00 a year", "$5.00". */
const priceLine = (p: PlanOfferView) =>
  p.amount === null || !p.currency ? '' : `${formatPrice(p.amount, p.currency)}${p.interval ? ` a ${p.interval}` : ''}`

const ICON = (p: Pick<PlanOfferView, 'mysteries' | 'escapeRooms'>) => (p.mysteries && p.escapeRooms ? '🔍🔐' : p.mysteries ? '🔍' : '🔐')

/**
 * The account page's plans (#101): what the site sells, and the way to Stripe's own pages. Buying happens on Stripe's
 * payment page (card numbers never reach this site), and changing, cancelling and invoices on Stripe's billing page.
 * This only sends the browser there; the server decides the host's access from what Stripe tells it.
 */
export function BillingPanel({ access }: { access: AccessView }) {
  const [view, setView] = useState<BillingView | null>(null)
  const [error, setError] = useState<string | null>(null)
  /** Which button is on its way to Stripe. */
  const [going, setGoing] = useState<string | null>(null)
  const [every, setEvery] = useState<PlanInterval>('month')

  useEffect(() => {
    api.billing.get().then(
      (v) => {
        setView(v)
        // Start on whichever the site sells, monthly first.
        if (!v.plans.some((p) => p.kind === 'subscription' && p.interval === 'month')) setEvery('year')
      },
      (e: Error) => setError(e.message),
    )
  }, [access])

  const go = async (key: string, where: () => Promise<{ url: string }>) => {
    setGoing(key)
    setError(null)
    try {
      window.location.assign((await where()).url)
    } catch (e) {
      setError((e as Error).message)
      setGoing(null)
    }
  }

  if (!view) return error ? <ErrorText>{error}</ErrorText> : null
  if (!view.enabled || access.plan === 'admin') return null

  const manage = (label: string, variant: 'primary' | 'ghost' = 'primary') => (
    <Button variant={variant} disabled={going !== null} onClick={() => go('portal', api.billing.portal)}>
      {going === 'portal' ? 'Opening…' : label}
    </Button>
  )
  const subscriptions = view.plans.filter((p) => p.kind === 'subscription')
  const intervals = (['month', 'year'] as const).filter((i) => subscriptions.some((p) => p.interval === i))
  // A pass only for games the host can't already start: one for a game they have would add nothing.
  const passes = view.plans.filter((p) => p.kind === 'pass' && ((p.mysteries && !access.mysteries) || (p.escapeRooms && !access.escapeRooms)))
  // Free access from the admin covers everything already.
  const offerSubscriptions = !view.subscribed && access.plan !== 'free' && subscriptions.length > 0

  if (!view.subscribed && !offerSubscriptions && passes.length === 0 && !view.canManage) return null

  return (
    <section id="plans" className="mt-10 scroll-mt-20" aria-labelledby="plans-title">
      <h2 id="plans-title" className="font-display mb-3 text-2xl">
        Plans
      </h2>
      <div className="space-y-3">
        {view.subscribed && (
          <Card className="space-y-3">
            <p className="text-sm">Change your plan, update your card, see your invoices or cancel, on Stripe's secure billing page.</p>
            {manage('Manage billing')}
          </Card>
        )}

        {offerSubscriptions && (
          <Card className="space-y-4">
            {intervals.length > 1 && (
              <div role="radiogroup" aria-label="How often to pay" className="inline-flex rounded-full border border-line p-1">
                {intervals.map((i) => (
                  <button
                    key={i}
                    type="button"
                    role="radio"
                    aria-checked={every === i}
                    onClick={() => setEvery(i)}
                    className={`min-h-11 rounded-full px-4 text-sm ${every === i ? 'bg-accent font-semibold text-bg' : 'text-muted hover:text-ink'}`}
                  >
                    {i === 'month' ? 'Monthly' : 'Yearly'}
                  </button>
                ))}
              </div>
            )}
            {access.plan === 'trial' && <p className="text-sm text-muted">Subscribe now and your first payment waits until your free trial ends.</p>}
            <ul className="grid gap-3 sm:grid-cols-3" aria-label="Subscriptions">
              {subscriptions
                .filter((p) => p.interval === every)
                .map((p) => (
                  <li key={p.id} className="flex flex-col rounded-xl border border-line bg-bg p-4">
                    <span aria-hidden className="text-2xl">
                      {ICON(p)}
                    </span>
                    <span className="mt-1 font-semibold">{planGames(p)}</span>
                    <span className="text-sm text-muted">{priceLine(p)}</span>
                    <Button className="mt-3" disabled={going !== null} onClick={() => go(p.id, () => api.billing.checkout(p.id))} aria-label={`Choose ${planGames(p)}, ${priceLine(p)}`}>
                      {going === p.id ? 'Opening checkout…' : 'Choose'}
                    </Button>
                  </li>
                ))}
            </ul>
            <p className="text-xs text-muted">Payments are taken by Stripe. Cancel any time from Manage billing: you keep your games until the end of what you've paid for.</p>
          </Card>
        )}

        {passes.length > 0 && (
          <Card className="space-y-3">
            <h3 className="font-semibold">🎟️ Party pass</h3>
            <p className="text-sm text-muted">
              {view.passHours} hours of hosting from the moment you pay. Paid once: nothing to cancel.
            </p>
            <div className="flex flex-wrap gap-2">
              {passes.map((p) => (
                <Button key={p.id} variant="ghost" disabled={going !== null} onClick={() => go(p.id, () => api.billing.checkout(p.id))}>
                  {going === p.id ? 'Opening checkout…' : `${ICON(p)} ${planGames(p)} · ${priceLine(p)}`}
                </Button>
              ))}
            </div>
          </Card>
        )}

        {view.canManage && !view.subscribed && <div>{manage('Your invoices and card', 'ghost')}</div>}
        <ErrorText>{error}</ErrorText>
      </div>
    </section>
  )
}

type Return = 'checking' | 'paid' | 'waiting' | 'cancelled'

const RETURN_TEXT: Record<Return, string> = {
  checking: 'Confirming your payment with Stripe…',
  paid: 'Payment confirmed. Thank you, and enjoy the games!',
  waiting:
    "Stripe is still confirming your payment. Your plan shows here as soon as it's through: usually a minute, a few days for a bank transfer. You can leave this page.",
  cancelled: 'Checkout cancelled. Nothing was charged.',
}

/**
 * Back from Stripe's payment page (?billing=done&session=…) or billing page (?billing=back). The address proves
 * nothing, so this asks the server to check with Stripe, a few times while the payment settles, and then tidies the
 * address so a reload doesn't repeat it.
 */
export function BillingReturn({ onChanged }: { onChanged: () => void }) {
  const [params, setParams] = useSearchParams()
  // What to say first comes straight from the address; the checking below changes it.
  const [state, setState] = useState<Return | null>(() =>
    params.get('billing') === 'done' ? 'checking' : params.get('billing') === 'cancelled' ? 'cancelled' : null,
  )
  // Once per visit: tidying the address changes `params`, which runs this effect again, and that run must do nothing.
  const started = useRef(false)

  useEffect(() => {
    const billing = params.get('billing')
    if (started.current || !billing) return
    started.current = true
    const session = params.get('session')
    setParams({}, { replace: true })
    if (billing === 'cancelled') return
    void (async () => {
      for (let attempt = 0; attempt < 10; attempt++) {
        try {
          const result = await api.billing.sync(billing === 'done' ? session : null)
          // Back from the billing page, one check is enough: a change there is already made.
          if (billing !== 'done' || result.paid) {
            if (billing === 'done') setState('paid')
            announceMeChanged()
            onChanged()
            return
          }
        } catch {
          /* Stripe or the network hiccuped: try again */
        }
        await sleep(3000)
      }
      if (billing === 'done') setState('waiting')
    })()
  }, [params, setParams, onChanged])

  if (!state) return null
  return (
    <p role="status" className="mt-6 rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm text-ink" data-testid="billing-return">
      {RETURN_TEXT[state]}
    </p>
  )
}
