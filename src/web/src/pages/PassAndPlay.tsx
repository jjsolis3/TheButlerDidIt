import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { PlayerScreen } from '../components/PlayerScreen'
import { Button, ErrorText, Shell } from '../components/ui'
import { api } from '../lib/api'
import { seats, type StoredSeat } from '../lib/seats'
import { useThemePalette } from '../lib/theme'

/**
 * One device, many players. The host's device holds a seat token for every
 * pass-and-play guest. To keep secrets secret, a dossier only opens after the
 * right person presses and holds, and "Hide" wipes it from the screen before
 * the device is handed on.
 */
export default function PassAndPlay() {
  const code = (useParams().code ?? '').toUpperCase()
  const [local, setLocal] = useState<StoredSeat[]>(() => seats.local(code))
  const [handingTo, setHandingTo] = useState<StoredSeat | null>(null)
  const [open, setOpen] = useState<StoredSeat | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [themeSlug, setThemeSlug] = useState<string>()
  useThemePalette(themeSlug)

  useEffect(() => {
    api.party(code).then((p) => setThemeSlug(p.themeSlug), () => {})
  }, [code])

  const add = async () => {
    const name = prompt('Guest name:')?.trim()
    if (!name) return
    setError(null)
    try {
      const seat = await api.addSeat(code, name, true)
      seats.addLocal(code, { seatId: seat.seatId, token: seat.token, name })
      setLocal(seats.local(code))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  if (open) {
    return (
      <div>
        <div className="sticky top-0 z-40 flex items-center justify-between gap-2 border-b border-blood/50 bg-blood/30 px-4 py-2 backdrop-blur">
          <span className="text-sm">Private dossier: {open.name}</span>
          <Button variant="primary" className="min-h-9 py-1" onClick={() => setOpen(null)}>
            Hide & pass on
          </Button>
        </div>
        <PlayerScreen
          code={code}
          token={open.token}
          onLeave={() => {
            seats.forget(code, open.seatId)
            setLocal(seats.local(code))
            setOpen(null)
          }}
        />
      </div>
    )
  }

  if (handingTo) {
    return (
      <HoldToReveal
        name={handingTo.name}
        onReveal={() => {
          setOpen(handingTo)
          setHandingTo(null)
        }}
        onCancel={() => setHandingTo(null)}
      />
    )
  }

  return (
    <Shell>
      <h1 className="font-display mt-6 text-3xl">Pass & play</h1>
      <p className="mt-2 text-muted">Tap a name, then hand the device to that guest. Only they should look.</p>
      <div className="mt-6 grid gap-3">
        {local.map((s) => (
          <button key={s.seatId} onClick={() => setHandingTo(s)} className="rounded-xl border border-line bg-surface p-4 text-left text-lg hover:border-accent">
            {s.name}
          </button>
        ))}
        {local.length === 0 && <p className="text-muted">No pass-and-play guests yet.</p>}
      </div>
      <div className="mt-6 flex flex-wrap gap-3">
        <Button variant="ghost" onClick={add}>
          Add a guest on this device
        </Button>
        <Link to={`/stage/${code}`} className="inline-flex min-h-11 items-center rounded-lg px-4 text-sm text-muted underline">
          Back to the stage
        </Link>
      </div>
      <ErrorText>{error}</ErrorText>
    </Shell>
  )
}

function HoldToReveal({ name, onReveal, onCancel }: { name: string; onReveal: () => void; onCancel: () => void }) {
  const [progress, setProgress] = useState(0)
  const timer = useRef<ReturnType<typeof setInterval>>(undefined)

  const start = () => {
    clearInterval(timer.current)
    const began = Date.now()
    timer.current = setInterval(() => {
      const p = Math.min(1, (Date.now() - began) / 900)
      setProgress(p)
      if (p >= 1) {
        clearInterval(timer.current)
        onReveal()
      }
    }, 30)
  }
  const stop = () => {
    clearInterval(timer.current)
    setProgress(0)
  }
  useEffect(() => () => clearInterval(timer.current), [])

  return (
    <div className="grain grid min-h-dvh place-items-center p-6 text-center">
      <div className="space-y-8">
        <p className="text-muted">Hand the device to</p>
        <p className="font-display text-5xl">{name}</p>
        <button
          onPointerDown={start}
          onPointerUp={stop}
          onPointerLeave={stop}
          onContextMenu={(e) => e.preventDefault()}
          className="relative mx-auto grid h-40 w-40 touch-none place-items-center rounded-full border-2 border-accent text-sm font-semibold text-accent select-none"
          style={{ background: `conic-gradient(var(--theme-accent) ${progress * 360}deg, transparent 0)` }}
          aria-label={`Press and hold to open ${name}'s dossier`}
        >
          <span className="grid h-32 w-32 place-items-center rounded-full bg-bg">Press & hold</span>
        </button>
        <button onClick={onCancel} className="text-sm text-muted underline">
          Not {name}? Go back
        </button>
      </div>
    </div>
  )
}
