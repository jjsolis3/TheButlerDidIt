import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { buttonClass } from '../components/buttonClass'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { UnsupportedGame } from '../components/UnsupportedGame'
import { EscapeStage } from '../escape/EscapeStage'
import { api } from '../lib/api'
import { watching, type StoredWatcher } from '../lib/seats'
import type { PartyInfo } from '../lib/types'
import { StageScreen } from './Stage'

/**
 * Spectator mode (/watch/:code, #112): the party's TV on your own phone, with cheers to send. For family
 * who can't be there, or more people than a game has seats. The link works on its own: someone who
 * hasn't watched yet gives a name first. The watch token is kept on this device like a seat token, so a
 * phone that sleeps or refreshes keeps watching.
 */
export default function Watch() {
  const code = (useParams().code ?? '').toUpperCase()
  const navigate = useNavigate()
  const [info, setInfo] = useState<PartyInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [me, setMe] = useState<StoredWatcher | undefined>(() => watching.get(code))
  const [removed, setRemoved] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.party(code).then(
      (p) => !cancelled && setInfo(p),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [code])

  if (error)
    return (
      <Shell>
        <Heading className="mt-8">No party here</Heading>
        <p className="mt-3 text-muted">There's no party with the code {code}. Check it with the host.</p>
      </Shell>
    )
  if (removed)
    return (
      <Shell>
        <Heading className="mt-8">You're no longer watching</Heading>
        <p className="mt-3 text-muted">The host has stopped people watching this party, or it has finished.</p>
        <Link to="/" className={buttonClass('ghost', 'mt-6')}>
          Back home
        </Link>
      </Shell>
    )
  if (!info) return <p className="p-10 text-center text-muted">Finding the party…</p>
  if (!me) return <WatchForm info={info} onWatching={setMe} />

  const watcher = {
    name: me.name,
    onLeave: () => {
      watching.forget(code)
      navigate('/')
    },
    onRemoved: () => {
      watching.forget(code)
      setRemoved(true)
    },
  }
  if (info.kind === 'escapeRoom') return <EscapeStage info={info} token={me.token} watcher={watcher} />
  if (info.kind !== 'mystery') return <UnsupportedGame kind={info.kind} />
  return <StageScreen info={info} token={me.token} watcher={watcher} />
}

function WatchForm({ info, onWatching }: { info: PartyInfo; onWatching: (w: StoredWatcher) => void }) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const closed = info.status === 'finished' ? 'This party has finished.' : !info.allowSpectators ? "The host isn't letting anyone watch this party." : null

  const watch = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const w = await api.watch(info.code, name.trim())
      const stored = { watcherId: w.watcherId, token: w.token, name: name.trim() }
      watching.set(info.code, stored)
      onWatching(stored)
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Heading className="mt-8 mb-2">Watch the party</Heading>
      <p className="mb-6 text-muted">
        See what's on the TV for <span className="text-ink">{info.title}</span> on your own phone, and send cheers. You won't get a seat or any clues.
      </p>
      <Card>
        {closed ? (
          <p className="text-muted">{closed}</p>
        ) : (
          <form onSubmit={watch} className="space-y-4">
            <Field label="Your name" hint="The TV shows it with your cheers.">
              <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} maxLength={30} required autoComplete="given-name" />
            </Field>
            <ErrorText>{error}</ErrorText>
            <Button type="submit" disabled={!name.trim() || busy} className="w-full text-base">
              👀 Start watching
            </Button>
          </form>
        )}
      </Card>
    </Shell>
  )
}
