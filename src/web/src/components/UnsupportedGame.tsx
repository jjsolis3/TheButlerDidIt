import { Link } from 'react-router'
import type { GameKind } from '../lib/types'

const names: Record<GameKind, string> = { mystery: 'murder mystery', escapeRoom: 'escape room' }

/** Shown on the game screens for a kind of party this version of the app can't play yet. */
export function UnsupportedGame({ kind }: { kind: GameKind }) {
  return (
    <div className="grid min-h-dvh place-items-center p-6 text-center text-muted">
      <div>
        <p className="font-display text-2xl text-ink">This is {names[kind] ? `an ${names[kind]}` : 'a new kind of game'}.</p>
        <p className="mt-2">That game type isn't available yet. Check back soon!</p>
        <Link className="mt-4 inline-block text-accent underline" to="/">
          Back to the start
        </Link>
      </div>
    </div>
  )
}
