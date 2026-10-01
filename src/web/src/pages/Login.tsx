import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import type { AuthOptions, InviteInfo } from '../lib/types'

/**
 * Where to go after signing in: the ?next= page (e.g. the host remote), but only a path on this
 * site. "//evil.example" or "https://…" would turn the login page into a redirect to anywhere.
 */
function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/host/new'
}

export default function Login() {
  const [params] = useSearchParams()
  // An invite link (/login?invite=…) opens straight on the sign-up form.
  const invite = params.get('invite')
  const [mode, setMode] = useState<'login' | 'register'>(invite ? 'register' : 'login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()
  const [options, setOptions] = useState<AuthOptions | null>(null)
  const [inviteInfo, setInviteInfo] = useState<InviteInfo | null>(null)
  const [inviteError, setInviteError] = useState<string | null>(null)

  useEffect(() => {
    api.authOptions().then(setOptions, () => setOptions(null))
  }, [])

  // Check the invite before the form is filled in, so a used or expired link says so straight away.
  // The server checks it again on sign-up; this is only for a helpful page.
  useEffect(() => {
    if (!invite) return
    let cancelled = false
    api.checkInvite(invite).then(
      (info) => {
        if (cancelled) return
        setInviteInfo(info)
        if (info.email) setEmail(info.email)
      },
      (e: Error) => {
        if (cancelled) return
        setInviteError(e.message)
        setMode('login') // "already used… sign in instead"
      },
    )
    return () => {
      cancelled = true
    }
  }, [invite])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (mode === 'login') await api.login(email, password)
      else await api.register(email, password, displayName, invite && !inviteError ? invite : undefined)
      navigate(safeNext(params.get('next')))
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Heading className="mt-8 mb-2">{mode === 'login' ? 'Welcome back, host' : 'Create a host account'}</Heading>
      <p className="mb-6 text-muted">Only the host needs an account. Guests join with a party code.</p>
      {mode === 'register' && inviteInfo && (
        <p className="mb-4 rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm text-ink">
          {inviteInfo.invitedBy} invited you to host on The Butler Did It.
        </p>
      )}
      {inviteError && (
        <div className="mb-4">
          <ErrorText>{inviteError}</ErrorText>
        </div>
      )}
      <Card>
        <form onSubmit={submit} className="space-y-4">
          {mode === 'register' && (
            <Field label="Your name">
              <input className={inputClass} value={displayName} onChange={(e) => setDisplayName(e.target.value)} required maxLength={60} />
            </Field>
          )}
          {/* An invite for one address fills it in and keeps it: the server would refuse any other. */}
          <Field label="Email" hint={mode === 'register' && inviteInfo?.email ? 'This invite is for this address.' : undefined}>
            <input
              className={`${inputClass} read-only:text-muted`}
              type="email"
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              readOnly={mode === 'register' && !!inviteInfo?.email}
              required
            />
          </Field>
          <Field label="Password" hint={mode === 'register' ? 'At least 8 characters.' : undefined}>
            <input
              className={inputClass}
              type="password"
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              minLength={8}
            />
          </Field>
          <ErrorText>{error}</ErrorText>
          <Button type="submit" disabled={busy} className="w-full">
            {mode === 'login' ? 'Sign in' : 'Create account'}
          </Button>
        </form>
      </Card>
      {mode === 'login' && options && (
        <p className="mt-4 text-center text-sm text-muted">
          {options.emailEnabled ? (
            <Link to="/forgot-password" className="text-accent underline">
              Forgot your password?
            </Link>
          ) : (
            'Forgot your password? Ask the admin for a reset link.'
          )}
        </p>
      )}
      {/* Sign-ups can be invite-only (ALLOW_REGISTRATION=false). The server enforces it; this only stops
          offering a form that would be refused. Shown while the options load, and to anyone with a good invite. */}
      {mode === 'login' && options?.allowRegistration === false && !inviteInfo ? (
        <p className="mt-4 text-center text-sm text-muted">New host accounts are by invitation. Ask the admin of this site for an invite link.</p>
      ) : (
        <p className="mt-4 text-center text-sm text-muted">
          {mode === 'login' ? 'New here? ' : 'Already have an account? '}
          <button className="text-accent underline" onClick={() => setMode(mode === 'login' ? 'register' : 'login')}>
            {mode === 'login' ? 'Create an account' : 'Sign in'}
          </button>
        </p>
      )}
    </Shell>
  )
}
