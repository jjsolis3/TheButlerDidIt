import type { AccessView, GameKind } from './types'

/** May this host start this game? (The server checks it again; this is so the page can say so first.) */
export const allows = (access: AccessView, game: GameKind) => (game === 'escapeRoom' ? access.escapeRooms : access.mysteries)

export const gameName = (game: GameKind) => (game === 'escapeRoom' ? 'Escape rooms' : 'Murder mysteries')

const DAY = 86_400_000
const shortDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })

/** Whole days until `iso`, rounded up, so the last day reads "1 day left" rather than "0". */
export const daysLeft = (iso: string, now = Date.now()) => Math.max(0, Math.ceil((new Date(iso).getTime() - now) / DAY))

/** How a host's access reads on their pages: a short name, and a line about what it means. */
export function describeAccess(access: AccessView): { title: string; detail: string } {
  switch (access.plan) {
    case 'admin':
      return { title: 'Admin', detail: 'You run this site, so every game is included.' }
    case 'free':
      return { title: 'Free access', detail: "Given by the site's admin. There's nothing to pay." }
    case 'subscription':
      return { title: 'Subscription', detail: access.endsAt ? `Renews ${shortDate(access.endsAt)}.` : 'Active.' }
    case 'pass':
      return { title: 'Party pass', detail: access.endsAt ? `Until ${shortDate(access.endsAt)}.` : 'Active.' }
    case 'trial': {
      const days = access.endsAt ? daysLeft(access.endsAt) : 0
      return { title: 'Free trial', detail: `${days} ${days === 1 ? 'day' : 'days'} left, with every game included.` }
    }
    case 'trialEnded':
      return { title: 'Trial ended', detail: "Your free trial is over. Ask the site's admin for access." }
    default:
      return { title: 'No plan', detail: "Ask the site's admin for access." }
  }
}
