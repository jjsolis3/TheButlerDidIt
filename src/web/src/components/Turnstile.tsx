import { useEffect, useRef } from 'react'

/** What Cloudflare's script puts on the page (only the parts used here). */
interface TurnstileApi {
  render: (
    element: HTMLElement,
    options: {
      sitekey: string
      callback: (token: string) => void
      'expired-callback': () => void
      'error-callback': () => void
      theme: 'dark' | 'light' | 'auto'
    },
  ) => string
  remove: (widgetId: string) => void
}

declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

const SCRIPT = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
let loading: Promise<void> | null = null

/** Cloudflare's script, added to the page once, the first time a form needs it. */
function loadTurnstile(): Promise<void> {
  loading ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SCRIPT
    script.async = true
    script.onload = () => resolve()
    script.onerror = () => {
      loading = null // let a later form try again
      reject(new Error("The check that you're a person couldn't load. Reload the page to try again."))
    }
    document.head.appendChild(script)
  })
  return loading
}

/**
 * Cloudflare Turnstile (#103): proves a person is filling in the form. Usually it needs no clicking; when it's sure, it
 * hands over a one-time token, which the form sends with the sign-up and the server checks with Cloudflare. A token
 * works once, so the form gives this a new `key` after a failed try, which draws a fresh widget.
 *
 * It's only on the page when the site has Turnstile keys, so a site without them never loads Cloudflare's script.
 */
export function Turnstile({ siteKey, onToken, onError }: { siteKey: string; onToken: (token: string | null) => void; onError: (message: string) => void }) {
  const box = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let widget: string | undefined
    let current = true
    loadTurnstile().then(
      () => {
        if (!current || !box.current || !window.turnstile) return
        widget = window.turnstile.render(box.current, {
          sitekey: siteKey,
          callback: (token) => onToken(token),
          // A token lasts 5 minutes; after that, or on a problem, the form waits for a new one.
          'expired-callback': () => onToken(null),
          'error-callback': () => onToken(null),
          theme: 'dark',
        })
      },
      (e: Error) => current && onError(e.message),
    )
    return () => {
      current = false
      if (widget) window.turnstile?.remove(widget)
    }
  }, [siteKey, onToken, onError])

  return <div ref={box} className="min-h-[65px]" data-testid="human-check" />
}
