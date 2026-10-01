import type { ButtonHTMLAttributes, ReactNode } from 'react'
import { Link } from 'react-router'
import { buttonClass, type Variant } from './buttonClass'

export function Button({
  variant = 'primary',
  className = '',
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return <button {...props} className={buttonClass(variant, className)} />
}

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rounded-xl border border-line bg-surface p-5 ${className}`}>{children}</div>
}

export function Heading({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <h1 className={`font-display text-3xl leading-tight text-ink sm:text-4xl ${className}`}>{children}</h1>
}

export function Eyebrow({ children }: { children: ReactNode }) {
  return <p className="text-xs font-semibold tracking-[0.2em] text-accent uppercase">{children}</p>
}

export function ErrorText({ children }: { children: ReactNode }) {
  if (!children) return null
  return (
    <p role="alert" className="rounded-lg border border-blood/50 bg-blood/10 px-3 py-2 text-sm text-red-200">
      {children}
    </p>
  )
}

export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) {
  return (
    <label className="block space-y-1.5">
      <span className="text-sm font-medium text-ink">{label}</span>
      {children}
      {hint && <span className="block text-xs text-muted">{hint}</span>}
    </label>
  )
}

export const inputClass =
  'w-full rounded-lg border border-line bg-bg px-3 py-2.5 text-base text-ink placeholder:text-muted/70 focus:border-accent focus:outline-none'

export function Shell({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="grain min-h-dvh">
      <header className="mx-auto flex max-w-6xl items-center justify-between px-4 py-4">
        <Link to="/" className="font-display text-lg text-ink">
          The Butler <span className="text-accent italic">Did It</span>
        </Link>
      </header>
      <main className={`mx-auto px-4 pb-16 ${wide ? 'max-w-6xl' : 'max-w-2xl'}`}>{children}</main>
    </div>
  )
}

export function StatusPill({ status }: { status: string }) {
  if (status === 'connected') return null
  return (
    <div className="fixed top-3 left-1/2 z-50 -translate-x-1/2 rounded-full border border-line bg-surface px-3 py-1 text-xs text-muted shadow-lg">
      {status === 'connecting' ? 'Connecting…' : 'Reconnecting…'}
    </div>
  )
}

/** A round on/off chip for filtering a shelf (e.g. 🎃 Halloween). With `glow`, it pulses gently to be noticed. */
export function FilterChip({ on, glow = false, onClick, children }: { on: boolean; glow?: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      onClick={onClick}
      className={`rounded-full border px-3 py-1 text-sm font-semibold transition ${
        on ? 'border-accent bg-accent text-bg' : 'border-line text-muted hover:text-ink'
      } ${glow ? 'animate-pulse ring-2 ring-accent/70' : ''}`}
    >
      {children}
    </button>
  )
}
