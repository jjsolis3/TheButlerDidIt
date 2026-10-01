export type Variant = 'primary' | 'ghost' | 'danger' | 'quiet'

const variants: Record<Variant, string> = {
  primary: 'bg-accent text-bg hover:brightness-110 font-semibold',
  ghost: 'border border-line text-ink hover:border-accent hover:text-accent',
  danger: 'border border-blood/60 text-red-200 hover:bg-blood/20',
  quiet: 'text-muted hover:text-ink',
}

/** A button's look, for a link that should look like one (a real link can be opened in a new tab). */
export const buttonClass = (variant: Variant = 'primary', className = '') =>
  `inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-40 ${variants[variant]} ${className}`
