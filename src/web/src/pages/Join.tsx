import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate, useParams } from 'react-router'
import { Button, Card, ErrorText, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import { seats } from '../lib/seats'
import type { PartyInfo } from '../lib/types'

export default function Join() {
  const params = useParams()
  const navigate = useNavigate()
  const [code, setCode] = useState(params.code?.toUpperCase() ?? '')
  const [name, setName] = useState('')
  const [party, setParty] = useState<PartyInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Already seated at this party on this device? Go straight back in.
  useEffect(() => {
    if (params.code && seats.mine(params.code)) navigate(`/play/${params.code.toUpperCase()}`, { replace: true })
  }, [params.code, navigate])

  useEffect(() => {
    const clean = code.replace(/[^A-Za-z0-9]/g, '')
    if (clean.length !== 6) {
      setParty(null)
      return
    }
    api.party(clean).then(
      (p) => {
        setParty(p)
        setError(null)
      },
      () => {
        setParty(null)
        setError('No party found with that code.')
      },
    )
  }, [code])

  const join = async (e: FormEvent) => {
    e.preventDefault()
    if (!party) return
    setBusy(true)
    setError(null)
    try {
      const seat = await api.join(party.code, name)
      seats.setMine(party.code, { seatId: seat.seatId, token: seat.token, name })
      navigate(`/play/${party.code}`)
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Heading className="mt-8 mb-6">Join the party</Heading>
      <Card>
        <form onSubmit={join} className="space-y-4">
          <Field label="Party code" hint="It's on the host's screen.">
            <input
              className={`${inputClass} font-mono text-2xl tracking-[0.4em] uppercase`}
              value={code}
              onChange={(e) => setCode(e.target.value.toUpperCase())}
              maxLength={8}
              autoCapitalize="characters"
              autoComplete="off"
              inputMode="text"
              required
            />
          </Field>
          {party && (
            <div className="rounded-lg border border-accent/40 bg-accent/5 p-3">
              <p className="font-display text-lg">{party.title}</p>
              <p className="text-sm text-muted">
                {party.playerCount} of {party.maxPlayers} guests have arrived.
              </p>
            </div>
          )}
          <Field label="Your name" hint="Your real name. You'll get a character next.">
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} maxLength={30} required autoComplete="given-name" />
          </Field>
          <ErrorText>{error}</ErrorText>
          <Button type="submit" disabled={!party || !name.trim() || busy} className="w-full text-base">
            Take my seat
          </Button>
        </form>
      </Card>
    </Shell>
  )
}
