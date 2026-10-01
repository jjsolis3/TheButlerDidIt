import { useState } from 'react'
import type { EscapeFeedEntry, EscapeSceneView, EscapeSpotView } from '../lib/types'
import { backdrop } from './moods'
import { PropIcon } from './props'

type Examine = (spotId: string) => Promise<void>

/**
 * The part of the room in front of the group, with its spots to search.
 *
 * The spots are ordinary HTML buttons placed over the picture (as a percentage of the scene's
 * canvas, so it scales to any screen), which keeps them reachable by keyboard and screen reader.
 * On a phone (`onExamine` given) tapping one searches it; on the TV they're display only.
 * What a spot holds arrives from the server only once someone has searched it.
 */
export function SceneView({
  scene,
  artUrl,
  feed,
  onExamine,
}: {
  scene: EscapeSceneView
  artUrl: string | null
  feed: EscapeFeedEntry[]
  onExamine?: Examine
}) {
  const [zoom, setZoom] = useState(1)
  const [last, setLast] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const interactive = !!onExamine

  const search = async (spot: EscapeSpotView) => {
    if (!onExamine || busy) return
    setError(null)
    setLast(spot.id)
    if (spot.examined) return // already searched: just show what was there again
    setBusy(true)
    try {
      await onExamine(spot.id)
    } catch (e) {
      // The room's own words for a spot that needs a tool ("Maybe in a different light?"), or "already searched".
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  // Feedback for the last tap: what was there, or the penalty for a decoy on Hard (from the ticker).
  const lastSpot = scene.objects.find((o) => o.id === last)
  const penalty = lastSpot && feed.findLast((f) => f.text.includes(`the ${lastSpot.label}. Nothing there`))
  const found = scene.objects.filter((o) => o.examined && o.look)

  return (
    <section aria-label="The room" className="space-y-2">
      {interactive && (
        <div className="flex items-center justify-between gap-2">
          <p className="text-xs text-muted">Tap anything that looks worth a closer look.</p>
          <div className="flex gap-1" role="group" aria-label="Zoom">
            {[1, 2, 3].map((z) => (
              <button
                key={z}
                onClick={() => setZoom(z)}
                aria-pressed={zoom === z}
                className={`min-h-9 min-w-9 rounded-lg border px-2 text-xs ${zoom === z ? 'border-accent text-accent' : 'border-line text-muted'}`}
              >
                {z}×
              </button>
            ))}
          </div>
        </div>
      )}
      <div className="overflow-auto rounded-xl border border-line" data-testid="scene">
        <div
          className="relative"
          style={{
            width: `${zoom * 100}%`,
            aspectRatio: `${scene.width} / ${scene.height}`,
            background: artUrl ? `center / cover no-repeat url(${JSON.stringify(artUrl)})` : backdrop(scene.backdrop),
          }}
        >
          {scene.objects.map((o) => (
            <Spot key={o.id} spot={o} scene={scene} onSearch={interactive ? () => void search(o) : undefined} disabled={busy} />
          ))}
        </div>
      </div>
      {interactive && (
        <div role="status" aria-live="polite" className="min-h-6 text-sm">
          {error ? (
            <p className="text-muted">🔒 {error}</p>
          ) : lastSpot?.examined ? (
            <p>
              🔎 <span className="font-semibold">{capitalise(lastSpot.label)}:</span> {lastSpot.look}
              {penalty && <span className="text-red-300"> (−10 s)</span>}
            </p>
          ) : null}
        </div>
      )}
      {!interactive && found.length > 0 && (
        <ul className="grid gap-1 text-sm sm:grid-cols-2" aria-label="Searched so far">
          {found.map((o) => (
            <li key={o.id} className="text-ink/90">
              🔎 <span className="font-semibold">{capitalise(o.label)}:</span> <span className="text-muted">{o.look}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function Spot({ spot: o, scene, onSearch, disabled }: { spot: EscapeSpotView; scene: EscapeSceneView; onSearch?: () => void; disabled: boolean }) {
  const style = {
    left: `${(o.x / scene.width) * 100}%`,
    top: `${(o.y / scene.height) * 100}%`,
    width: `${(o.w / scene.width) * 100}%`,
    height: `${(o.h / scene.height) * 100}%`,
  }
  const inner = (
    <>
      <PropIcon prop={o.prop} examined={o.examined} />
      <span className="absolute inset-x-0 bottom-0 truncate rounded-b-lg bg-black/55 px-1 text-center text-[10px] leading-4 text-ink sm:text-xs">
        {o.examined ? '✓ ' : ''}
        {o.label}
      </span>
    </>
  )
  const label = o.examined ? `The ${o.label}, searched: ${o.look ?? ''}` : `Search the ${o.label}`
  const cls = 'scene-spot absolute rounded-lg'
  return onSearch ? (
    <button
      style={style}
      className={`${cls} focus-visible:outline-2 focus-visible:outline-accent ${o.examined ? 'opacity-70' : 'hover:brightness-125'}`}
      onClick={onSearch}
      disabled={disabled}
      aria-label={label}
      data-testid={`spot-${o.id}`}
    >
      {inner}
    </button>
  ) : (
    <div style={style} className={`${cls} ${o.examined ? 'opacity-70' : ''}`} role="img" aria-label={label} data-testid={`spot-${o.id}`}>
      {inner}
    </div>
  )
}

const capitalise = (s: string) => (s ? s[0].toUpperCase() + s.slice(1) : s)
