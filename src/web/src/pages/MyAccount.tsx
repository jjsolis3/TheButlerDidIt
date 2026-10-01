import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Field, Heading, inputClass, Shell } from '../components/ui'
import { describeAccess } from '../lib/access'
import { api } from '../lib/api'
import type { AccountView } from '../lib/types'
import { announceMeChanged, useMe } from '../lib/useMe'

const money = (usd: number) => `$${usd.toFixed(2)}`

/** One form's state: working, its error, and its "done" message. Each form has its own, so one form's error never hides another's. */
function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const run = async (action: () => Promise<string | void>) => {
    setBusy(true)
    setError(null)
    setDone(null)
    try {
      const message = await action()
      if (message) setDone(message)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return { busy, error, done, run }
}

function Done({ children }: { children: ReactNode }) {
  if (!children) return null
  return (
    <p role="status" className="rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm text-ink">
      {children}
    </p>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-10">
      <h2 className="font-display mb-3 text-2xl">{title}</h2>
      {children}
    </section>
  )
}

/**
 * The host's account (/account): their plan, this month's use, what they've made, and their
 * details. Every change goes to /api/account, which only ever acts on whoever is signed in.
 */
export default function MyAccount() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [account, setAccount] = useState<AccountView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (me === null) navigate(`/login?next=${encodeURIComponent('/account')}`)
    if (me) api.account.get().then(setAccount, (e: Error) => setError(e.message))
  }, [me, navigate])

  if (!account)
    return (
      <Shell>
        <Heading className="mt-8">Your account</Heading>
        <div className="mt-4">{error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Loading…</p>}</div>
      </Shell>
    )

  return (
    <Shell>
      <Eyebrow>Your account</Eyebrow>
      <Heading className="mt-2">{account.displayName}</Heading>
      <p className="mt-1 text-muted">
        {account.email}
        {account.isAdmin && <span className="text-accent"> · admin</span>}
      </p>

      <div className="mt-8 grid gap-3 sm:grid-cols-2">
        <Card>
          <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">Your plan</h2>
          <p className="font-display mt-2 text-xl">{describeAccess(account.access).title}</p>
          <ul className="mt-2 space-y-1 text-sm" aria-label="Games in your plan">
            <li className={account.access.mysteries ? '' : 'text-muted'}>{account.access.mysteries ? '✓' : '🔒'} 🔍 Murder mysteries</li>
            <li className={account.access.escapeRooms ? '' : 'text-muted'}>{account.access.escapeRooms ? '✓' : '🔒'} 🔐 Escape rooms</li>
          </ul>
          <p className="mt-3 text-xs text-muted">{describeAccess(account.access).detail}</p>
        </Card>
        <Card>
          <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">This month</h2>
          <p className="mt-2 text-sm">
            {account.usage.mysteriesThisMonth} {account.usage.mysteriesThisMonth === 1 ? 'mystery' : 'mysteries'} and{' '}
            {account.usage.escapeRoomsThisMonth} escape {account.usage.escapeRoomsThisMonth === 1 ? 'room' : 'rooms'} hosted
          </p>
          <p className="text-xs text-muted">{account.usage.partiesAllTime} parties since you joined</p>
          <AiSpend spent={account.usage.aiSpentThisMonthUsd} budget={account.usage.aiBudgetUsd} />
        </Card>
      </div>

      <Section title="What you've made">
        <ul className="grid gap-2 sm:grid-cols-2">
          <LibraryLink to="/" label="Your parties" count={account.library.parties} />
          <LibraryLink to="/mysteries" label="Your mysteries" count={account.library.mysteries} />
          <LibraryLink to="/host/new?game=escape" label="Escape rooms the AI wrote for you" count={account.library.escapeRooms} />
          <li className="rounded-xl border border-line bg-surface px-4 py-3">
            <span className="font-semibold">{account.library.escapes}</span> <span className="text-muted">successful escapes</span>
          </li>
        </ul>
      </Section>

      <Section title="Your details">
        <div className="space-y-4">
          <NameForm account={account} onChange={(displayName) => setAccount({ ...account, displayName })} />
          <EmailForm account={account} onChange={(email) => setAccount({ ...account, email, emailConfirmed: true })} />
          <PasswordForm />
        </div>
      </Section>

      <Section title="Signed-in devices">
        <SignOutEverywhere />
      </Section>

      <Section title="Your data">
        <DangerZone account={account} />
      </Section>
    </Shell>
  )
}

function AiSpend({ spent, budget }: { spent: number; budget: number }) {
  if (budget <= 0 && spent === 0) return null
  const share = budget > 0 ? Math.min(1, spent / budget) : 0
  return (
    <div className="mt-3">
      <p className="text-sm">
        AI: {money(spent)}
        {budget > 0 && <span className="text-muted"> of {money(budget)}</span>}
      </p>
      {budget > 0 && (
        <div className="mt-1 h-2 overflow-hidden rounded-full bg-bg" role="meter" aria-label="AI spend this month" aria-valuemin={0} aria-valuemax={budget} aria-valuenow={spent}>
          <div className="h-full rounded-full bg-accent" style={{ width: `${share * 100}%` }} />
        </div>
      )}
    </div>
  )
}

function LibraryLink({ to, label, count }: { to: string; label: string; count: number }) {
  return (
    <li>
      <Link to={to} className="block rounded-xl border border-line bg-surface px-4 py-3 transition hover:border-accent">
        <span className="font-semibold">{count}</span> <span className="text-muted">{label}</span> <span aria-hidden="true">→</span>
      </Link>
    </li>
  )
}

