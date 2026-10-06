/** A whole-number percentage, 0 when there's nothing to divide by. */
export const percent = (part: number, whole: number) => (whole === 0 ? 0 : Math.round((part / whole) * 100))

/** Money for the admin: cents for small sums, so a few calls' cost doesn't round to $0.00. */
export const money = (n: number) => `$${n.toFixed(n < 1 ? 4 : 2)}`

/** A file size in the largest unit that keeps it at or above 1. */
export function bytes(n: number) {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024
    i++
  }
  return `${i === 0 ? n : n.toFixed(n < 10 ? 1 : 0)} ${units[i]}`
}

/** "today", "yesterday", "5 days ago", or a date for anything older than a month. */
export function ago(iso: string, now = Date.now()) {
  const days = Math.floor((now - new Date(iso).getTime()) / 86_400_000)
  if (days <= 0) return 'today'
  if (days === 1) return 'yesterday'
  if (days < 31) return `${days} days ago`
  return new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })
}
