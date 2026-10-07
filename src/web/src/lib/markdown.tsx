import type { ReactNode } from 'react'
import { Link } from 'react-router'

/**
 * A small Markdown reader for the terms, privacy and refund pages (#103): headings (#, ##, ###), paragraphs, bulleted
 * and numbered lists, **bold**, _italic_ and [links](/privacy). That is all those pages use; anything else shows as
 * plain text.
 *
 * It builds React elements rather than HTML, so nothing in a page can run as code, and a link only goes somewhere on
 * this site, to a web address or to an email address.
 */

type Block = { kind: 'h1' | 'h2' | 'h3' | 'p'; text: string } | { kind: 'ul' | 'ol'; items: string[] }

/** Splits the text into headings, paragraphs and lists. A blank line ends a paragraph or a list. */
function blocks(markdown: string): Block[] {
  const out: Block[] = []
  // Whether the last block can still take more lines: a blank line or a heading closes it.
  let open = false
  for (const raw of markdown.split('\n')) {
    const line = raw.trim()
    const heading = /^(#{1,3})\s+(.+)$/.exec(line)
    const item = /^(?:([-*])|\d+[.)])\s+(.+)$/.exec(line)
    const last = open ? out[out.length - 1] : undefined
    if (line === '') open = false
    else if (heading) {
      out.push({ kind: `h${heading[1].length}` as 'h1' | 'h2' | 'h3', text: heading[2] })
      open = false
    } else if (item) {
      const kind = item[1] ? 'ul' : 'ol'
      if (last && 'items' in last && last.kind === kind) last.items.push(item[2])
      else {
        out.push({ kind, items: [item[2]] })
        open = true
      }
    } else if (last && 'items' in last) {
      // A wrapped line carries on the item above it.
      last.items[last.items.length - 1] += ` ${line}`
    } else if (last && last.kind === 'p') last.text += ` ${line}`
    else {
      out.push({ kind: 'p', text: line })
      open = true
    }
  }
  return out
}

/** A heading's anchor, so a link like /privacy#children lands on it. */
function anchor(text: string): string {
  return text
    .replace(/[*_]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
}

// Bold, then italic (an underscore inside a word, as in an email address, isn't italic), then a link.
const INLINE = /\*\*(.+?)\*\*|(?<![\w])_(.+?)_(?![\w])|\[([^\]]+)\]\(([^)\s]+)\)/g

/** Bold, italic and links inside a line. */
function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  let last = 0
  for (const m of text.matchAll(INLINE)) {
    if (m.index > last) out.push(text.slice(last, m.index))
    if (m[1] !== undefined) out.push(<strong key={m.index}>{inline(m[1])}</strong>)
    else if (m[2] !== undefined) out.push(<em key={m.index}>{inline(m[2])}</em>)
    else out.push(<Linked key={m.index} label={m[3]} href={m[4]} />)
    last = m.index + m[0].length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

function Linked({ label, href }: { label: string; href: string }) {
  const className = 'text-accent underline'
  if (href.startsWith('/')) {
    return (
      <Link to={href} className={className}>
        {inline(label)}
      </Link>
    )
  }
  if (/^mailto:/i.test(href)) {
    return (
      <a href={href} className={className}>
        {inline(label)}
      </a>
    )
  }
  if (/^https?:\/\//i.test(href)) {
    return (
      <a href={href} className={className} target="_blank" rel="noopener noreferrer">
        {inline(label)}
      </a>
    )
  }
  return <>{inline(label)}</>
}

/** A Markdown page, styled for reading. */
export function Markdown({ text }: { text: string }) {
  return (
    <div className="space-y-4 leading-relaxed">
      {blocks(text).map((b, i) => {
        switch (b.kind) {
          case 'h1':
            return (
              <h1 key={i} id={anchor(b.text)} className="font-display text-4xl">
                {inline(b.text)}
              </h1>
            )
          case 'h2':
            return (
              <h2 key={i} id={anchor(b.text)} className="font-display scroll-mt-4 pt-4 text-2xl">
                {inline(b.text)}
              </h2>
            )
          case 'h3':
            return (
              <h3 key={i} id={anchor(b.text)} className="scroll-mt-4 pt-2 text-lg font-semibold">
                {inline(b.text)}
              </h3>
            )
          case 'p':
            return <p key={i}>{inline(b.text)}</p>
          case 'ul':
          case 'ol': {
            const List = b.kind
            return (
              <List key={i} className={`space-y-2 pl-6 ${b.kind === 'ul' ? 'list-disc' : 'list-decimal'}`}>
                {b.items.map((item, j) => (
                  <li key={j}>{inline(item)}</li>
                ))}
              </List>
            )
          }
        }
      })}
    </div>
  )
}
