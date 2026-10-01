import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import type { SpectatorList, SpectatorView } from '../lib/types'
import { Button, ErrorText } from './ui'

/**
 * The host's view of spectator mode (#112): who's watching the TV on their own phone, a link to send to
 * family who can't be there, a way to remove anyone, and a switch to stop people watching at all.
 * `refresh` changes whenever the server says someone started or stopped watching.
 */
export function WatchersPanel({ code, refresh, open = false }: { code: string; refresh: number; open?: boolean }) {
  const [list, setList] = useState<SpectatorList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.spectators(code).then(
      (l) => !cancelled && setList(l),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [code, refresh])

  const link = `${window.location.origin}/watch/${code}`
  const run = (action: () => Promise<SpectatorList | void>) => {
    setError(null)
    action().then(
      (l) => l && setList(l),
      (e: Error) => setError(e.message),
    )
  }
  // The switch moves at once (it's what was tapped), and moves back if the server says no.
  const toggle = (allow: boolean) => {
    setError(null)
    setList((l) => l && { ...l, allow })
    api.allowSpectators(code, allow).then(setList, (e: Error) => {
      setError(e.message)
      setList((l) => l && { ...l, allow: !allow })
    })
  }
  const remove = (w: SpectatorView) =>
    run(async () => {
      await api.removeSpectator(code, w.id)
      setList((l) => l && { ...l, watching: l.watching.filter((x) => x.id !== w.id) })
    })

  const count = list?.watching.length ?? 0
  return (
    <details open={open} data-testid="watchers" className="rounded-xl border border-line bg-surface/80 p-4 text-left">
      <summary className="min-h-11 cursor-pointer content-center font-semibold">
        👀 {count} watching{list && !list.allow ? ' · watching is off' : ''}
      </summary>
      <p className="mt-1 text-sm text-muted">
        Family who can't be there, or more people than there are seats, can watch this screen on their own phone, even after the game starts, and send cheers. They
        see what the TV shows, including the players' photos, and nothing else.
      </p>
      <label className="mt-3 flex min-h-11 items-center gap-2 text-sm">
        <input type="checkbox" checked={list?.allow ?? true} disabled={!list} onChange={(e) => toggle(e.target.checked)} />
        Let people watch
      </label>
      {list?.allow && (
        <div className="mt-2 flex flex-wrap gap-2">
          <input readOnly value={link} aria-label="Watch link" onFocus={(e) => e.target.select()} className="min-w-0 flex-1 rounded-lg border border-line bg-bg px-3 py-2 font-mono text-sm" />
          <Button
            variant="ghost"
            onClick={() =>
              run(async () => {
                await navigator.clipboard.writeText(link)
                setCopied(true)
              })
            }
          >
            {copied ? 'Copied!' : 'Copy watch link'}
          </Button>
        </div>
      )}
      {count > 0 && (
        <ul className="mt-3 divide-y divide-line">
          {list!.watching.map((w) => (
            <li key={w.id} className="flex items-center justify-between gap-2 py-1 text-sm">
              <span className="truncate">{w.name}</span>
              <Button variant="quiet" onClick={() => remove(w)} aria-label={`Stop ${w.name} watching`}>
                Remove
              </Button>
            </li>
          ))}
        </ul>
      )}
      <ErrorText>{error}</ErrorText>
    </details>
  )
}
