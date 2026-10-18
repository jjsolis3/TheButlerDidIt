import { useState } from 'react'
import { api } from '../lib/api'
import { Button, ErrorText } from './ui'

/**
 * The host's switch for costume selfies (#150). A Family party's guests may be children, and a child's photo needs a
 * parent's OK, so there selfies start off and turning them on asks the host to confirm they have it. Turning them off
 * deletes the selfies taken so far. `allowed` comes from the page, which also hears the switch move on another device.
 */
export function SelfiesPanel({ code, family, allowed, onChange }: { code: string; family: boolean; allowed: boolean; onChange: (allowed: boolean) => void }) {
  const [parentsOk, setParentsOk] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const change = async (allow: boolean) => {
    setBusy(true)
    setError(null)
    try {
      onChange((await api.allowSelfies(code, allow, parentsOk)).allow)
      setParentsOk(false)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div data-testid="selfies" className="rounded-xl border border-line bg-surface/80 p-4 text-left text-sm">
      <p className="font-semibold">📸 Costume selfies are {allowed ? 'on' : 'off'}</p>
      <p className="mt-1 text-muted">
        {allowed
          ? 'Guests can add a photo of their costume from their phone. It shows on this screen, and to anyone watching it, and is deleted after the party.'
          : family
            ? "Guests at a Family party may be children, so selfies start off. Turn them on only if every child's parent is happy for them to share a photo."
            : "Guests can't add a photo of their costume."}
      </p>
      {!allowed && family && (
        <label className="mt-3 flex min-h-11 items-center gap-2">
          <input type="checkbox" checked={parentsOk} onChange={(e) => setParentsOk(e.target.checked)} />
          The children's parents are happy for them to share a photo
        </label>
      )}
      <div className="mt-2 flex flex-wrap items-center gap-3">
        <Button variant="ghost" disabled={busy || (!allowed && family && !parentsOk)} onClick={() => void change(!allowed)}>
          {busy ? 'Saving…' : allowed ? 'Turn selfies off' : 'Turn selfies on'}
        </Button>
        {allowed && <span className="text-xs text-muted">Turning them off deletes the ones taken so far.</span>}
      </div>
      <ErrorText>{error}</ErrorText>
    </div>
  )
}
