import { useEffect, useState } from 'react'
import { useLocation } from 'react-router'
import { ErrorText, Shell } from '../components/ui'
import { api } from '../lib/api'
import { Markdown } from '../lib/markdown'
import type { LegalPage } from '../lib/types'
import { useMe } from '../lib/useMe'

/**
 * The terms, privacy and refund pages (#103). The words live in content/legal on the server, which fills in the site's
 * own details (who runs it, how long things are kept, which services it uses), so the page and the site never disagree.
 */
export default function Legal({ page }: { page: LegalPage['page'] }) {
  const [doc, setDoc] = useState<LegalPage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { me } = useMe()
  const { hash } = useLocation()

  useEffect(() => {
    let current = true
    api.legal(page).then(
      (d) => current && setDoc(d),
      (e: Error) => current && setError(e.message),
    )
    return () => {
      current = false
    }
  }, [page])

  // A link such as /privacy#children: the section is only there once the page has loaded.
  useEffect(() => {
    if (doc && hash) document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView()
  }, [doc, hash])

  return (
    <Shell>
      {doc?.draft && me?.isAdmin && (
        <p className="mb-6 rounded-lg border border-accent/60 bg-accent/10 p-3 text-sm" data-testid="legal-draft">
          <span className="font-semibold">Only you see this: </span>
          this page is still the starter draft. Have it reviewed, then delete the first line of content/legal/{page}.md.
        </p>
      )}
      <ErrorText>{error}</ErrorText>
      {doc ? <Markdown text={doc.markdown} /> : !error && <p className="text-muted">Loading…</p>}
    </Shell>
  )
}
