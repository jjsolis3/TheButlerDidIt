import { Link } from 'react-router'
import { allows, daysLeft, describeAccess, gameName } from '../lib/access'
import type { AccessView, GameKind } from '../lib/types'

/**
 * On the host page: says when the chosen game isn't in the host's plan (before they fill the form
 * in), or how long their free trial has left. The server makes the same check when they create.
 */
export function AccessNotice({ access, game }: { access: AccessView | undefined; game: GameKind }) {
  if (!access) return null
  if (!allows(access, game))
    return (
      <div role="status" className="mb-6 rounded-xl border border-blood/50 bg-blood/10 p-4 text-sm">
        <p className="font-semibold">🔒 {gameName(game)} aren't in your plan.</p>
        <p className="mt-1 text-muted">{describeAccess(access).detail}</p>
        {/* With payments on (#101) the way in is a plan, on the account page. */}
        <Link to={access.payments ? '/account#plans' : '/account'} className="mt-2 inline-block text-accent underline">
          {access.payments ? 'Choose a plan' : 'Your plan'}
        </Link>
      </div>
    )
  if (access.plan === 'trial' && access.endsAt) {
    const days = daysLeft(access.endsAt)
    return (
      <p className="mb-6 text-sm text-muted">
        🎟️ Free trial: {days} {days === 1 ? 'day' : 'days'} left, with every game included.
      </p>
    )
  }
  return null
}
