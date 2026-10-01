import { useCallback, useSyncExternalStore } from 'react'

/**
 * Big enough for the escape room's TV layout (#116): the whole room on one screen, never scrolling.
 * Narrower or shorter screens (phones watching, small windows) keep the stacked layout, which scrolls.
 */
export const TV_LAYOUT = '(min-width: 1024px) and (min-height: 600px)'

/**
 * Whether a CSS media query matches, kept up to date as the window changes size. useSyncExternalStore is
 * React's way of reading something that lives outside React (here, the browser's matchMedia): every render
 * sees the same answer, and a resize re-renders only when the answer flips.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onChange: () => void) => {
      const list = window.matchMedia(query)
      list.addEventListener('change', onChange)
      return () => list.removeEventListener('change', onChange)
    },
    [query],
  )
  return useSyncExternalStore(subscribe, () => window.matchMedia(query).matches)
}
