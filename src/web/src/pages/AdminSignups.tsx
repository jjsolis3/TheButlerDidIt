import { useEffect, useState } from 'react'
import { Card, ErrorText, Heading } from '../components/ui'
import { api } from '../lib/api'
import type { SignUpsView } from '../lib/types'
import { InvitesPanel } from './AdminInvites'

const CHOICES = [
  { open: true, title: '🌐 Anyone', detail: 'Anyone with the address can make a host account, and starts on the free trial.' },
  { open: false, title: '✉️ Invites only', detail: 'New hosts need an invite link from you. Each link makes one account.' },
]

/**
 * Admin → Sign-ups: who can make a host account, and the invite links. The switch takes effect at once on every
 * server, with no redeploy; "Use the server's setting" hands the choice back to Auth__AllowRegistration.
 */
export default function AdminSignups() {
  const [view, setView] = useState<SignUpsView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.admin.signUps().then(setView, (e: Error) => setError(e.message))
  }, [])

  const choose = async (open: boolean | null) => {
    setBusy(true)
    setError(null)
    try {
      setView(await api.admin.setSignUps(open))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-6">
      <Heading>Sign-ups</Heading>
      <ErrorText>{error}</ErrorText>
      {view && (
        <Card className="space-y-4">
          <div>
            <h2 className="font-display text-2xl">Who can make a host account?</h2>
            <p className="text-sm text-muted">Guests never need an account: they join a party with its code. This is only about hosts.</p>
          </div>
          <div className="grid gap-3 sm:grid-cols-2" role="radiogroup" aria-label="Who can sign up">
            {CHOICES.map((c) => (
              <button
                key={String(c.open)}
                type="button"
                role="radio"
                aria-checked={view.open === c.open}
                disabled={busy}
                onClick={() => view.open !== c.open && choose(c.open)}
                className={`rounded-xl border p-4 text-left transition ${view.open === c.open ? 'border-accent bg-accent/10' : 'border-line bg-bg hover:border-accent/60'}`}
              >
                <span className="block font-semibold">{c.title}</span>
                <span className="mt-1 block text-sm text-muted">{c.detail}</span>
              </button>
            ))}
          </div>
          <p className="text-sm text-muted" data-testid="signups-source">
            {view.switch === null ? (
              <>This is the server's setting (Auth__AllowRegistration). Choosing here overrides it straight away, with no redeploy.</>
            ) : (
              <>
                Your choice, which overrides the server's setting ({view.serverSetting ? 'anyone' : 'invites only'}).{' '}
                <button type="button" className="text-accent underline" disabled={busy} onClick={() => choose(null)}>
                  Use the server's setting
                </button>
              </>
            )}
          </p>
          <p className="text-sm text-muted">
            {view.requireConfirmedEmail
              ? 'New hosts confirm their email address before they can create a party.'
              : view.emailEnabled
                ? "New hosts are sent a confirmation email, but don't have to click it to start (Auth__RequireConfirmedEmail turns that on)."
                : "This server can't send email, so new hosts' addresses aren't confirmed."}
          </p>
        </Card>
      )}
      {/* The invites panel says whether invites are needed; it follows the switch above. */}
      <InvitesPanel open={view?.open} />
    </div>
  )
}
