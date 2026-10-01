import { useEffect, useState, type FormEvent } from 'react'
import { Button, ErrorText, Field, inputClass } from '../components/ui'
import { api } from '../lib/api'
import type { AuthOptions, CreatedInvite, InviteView } from '../lib/types'

const LENGTHS = [
  { days: 1, label: '1 day' },
  { days: 7, label: '1 week' },
  { days: 30, label: '30 days' },
]

const date = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

/** Who an invite is for, as the admin described it. */
const label = (i: InviteView) => [i.note, i.email].filter(Boolean).join(' · ') || 'Anyone with the link'

function describe(i: InviteView) {
  if (i.status === 'used') return `Used by ${i.usedBy ?? 'an account since deleted'}${i.usedAt ? ` on ${date(i.usedAt)}` : ''}`
  if (i.status === 'expired') return `Expired on ${date(i.expiresAt)}`
  return `Works once, until ${date(i.expiresAt)}`
}

/** A new invite as the page shows it: whether it was meant to be emailed, and whether the copy worked. */
type Made = CreatedInvite & { meantToSend: boolean; copied: boolean }

/**
 * Admin: invite links. While sign-ups are closed (ALLOW_REGISTRATION=false), an invite is how a new
 * host gets an account. Each link works once. The server keeps only a hash of it, so the link can
 * be copied only right after it's made.
 */
export function InvitesPanel() {
  const [invites, setInvites] = useState<InviteView[] | null>(null)
  const [options, setOptions] = useState<AuthOptions | null>(null)
  const [note, setNote] = useState('')
  const [email, setEmail] = useState('')
  const [days, setDays] = useState(7)
  const [send, setSend] = useState(true)
  // Family and friends: both games free for good, instead of the free trial.
  const [freeAccess, setFreeAccess] = useState(false)
  const [made, setMade] = useState<Made | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.admin.invites().then(
      (i) => !cancelled && setInvites(i),
      (e: Error) => !cancelled && setError(e.message),
    )
    api.authOptions().then(
      (o) => !cancelled && setOptions(o),
      () => {},
    )
    return () => {
      cancelled = true
    }
  }, [])

  // Sending needs email set up on the server, and an address to send to.
  const canSend = !!options?.emailEnabled && email.trim() !== ''

  const create = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setMade(null)
    try {
      const meantToSend = canSend && send
      const created = await api.admin.createInvite({ email: email.trim() || null, note: note.trim() || null, days, send: meantToSend, freeAccess })
      // The clipboard can refuse (an insecure page, a strict browser), so only say "copied" when it worked.
      const copied = (await navigator.clipboard?.writeText(created.link).then(
        () => true,
        () => false,
      )) ?? false
      setMade({ ...created, meantToSend, copied })
      setInvites((all) => [created.invite, ...(all ?? [])])
      setNote('')
      setEmail('')
      setFreeAccess(false)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const remove = async (i: InviteView) => {
    if (i.status === 'pending' && !window.confirm(`Revoke the invite for ${label(i)}? The link stops working.`)) return
    setError(null)
    try {
      await api.admin.deleteInvite(i.id)
      setInvites((all) => all?.filter((x) => x.id !== i.id) ?? null)
      if (made?.invite.id === i.id) setMade(null)
    } catch (err) {
      setError((err as Error).message)
    }
  }

  return (
    <section className="mb-10">
      <h2 className="font-display mb-1 text-2xl">Invites</h2>
      <p className="mb-4 max-w-2xl text-sm text-muted">
        {options?.allowRegistration === false
          ? 'Sign-ups are invite-only. Make a link for each new host and send it to them. Each link makes one account.'
          : 'Anyone can sign up right now. Invites still work, for example one that only a single email address can use.'}
      </p>

      <form onSubmit={create} className="space-y-4 rounded-xl border border-line bg-surface p-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Who's it for?" hint="Only you see this.">
            <input className={inputClass} value={note} onChange={(e) => setNote(e.target.value)} maxLength={80} placeholder="Ana, my sister" />
          </Field>
          <Field label="Their email (optional)" hint="Then only this address can use the link.">
            <input className={inputClass} type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={256} />
          </Field>
        </div>
        <div className="flex flex-wrap items-end gap-4">
          <Field label="The link works for">
            <select className={inputClass} value={days} onChange={(e) => setDays(Number(e.target.value))}>
              {LENGTHS.map((l) => (
                <option key={l.days} value={l.days}>
                  {l.label}
                </option>
              ))}
            </select>
          </Field>
          <label className="flex min-h-11 items-center gap-2 text-sm">
            <input type="checkbox" checked={freeAccess} onChange={(e) => setFreeAccess(e.target.checked)} />
            Free access for good (instead of the free trial)
          </label>
          {options?.emailEnabled && (
            <label className="flex min-h-11 items-center gap-2 text-sm">
              <input type="checkbox" checked={canSend && send} disabled={!email.trim()} onChange={(e) => setSend(e.target.checked)} />
              Email it to them
            </label>
          )}
          <Button type="submit" disabled={busy} className="sm:ml-auto">
            Make an invite link
          </Button>
        </div>
      </form>

      <div className="mt-3">
        <ErrorText>{error}</ErrorText>
      </div>

      {made && (
        <div className="mt-3 space-y-1">
          <p className="text-xs text-muted">
            {made.emailed
              ? `Emailed to ${made.invite.email}. `
              : made.meantToSend
                ? "The email couldn't be sent, so send the link yourself. "
                : 'Send this link to them by text or chat. '}
            {made.copied ? "It's copied to your clipboard. " : ''}
            This is the only time it's shown.
          </p>
          <input
            readOnly
            value={made.link}
            aria-label="New invite link"
            onFocus={(e) => e.target.select()}
            className="w-full rounded-lg border border-line bg-bg px-3 py-2 font-mono text-xs"
          />
        </div>
      )}

      {invites && invites.length > 0 && (
        <ul className="mt-4 space-y-2">
          {invites.map((i) => (
            <li key={i.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line bg-surface px-4 py-3">
              <div className="min-w-0">
                <p className="truncate font-semibold">{label(i)}</p>
                <p className="text-sm text-muted">
                  {describe(i)}
                  {i.freeAccess && ' · free access'}
                </p>
              </div>
              <Button variant="quiet" onClick={() => remove(i)} aria-label={`${i.status === 'pending' ? 'Revoke' : 'Remove'} the invite for ${label(i)}`}>
                {i.status === 'pending' ? 'Revoke' : 'Remove'}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
