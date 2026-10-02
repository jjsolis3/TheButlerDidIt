import { useId, useState, type ReactNode } from 'react'
import type { MediaLimits, RoomMediaKind } from '../lib/types'
import { buttonClass } from './buttonClass'
import { ErrorText } from './ui'

// One place for a host's own picture, video or sound, shared by the escape room and mystery editors: a preview,
// Upload/Replace with progress, Remove, and the checks that save a long upload that would only be refused.

const ACCEPT: Record<RoomMediaKind, string> = {
  image: 'image/jpeg,image/png,image/webp',
  video: 'video/mp4,video/quicktime,video/webm',
  audio: 'audio/mpeg,audio/mp4,audio/x-m4a,audio/ogg,audio/wav,audio/webm',
}

const megabytes = (bytes: number) =>
  `${Math.max(0.1, bytes / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: bytes < 10 * 1024 * 1024 ? 1 : 0 })} MB`

/** The size limit for one kind of file. */
const limitFor = (kind: RoomMediaKind, limits: MediaLimits) =>
  kind === 'image' ? limits.imageBytes : kind === 'video' ? limits.videoBytes : limits.audioBytes

/**
 * Whether this browser can play the file, by trying to load it the way the TV will. A video from an iPhone is often
 * HEVC, which most browsers can't play: better to say so now than to find out at the party.
 */
function canPlay(file: File, kind: 'video' | 'audio'): Promise<boolean> {
  return new Promise((resolve) => {
    const element = document.createElement(kind)
    const url = URL.createObjectURL(file)
    const done = (ok: boolean) => {
      clearTimeout(timer)
      element.removeAttribute('src')
      URL.revokeObjectURL(url)
      resolve(ok)
    }
    // If the browser takes too long to decide, let the upload go ahead: the server checks the file too.
    const timer = setTimeout(() => done(true), 8000)
    element.preload = 'metadata'
    element.onloadedmetadata = () => done(kind === 'audio' || (element as HTMLVideoElement).videoWidth > 0)
    element.onerror = () => done(false)
    element.src = url
  })
}

/** The size limits, the allowance used so far, and what plays where: the top of a media tab. */
export function MediaAdvice({ limits }: { limits: MediaLimits }) {
  return (
    <>
      <p>
        Videos: MP4 plays everywhere (up to {megabytes(limits.videoBytes)}). On an iPhone, set Settings → Camera → Formats → Most Compatible first. Sounds and music:
        MP3 plays everywhere (up to {megabytes(limits.audioBytes)}); M4A and OGG work in most browsers.
      </p>
      {limits.allowanceBytes !== null && (
        <p>
          You've used {megabytes(limits.usedBytes)} of your {megabytes(limits.allowanceBytes)} for uploads.
        </p>
      )}
    </>
  )
}

export function MediaSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3" aria-label={title}>
      <h2 className="text-xs font-semibold tracking-wider text-accent uppercase">{title}</h2>
      <div className="grid gap-3 md:grid-cols-3">{children}</div>
    </section>
  )
}

export interface MediaSlotCardProps {
  kind: RoomMediaKind
  /** What's there now, or null. */
  url: string | null
  /** Someone uploaded it (rather than the AI painting it). */
  uploaded: boolean
  /** The card's heading, e.g. "Portrait". */
  label: string
  /** The card's full name, for screen readers and the upload button, e.g. "Portrait of Lady Ashworth". */
  title: string
  hint: string
  /** What the card says when nothing's there. */
  empty: string
  limits: MediaLimits
  canEdit: boolean
  onUpload: (file: File, onProgress: (fraction: number) => void) => Promise<void>
  onRemove: () => Promise<void>
  testKey?: string
}

export function MediaSlotCard({ kind, url, uploaded, label, title, hint, empty, limits, canEdit, onUpload, onRemove, testKey }: MediaSlotCardProps) {
  const inputId = useId()
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const limit = limitFor(kind, limits)

  const choose = async (file: File | undefined) => {
    if (!file) return
    setError(null)
    // Checked here first, to save a long upload that would only be refused. The server checks again.
    if (file.size > limit) return setError(`That file is ${megabytes(file.size)}; the most is ${megabytes(limit)}.`)
    if (kind !== 'image' && !(await canPlay(file, kind)))
      return setError(
        kind === 'video'
          ? "This browser can't play that video, so the TV probably can't either. Save it as an MP4 (H.264): on an iPhone, Settings → Camera → Formats → Most Compatible."
          : "This browser can't play that sound. Try an MP3.",
      )
    setProgress(0)
    try {
      await onUpload(file, setProgress)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setProgress(null)
    }
  }

  const remove = async () => {
    setError(null)
    try {
      await onRemove()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-line bg-surface p-3" aria-label={title} role="group" data-key={testKey}>
      <div className="flex items-baseline justify-between gap-2">
        <h3 className="font-semibold">{label}</h3>
        {url && <span className="shrink-0 rounded-full border border-line px-2 py-0.5 text-xs text-muted">{uploaded ? 'Your upload' : 'AI picture'}</span>}
      </div>
      <p className="text-xs text-muted">{hint}</p>
      {url ? (
        <Preview kind={kind} url={url} label={title} />
      ) : (
        <p className="grid min-h-20 place-items-center rounded-lg border border-dashed border-line p-3 text-center text-xs text-muted">{empty}</p>
      )}
      {progress !== null && (
        <div className="h-2 overflow-hidden rounded-full bg-line" role="progressbar" aria-label={`Uploading ${title}`} aria-valuenow={Math.round(progress * 100)}>
          <div className="h-full bg-accent transition-[width]" style={{ width: `${Math.round(progress * 100)}%` }} />
        </div>
      )}
      <ErrorText>{error}</ErrorText>
      {canEdit && (
        <div className="mt-auto flex flex-wrap gap-2">
          {/* A label styled as a button opens the file picker; the real input is hidden but still reachable by screen readers. */}
          <label htmlFor={inputId} className={buttonClass('ghost', `cursor-pointer ${progress !== null ? 'pointer-events-none opacity-40' : ''}`)}>
            {progress !== null ? `Uploading… ${Math.round(progress * 100)}%` : uploaded ? 'Replace' : 'Upload'}
          </label>
          <input
            id={inputId}
            type="file"
            accept={ACCEPT[kind]}
            className="sr-only"
            aria-label={`Upload ${title}`}
            disabled={progress !== null}
            onChange={(e) => {
              void choose(e.target.files?.[0])
              e.target.value = '' // so choosing the same file again still counts as a change
            }}
          />
          {uploaded && (
            <button type="button" className={buttonClass('quiet')} onClick={remove} aria-label={`Remove ${title}`}>
              Remove
            </button>
          )}
        </div>
      )}
    </div>
  )
}

function Preview({ kind, url, label }: { kind: RoomMediaKind; url: string; label: string }) {
  if (kind === 'image') return <img src={url} alt={label} className="aspect-video w-full rounded-lg object-cover" />
  // preload="metadata": just enough to show the length and first frame, not the whole file.
  if (kind === 'video') return <video src={url} controls preload="metadata" className="aspect-video w-full rounded-lg bg-black" aria-label={label} />
  return <audio src={url} controls preload="metadata" className="w-full" aria-label={label} />
}
