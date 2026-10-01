import { useEffect, useState, type ReactNode } from 'react'
import { api } from '../lib/api'
import { Button, ErrorText } from './ui'

/**
 * The host's recap panel at the end of a game, for both games: the recap stays private until the host
 * shares it, and a shared link can be copied, opened or switched off (the old link then stops working).
 *
 * `load` fetches the host's preview of this game's recap; pass a function that doesn't change between
 * renders (like `api.recap`), since a new one would load it again. `extra` adds actions that need the
 * loaded recap, such as the escape room's share card.
 */
export function RecapShare<T extends { url: string | null }>({
  code,
  blurb,
  load,
  extra,
}: {
  code: string
  blurb: string
  load: (code: string) => Promise<T>
  extra?: (loaded: T, link: string | null) => ReactNode
}) {
  const [loaded, setLoaded] = useState<T | null>(null)
  const [url, setUrl] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    load(code).then(
      (r) => {
        if (cancelled) return
        setLoaded(r)
        setUrl(r.url)
      },
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [code, load])

  const full = url ? `${window.location.origin}${url}` : null
  const run = (action: () => Promise<void>) => {
    setError(null)
    setCopied(false)
    action().catch((e: Error) => setError(e.message))
  }

  return (
    <section className="mx-auto mt-10 max-w-2xl rounded-2xl border border-line bg-surface p-5 text-left">
      <h2 className="font-display text-2xl">The recap</h2>
      <p className="mt-1 text-sm text-muted">{blurb} It stays private unless you share it.</p>
      {loaded && !full && (
        <Button className="mt-4" onClick={() => run(async () => setUrl((await api.shareRecap(code)).url))}>
          Share the recap
        </Button>
      )}
      {full && (
        <div className="mt-4 space-y-3">
          <input readOnly value={full} aria-label="Recap link" onFocus={(e) => e.target.select()} className="w-full rounded-lg border border-line bg-bg px-3 py-2 font-mono text-sm" />
          <div className="flex flex-wrap gap-2">
            <Button
              onClick={() =>
                run(async () => {
                  await navigator.clipboard.writeText(full)
                  setCopied(true)
                })
              }
            >
              {copied ? 'Copied!' : 'Copy link'}
            </Button>
            <a href={full} target="_blank" rel="noreferrer" className="inline-flex min-h-11 items-center rounded-lg border border-line px-4 text-sm hover:border-accent">
              Open it
            </a>
            <Button
              variant="quiet"
              onClick={() =>
                run(async () => {
                  await api.unshareRecap(code)
                  setUrl(null)
                })
              }
            >
              Stop sharing
            </Button>
          </div>
        </div>
      )}
      {loaded && extra && <div className="mt-4 border-t border-line pt-4">{extra(loaded, full)}</div>}
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
