import { useLayoutEffect, useRef, useState } from 'react'

/**
 * Fits what's inside to the height it has, for the TV layout: nobody scrolls a TV across a room (#129).
 *
 * Content a little taller than the screen is scaled down to fit (never up). Below `min` it would be too small to
 * read from the sofa, so it stops shrinking there and the box scrolls instead, as a last resort. The phases keep
 * their own lists short on the TV (the newest clues, the latest secrets), so that should be rare.
 *
 * It measures the content's natural height (a CSS transform doesn't change it) whenever either box resizes: a new
 * clue, a longer paragraph, a smaller window. `data-fit` says which it did, for the tests.
 */
export function FitToScreen({ children, min = 0.6, className = '' }: { children: React.ReactNode; min?: number; className?: string }) {
  const outer = useRef<HTMLDivElement>(null)
  const inner = useRef<HTMLDivElement>(null)
  const [fit, setFit] = useState({ scale: 1, height: 0 })

  // A layout effect, so the first frame is already the right size rather than flashing the full one.
  useLayoutEffect(() => {
    const box = outer.current
    const content = inner.current
    if (!box || !content) return
    const measure = () => {
      const needed = content.offsetHeight
      // Both heights are rounded to whole pixels but the layout isn't, so keep a pixel spare: otherwise the scaled
      // content can come out a fraction taller than its box, which clips the bottom of the last card.
      const room = box.clientHeight - 1
      const scale = needed > room ? Math.max(min, room / needed) : 1
      setFit((f) => (f.scale === scale && f.height === needed ? f : { scale, height: needed }))
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(box)
    observer.observe(content)
    return () => observer.disconnect()
  }, [min])

  const state = fit.scale === 1 ? 'fits' : fit.scale > min ? 'scaled' : 'scrolls'
  return (
    <div ref={outer} data-fit={state} className={`h-full min-h-0 overflow-x-hidden ${state === 'scrolls' ? 'overflow-y-auto' : 'overflow-y-hidden'} ${className}`}>
      {/* The scaled content's footprint: a transform shrinks what you see but not the space it takes. */}
      <div style={{ height: fit.height ? fit.height * fit.scale : undefined }}>
        <div ref={inner} style={fit.scale === 1 ? undefined : { transform: `scale(${fit.scale})`, transformOrigin: 'top center' }}>
          {children}
        </div>
      </div>
    </div>
  )
}
