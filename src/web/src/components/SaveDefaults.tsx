import { useState } from 'react'
import { Link } from 'react-router'
import { api } from '../lib/api'
import type { HostPreferences } from '../lib/types'

/**
 * "Save as my usual settings" under the Create button (#102): stores this game's choices, so the host page starts from
 * them next time. `change` sets this game's part of the settings; the other game's part is kept as it was saved.
 */
export function SaveDefaults({ change }: { change: (saved: HostPreferences) => HostPreferences }) {
  const [state, setState] = useState<'idle' | 'saving' | 'saved'>('idle')
  const [error, setError] = useState<string | null>(null)

  const save = async () => {
    setState('saving')
    setError(null)
    try {
      // Read first: the other game's settings may have changed on the settings page (or another device) since.
      await api.account.savePreferences(change(await api.account.preferences()))
      setState('saved')
    } catch (e) {
      setError((e as Error).message)
      setState('idle')
    }
  }

  return (
    <p className="text-center text-sm text-muted" role="status">
      {state === 'saved' ? (
        <>
          ✓ Saved. New parties start like this. <Link to="/settings" className="underline hover:text-ink">Party settings</Link>
        </>
      ) : (
        <button type="button" onClick={save} disabled={state === 'saving'} className="underline hover:text-ink disabled:opacity-60">
          {state === 'saving' ? 'Saving…' : 'Save these as my usual settings'}
        </button>
      )}
      {error && <span className="ml-2 text-red-300">{error}</span>}
    </p>
  )
}