function NameForm({ account, onChange }: { account: AccountView; onChange: (name: string) => void }) {
  const [name, setName] = useState(account.displayName)
  const { busy, error, done, run } = useAction()
  const submit = (e: FormEvent) => {
    e.preventDefault()
    run(async () => {
      const me = await api.account.rename(name)
      onChange(me.displayName)
      announceMeChanged() // the header shows the new name too
      return 'Saved.'
    })
  }
  return (
    <Card>
      <form onSubmit={submit} className="space-y-3">
        <Field label="Your name" hint="Guests see it on invitations and the party screen.">
          <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} required maxLength={60} autoComplete="name" />
        </Field>
        <ErrorText>{error}</ErrorText>
        <Done>{done}</Done>
        <Button type="submit" disabled={busy || name.trim() === account.displayName}>
          Save name
        </Button>
      </form>
    </Card>
  )
}

function EmailForm({ account, onChange }: { account: AccountView; onChange: (email: string) => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const { busy, error, done, run } = useAction()
  const submit = (e: FormEvent) => {
    e.preventDefault()
    run(async () => {
      const result = await api.account.changeEmail(email, password)
      if (!result.pending) {
        onChange(result.me.email)
        announceMeChanged()
      }
      setEmail('')
      setPassword('')
      return result.message
    })
  }
  return (
    <Card>
      <form onSubmit={submit} className="space-y-3">
        <h3 className="font-semibold">Email address</h3>
        <p className="text-sm">
          <span className="text-muted">Now:</span> {account.email}{' '}
          {account.emailEnabled && <span className="text-xs text-muted">({account.emailConfirmed ? 'confirmed' : 'not confirmed yet'})</span>}
        </p>
        <Field
          label="New email address"
          hint={account.emailEnabled ? "We'll send a link to the new address. It changes when you click it." : 'It changes straight away. Sign in with it from then on.'}
        >
          <input className={inputClass} type="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={256} autoComplete="email" />
        </Field>
        <Field label="Your current password">
          <input className={inputClass} type="password" value={password} onChange={(e) => setPassword(e.target.value)} required autoComplete="current-password" />
        </Field>
        <ErrorText>{error}</ErrorText>
        <Done>{done}</Done>
        <Button type="submit" disabled={busy}>
          Change email
        </Button>
      </form>
    </Card>
  )
}

function PasswordForm() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const { busy, error, done, run } = useAction()
  const submit = (e: FormEvent) => {
    e.preventDefault()
    run(async () => {
      await api.account.changePassword(current, next)
      setCurrent('')
      setNext('')
      return 'Password changed. Any other device signed in to your account is signed out within a minute.'
    })
  }
  return (
    <Card>
      <form onSubmit={submit} className="space-y-3">
        <h3 className="font-semibold">Password</h3>
        <Field label="Current password">
          <input className={inputClass} type="password" value={current} onChange={(e) => setCurrent(e.target.value)} required autoComplete="current-password" />
        </Field>
        <Field label="New password" hint="At least 8 characters.">
          <input className={inputClass} type="password" value={next} onChange={(e) => setNext(e.target.value)} required minLength={8} autoComplete="new-password" />
        </Field>
        <ErrorText>{error}</ErrorText>
        <Done>{done}</Done>
        <Button type="submit" disabled={busy}>
          Change password
        </Button>
      </form>
    </Card>
  )
}

function SignOutEverywhere() {
  const { busy, error, done, run } = useAction()
  return (
    <Card>
      <p className="text-sm">
        Signed in on a shared computer, or lost a phone? This signs out every other device within a minute. This one stays signed in.
      </p>
      <div className="mt-3 space-y-3">
        <ErrorText>{error}</ErrorText>
        <Done>{done}</Done>
        <Button
          variant="ghost"
          disabled={busy}
          onClick={() =>
            run(async () => {
              await api.account.signOutEverywhere()
              return 'Done. Every other device will be signed out within a minute.'
            })
          }
        >
          Sign out everywhere else
        </Button>
      </div>
    </Card>
  )
}

function DangerZone({ account }: { account: AccountView }) {
  const [password, setPassword] = useState('')
  const [sure, setSure] = useState(false)
  const { busy, error, run } = useAction()
  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (!window.confirm('Delete your account and everything you made? This can’t be undone.')) return
    run(async () => {
      await api.account.remove(password)
      // A full page load, so nothing on screen still thinks you're signed in.
      window.location.assign('/')
    })
  }
  return (
    <div className="space-y-3">
      <Card>
        <p className="text-sm">A copy of what your account holds: your details, your parties, the mysteries and rooms you've made, and your escapes.</p>
        <a href={api.account.exportUrl} download className="mt-3 inline-block text-sm text-accent underline">
          Download my data (JSON)
        </a>
      </Card>
      <div className="rounded-xl border border-blood/50 bg-blood/5 p-5">
        <h3 className="font-semibold text-red-200">Delete my account</h3>
        {account.isAdmin ? (
          <p className="mt-2 text-sm text-muted">The admin account runs this site, so it can't be deleted here.</p>
        ) : (
          <form onSubmit={submit} className="mt-2 space-y-3">
            <p className="text-sm text-muted">
              Deletes your account, your parties (with guests' selfies), and the mysteries and escape rooms you made. Leaderboard times stay,
              without your guests' names.
            </p>
            <Field label="Your password">
              <input className={inputClass} type="password" value={password} onChange={(e) => setPassword(e.target.value)} required autoComplete="current-password" />
            </Field>
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-1" checked={sure} onChange={(e) => setSure(e.target.checked)} />
              I understand this can't be undone.
            </label>
            <ErrorText>{error}</ErrorText>
            <Button type="submit" variant="danger" disabled={busy || !sure}>
              Delete my account
            </Button>
          </form>
        )}
      </div>
    </div>
  )
}
