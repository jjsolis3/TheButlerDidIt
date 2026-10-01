import { useState } from 'react'
import { Button } from '../components/ui'
import type { EscapeRecapPage } from '../lib/types'
import { shareCard } from './shareCard'

/** "📸 Share card": makes the picture and opens the share sheet, or downloads it on a computer. */
export function ShareCardButton({ page, link }: { page: EscapeRecapPage; link: string | null }) {
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  const share = async () => {
    setBusy(true)
    setNote(null)
    try {
      const outcome = await shareCard(page, link)
      setNote(outcome === 'downloaded' ? 'The picture is in your downloads.' : outcome === 'shared' ? 'Shared!' : null)
    } catch {
      setNote("The picture couldn't be made on this device.")
    } finally {
      setBusy(false)
    }
  }

  return (
    <div>
      <Button onClick={share} disabled={busy}>
        📸 {busy ? 'Making the picture…' : 'Share card'}
      </Button>
      <p className="mt-2 min-h-5 text-sm text-muted" aria-live="polite">
        {note ?? 'A picture of your result for the group chat. It shows the room, your time and the team, never the answers.'}
      </p>
    </div>
  )
}
