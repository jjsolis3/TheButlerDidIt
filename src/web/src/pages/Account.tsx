import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'

/**
 * The account pages reached from emails: forgot password, choose a new password,
 * and confirm your email. The links carry the one-time token in the query string.
 */

export function ForgotPassword() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      setSent((await api.forgotPassword(email)).message)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Heading className="mt-8 mb-2">Forgot your password?</Heading>
      <p className="mb-6 text-muted">Enter your email and we'll send you a link to choose a new one.</p>
      <Card>
        {sent ? (
          <p>{sent}</p>
        ) : (
          <form onSubmit={submit} className="space-y-4">
            <Field label="Email">
              <input className={inputClass} type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
            </Field>
            <ErrorText>{error}</ErrorText>
            <Button type="submit" disabled={busy} className="w-full">
              Send me a reset link
            </Button>
          </form>
        )}
      </Card>
      <p className="mt-4 text-center text-sm">
        <Link to="/login" className="text-accent underline">
          Back to sign in
        </Link>
      </p>
    </Shell>
  )
}

export function ResetPassword() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const email = params.get('email') ?? ''
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.resetPassword(email, token, password)
      navigate('/host/new') // the server signed us in
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  if (!email || !token)
    return (
      <Shell>
        <Heading className="mt-8 mb-4">This link is incomplete</Heading>
        <p className="text-muted">
          Open the whole link from your email, or <Link to="/forgot-password" className="text-accent underline">ask for a new one</Link>.
        </p>
      </Shell>
    )

  return (
    <Shell>
      <Heading className="mt-8 mb-2">Choose a new password</Heading>
      <p className="mb-6 text-muted">For {email}. Signing in on other devices will need the new password.</p>
      <Card>
        <form onSubmit={submit} className="space-y-4">
          <Field label="New password" hint="At least 8 characters, including a number.">
            <input
              className={inputClass}
              type="password"
              autoComplete="new-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              minLength={8}
            />
          </Field>
          <ErrorText>{error}</ErrorText>
          <Button type="submit" disabled={busy} className="w-full">
            Save and sign in
          </Button>
        </form>
      </Card>
    </Shell>
  )
}

export function ConfirmEmail() {
  const [params] = useSearchParams()
  const userId = params.get('user') ?? ''
  const token = params.get('token') ?? ''
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null)

  // Confirming changes something, so it's a POST sent when the page opens. (Some email
  // scanners pre-open links; they don't run JavaScript, so they can't confirm by accident.)
  useEffect(() => {
    if (!userId || !token) return
    let cancelled = false
    api.confirmEmail(userId, token).then(
      () => !cancelled && setResult({ ok: true, message: 'Thanks, your email is confirmed.' }),
      (e: Error) => !cancelled && setResult({ ok: false, message: e.message }),
    )
    return () => {
      cancelled = true
    }
  }, [userId, token])

  const message = !userId || !token ? { ok: false, message: 'This link is incomplete. Open the whole link from your email.' } : result

  return (
    <Shell>
      <Heading className="mt-8 mb-4">Confirm your email</Heading>
      <Card>
        {message ? <p className={message.ok ? '' : 'text-red-200'}>{message.message}</p> : <p className="text-muted">Confirming…</p>}
      </Card>
      <p className="mt-4 text-center text-sm">
        <Link to="/host/new" className="text-accent underline">
          Host a party
        </Link>
      </p>
    </Shell>
  )
}

/** Shown to a signed-in host whose email isn't confirmed yet, on servers that require it. */
export function ConfirmEmailBanner({ required }: { required: boolean }) {
  const [state, setState] = useState<'idle' | 'sent' | string>('idle')
  if (!required) return null
  return (
    <div className="mb-6 rounded-xl border border-accent/50 bg-accent/10 p-4 text-sm">
      <p>
        <span className="font-semibold">Please confirm your email.</span> We sent you a link when you signed up. You can create parties
        once it's confirmed.
      </p>
      {state === 'sent' ? (
        <p className="mt-2 text-accent">Sent! Check your inbox (and spam folder).</p>
      ) : (
        <button
          className="mt-2 text-accent underline"
          onClick={() => api.resendConfirmation().then(() => setState('sent'), (e: Error) => setState(e.message))}
        >
          Send it again
        </button>
      )}
      {state !== 'idle' && state !== 'sent' && <p className="mt-2 text-red-200">{state}</p>}
    </div>
  )
}
