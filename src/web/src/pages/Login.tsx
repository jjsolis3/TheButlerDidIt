import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'

export default function Login() {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (mode === 'login') await api.login(email, password)
      else await api.register(email, password, displayName)
      navigate('/host/new')
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
      <p className="mt-4 text-center text-sm text-muted">
        {mode === 'login' ? 'New here? ' : 'Already have an account? '}
        <button className="text-accent underline" onClick={() => setMode(mode === 'login' ? 'register' : 'login')}>
          {mode === 'login' ? 'Create an account' : 'Sign in'}
        </button>
      </p>
    </Shell>
  )
}
