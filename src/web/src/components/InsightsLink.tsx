import { Link } from 'react-router'
import type { PlaySummary } from '../lib/types'

/**
 * A library card's line about how its games went (#130): the guests' rating and the games recorded, and a link to
 * the full insights. Only on cards whose insights the host may see (their own, and the built-in ones for the admin).
 */
export function InsightsLink({ summary, to }: { summary: PlaySummary | null; to: string }) {
  if (!summary) return null
  return (
    <p className="mt-1 text-sm text-muted">
      {summary.rating === null ? 'No ratings yet' : `★ ${summary.rating} from ${summary.ratings} guest${summary.ratings === 1 ? '' : 's'}`} · {summary.plays} game
      {summary.plays === 1 ? '' : 's'} recorded ·{' '}
      <Link to={to} className="text-accent underline hover:text-ink">
        📊 Insights
      </Link>
    </p>
  )
}
