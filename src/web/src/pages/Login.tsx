import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import type { AuthOptions } from '../lib/types'

/**
 * Where to go after signing in: the ?next= page (e.g. the host remote), but only a path on this
 * site. "//evil.example" or "https://…" would turn the login page into a redirect to anywhere.
 */
function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/host/new'
}

export default function Login() {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const [options, setOptions] = useState<AuthOptions | null>(null)

  useEffect(() => {
    api.authOptions().then(setOptions, () => setOptions(null))
  }, [])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (mode === 'login') await api.login(email, password)
      else await api.register(email, password, displayName)
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
      <Card>
        <form onSubmit={submit} className="space-y-4">
          {mode === 'register' && (
            <Field label="Your name">
              <input className={inputClass} value={displayName} onChange={(e) => setDisplayName(e.target.value)} required maxLength={60} />
            </Field>
          )}
          <Field label="Email">
            <input className={inputClass} type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
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
      <p className="mt-4 text-center text-sm text-muted">
        {mode === 'login' ? 'New here? ' : 'Already have an account? '}
        <button className="text-accent underline" onClick={() => setMode(mode === 'login' ? 'register' : 'login')}>
          {mode === 'login' ? 'Create an account' : 'Sign in'}
        </button>
      </p>
    </Shell>
  )
}
