import { useEffect } from 'react'

/**
 * Asks search engines not to index the page while it's open (the API response says the same). For the
 * shared recap pages: a link someone posted publicly still shouldn't put a family's party in search results.
 */
export function useNoIndex() {
  useEffect(() => {
    const meta = document.createElement('meta')
    meta.name = 'robots'
    meta.content = 'noindex'
    document.head.appendChild(meta)
    return () => meta.remove()
  }, [])
}
