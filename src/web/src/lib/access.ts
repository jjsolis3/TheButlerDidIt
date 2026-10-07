import type { AccessView, GameKind, PlanOfferView } from './types'

/** May this host start this game? (The server checks it again; this is so the page can say so first.) */
export const allows = (access: AccessView, game: GameKind) => (game === 'escapeRoom' ? access.escapeRooms : access.mysteries)

export const gameName = (game: GameKind) => (game === 'escapeRoom' ? 'Escape rooms' : 'Murder mysteries')

const DAY = 86_400_000
const shortDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
/** A party pass lasts hours, so its end needs the time too: "Fri 10 Oct, 14:00". */
const dayAndTime = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' })

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
      // Its state at Stripe (#101): renewing, in its free days, cancelled, or waiting on a card.
      switch (access.status) {
        case 'trialing':
          return { title: 'Subscription', detail: access.renewsAt ? `Free until ${shortDate(access.renewsAt)}, then it renews.` : 'Active.' }
        case 'ending':
          return { title: 'Subscription', detail: access.endsAt ? `Cancelled: your games stay until ${shortDate(access.endsAt)}.` : 'Cancelled.' }
        case 'pastDue':
          return {
            title: 'Subscription',
            detail: `Your last payment didn't go through. Update your card with Manage billing${access.endsAt ? ` by ${shortDate(access.endsAt)}` : ''} to keep your games.`,
          }
        default:
          return { title: 'Subscription', detail: access.renewsAt ? `Renews ${shortDate(access.renewsAt)}.` : 'Active.' }
      }
    case 'pass':
      return { title: 'Party pass', detail: access.endsAt ? `Until ${dayAndTime(access.endsAt)}.` : 'Active.' }
    case 'trial': {
      const days = access.endsAt ? daysLeft(access.endsAt) : 0
      return { title: 'Free trial', detail: `${days} ${days === 1 ? 'day' : 'days'} left, with every game included.` }
    }
    case 'trialEnded':
      return {
        title: 'Trial ended',
        detail: access.payments ? 'Your free trial is over. Choose a plan to keep hosting.' : "Your free trial is over. Ask the site's admin for access.",
      }
    default:
      return { title: 'No plan', detail: access.payments ? 'Choose a plan to start hosting.' : "Ask the site's admin for access." }
  }
}

/** What a plan gives, in a few words. */
export const planGames = (p: Pick<PlanOfferView, 'mysteries' | 'escapeRooms'>) =>
  p.mysteries && p.escapeRooms ? 'Both games' : p.mysteries ? 'Murder mysteries' : 'Escape rooms'

/**
 * A price as Stripe gives it, in the currency's smallest unit, written the viewer's way: "$12.00", "12,00 €", "¥1,200".
 * The number of decimals comes from the currency itself, so yen (none) and dollars (two) both come out right.
 */
export function formatPrice(amount: number, currency: string) {
  const format = new Intl.NumberFormat(undefined, { style: 'currency', currency: currency.toUpperCase() })
  return format.format(amount / 10 ** (format.resolvedOptions().maximumFractionDigits ?? 2))
}
