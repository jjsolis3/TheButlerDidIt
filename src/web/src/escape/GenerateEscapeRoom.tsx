import { useEffect, useRef, useState } from 'react'
import { Button, Card, ErrorText, Field, inputClass } from '../components/ui'
import { api } from '../lib/api'
import { useJobUpdates } from '../lib/hub'
import type { ContentRating, GenerationJob } from '../lib/types'

/** "Write a new escape room with AI": starts a background job from a theme and follows its progress. */
export function GenerateEscapeRoom({ onReady }: { onReady: (job: GenerationJob) => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const [theme, setTheme] = useState('')
  const [rating, setRating] = useState<ContentRating>('family')
  const [minutes, setMinutes] = useState(45)
  const [job, setJob] = useState<GenerationJob | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Kept in a ref so a new onReady from the parent doesn't restart the polling below.
  const onReadyRef = useRef(onReady)
  useEffect(() => {
    onReadyRef.current = onReady
  }, [onReady])

  // Follow the job until it finishes: re-fetch whenever the server signals a change, and every
  // 15 seconds in case a signal is lost. The work happens on the server, so leaving this page doesn't stop it.
  const jobId = job && (job.status === 'queued' || job.status === 'running') ? job.id : null
  const updates = useJobUpdates(jobId !== null)
  useEffect(() => {
    if (!jobId) return
    let active = true
    const refresh = async () => {
      try {
        const next = await api.generationJob(jobId)
        if (!active) return
        setJob(next)
        if (next.status === 'succeeded') await onReadyRef.current(next)
      } catch (e) {
        if (active) setError((e as Error).message)
      }
    }
    if (updates > 0) void refresh()
    const fallback = setInterval(refresh, 15_000)
    return () => {
      active = false
      clearInterval(fallback)
    }
  }, [jobId, updates])

  const start = async () => {
    setError(null)
    try {
      setJob(await api.generateEscapeRoom(theme, rating, minutes))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const running = job && (job.status === 'queued' || job.status === 'running')

  if (!open) {
    return (
      <button onClick={() => setOpen(true)} className="w-full rounded-xl border border-dashed border-accent/60 p-4 text-left hover:bg-accent/5">
        <p className="font-display text-lg text-accent">✨ Write a new escape room with AI</p>
        <p className="text-sm text-muted">Any theme you like: riddles, locks and a villain written for it, and checked to be escapable. Takes a minute or two.</p>
      </button>
    )
  }

  return (
    <Card className="border-accent/60">
      <p className="font-display mb-4 text-lg text-accent">✨ Write a new escape room with AI</p>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="sm:col-span-2">
          <Field label="Theme" hint="e.g. “a haunted lighthouse”, “a spaceship whose computer has gone rogue”, “a wizard's library”">
            <input className={inputClass} value={theme} onChange={(e) => setTheme(e.target.value)} maxLength={120} disabled={!!running} />
          </Field>
        </div>
        <Field label="Content">
          <select className={inputClass} value={rating} onChange={(e) => setRating(e.target.value as ContentRating)} disabled={!!running}>
            <option value="family">Family friendly</option>
            <option value="mature">Adults</option>
          </select>
        </Field>
        <Field label="Clock">
          <select className={inputClass} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))} disabled={!!running}>
            <option value={30}>30 minutes</option>
            <option value={45}>45 minutes</option>
            <option value={60}>60 minutes</option>
          </select>
        </Field>
      </div>
      <div className="mt-4 space-y-2">
        {!running && job?.status !== 'succeeded' && (
          <Button onClick={start} disabled={theme.trim() === ''}>
            Write my escape room
          </Button>
        )}
        {running && (
          <p className="candle text-sm text-accent" role="status">
            {job.progress}
          </p>
        )}
        {job?.status === 'succeeded' && (
          <div className="rounded-lg border border-accent/50 bg-accent/10 p-3 text-sm">
            <p>{job.progress} It's first on the shelf and selected. Carry on setting up.</p>
            {job.warnings.map((w) => (
              <p key={w} className="mt-1 text-muted">
                ⚠ {w}
              </p>
            ))}
          </div>
        )}
        {job?.status === 'failed' && <ErrorText>{job.error}</ErrorText>}
        <ErrorText>{error}</ErrorText>
      </div>
    </Card>
  )
}
