import { useEffect, useState } from 'react'
import { api } from './api'
import type { ThemeCard, ThemePalette } from './types'

let themesPromise: Promise<ThemeCard[]> | null = null

/** Themes change rarely, so fetch them once per page load and share the result. */
export function loadThemes(): Promise<ThemeCard[]> {
  themesPromise ??= api.themes().catch((err) => {
    themesPromise = null
    throw err
  })
  return themesPromise
}

export function useThemes() {
  const [themes, setThemes] = useState<ThemeCard[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    loadThemes().then(setThemes, (e: Error) => setError(e.message))
  }, [])
  return { themes, error }
}

const DEFAULT: ThemePalette = { background: '#0f0d0b', surface: '#1c1814', accent: '#c8a45a', ink: '#f3ead8' }

export function applyPalette(p: ThemePalette = DEFAULT) {
  const root = document.documentElement.style
  root.setProperty('--theme-bg', p.background)
  root.setProperty('--theme-surface', p.surface)
  root.setProperty('--theme-accent', p.accent)
  root.setProperty('--theme-ink', p.ink)
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', p.background)
}

/** Recolours the app for the party's theme while the component is mounted. */
export function useThemePalette(slug: string | undefined) {
  useEffect(() => {
    if (!slug) return
    let active = true
    loadThemes()
      .then((themes) => {
        if (active) applyPalette(themes.find((t) => t.theme.slug === slug)?.theme.palette)
      })
      .catch(() => {})
    return () => {
      active = false
      applyPalette()
    }
  }, [slug])
}
