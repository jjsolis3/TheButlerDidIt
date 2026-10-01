import { ESCAPE_PALETTE } from '../lib/theme'
import type { EscapeRecapPage } from '../lib/types'
import { DIFFICULTY } from './labels'
import { formatDuration } from './time'

/**
 * The share card (#111): a picture of how the escape went, for the group chat or social media.
 *
 * It's drawn in the browser on a <canvas> rather than on the server: the browser already has the site's
 * fonts (the server's Docker image has none), and the phone's share sheet takes a picture file directly.
 * The cover comes from the same site, so the canvas isn't "tainted" and can be turned into a PNG.
 */

const W = 1080
const H = 1350 // 4:5, the shape Instagram and most chats show without cropping
const PAD = 80
const COVER_H = 640
const TRAPPED = '#f07171'
const MUTED = 'rgba(230, 240, 236, 0.66)'
const DISPLAY = '"Playfair Display", Georgia, serif'
const BODY = 'Inter, system-ui, sans-serif'

function loadImage(src: string): Promise<HTMLImageElement | null> {
  return new Promise((resolve) => {
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = () => resolve(null) // no picture is fine: the card has a backdrop of its own
    img.src = src
  })
}

/** Fills the box with the picture, cropping what doesn't fit, like CSS object-fit: cover. */
function drawCover(ctx: CanvasRenderingContext2D, img: HTMLImageElement, x: number, y: number, w: number, h: number) {
  const scale = Math.max(w / img.naturalWidth, h / img.naturalHeight)
  const sw = w / scale
  const sh = h / scale
  ctx.drawImage(img, (img.naturalWidth - sw) / 2, (img.naturalHeight - sh) / 2, sw, sh, x, y, w, h)
}

/** Splits text into lines no wider than maxWidth in the current font. A cut last line ends in "…". */
function wrap(ctx: CanvasRenderingContext2D, text: string, maxWidth: number, maxLines: number): string[] {
  const lines: string[] = []
  let line = ''
  for (const word of text.split(/\s+/).filter(Boolean)) {
    const next = line ? `${line} ${word}` : word
    if (ctx.measureText(next).width <= maxWidth || !line) line = next
    else {
      lines.push(line)
      line = word
    }
  }
  if (line) lines.push(line)
  if (lines.length <= maxLines) return lines
  const kept = lines.slice(0, maxLines)
  let last = kept[maxLines - 1]
  while (last.length > 1 && ctx.measureText(`${last}…`).width > maxWidth) last = last.slice(0, -1)
  kept[maxLines - 1] = `${last.trimEnd()}…`
  return kept
}

/** "Ada, Ben and Cy" */
export function joinNames(names: string[]) {
  return names.length <= 1 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

/** Draws the card and returns it as a PNG. `site` is the address printed at the bottom. */
export async function drawShareCard(page: EscapeRecapPage, site: string): Promise<Blob> {
  const r = page.recap
  // The page loads its fonts from Google Fonts; wait for them so the card doesn't fall back to Georgia.
  await Promise.all([document.fonts.load(`700 80px ${DISPLAY}`), document.fonts.load(`600 40px ${BODY}`)]).catch(() => undefined)

  const canvas = document.createElement('canvas')
  canvas.width = W
  canvas.height = H
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error("This browser can't draw the picture.")
  const { background, surface, accent, ink } = ESCAPE_PALETTE
  const mark = r.escaped ? accent : TRAPPED

  ctx.fillStyle = background
  ctx.fillRect(0, 0, W, H)

  // The room's cover across the top, greyed out if the group was trapped; a plain backdrop if none was painted.
  const img = r.coverUrl ? await loadImage(r.coverUrl) : null
  if (img) {
    ctx.filter = r.escaped ? 'none' : 'grayscale(1)'
    drawCover(ctx, img, 0, 0, W, COVER_H)
    ctx.filter = 'none'
  } else {
    const glow = ctx.createRadialGradient(W / 2, COVER_H / 2, 40, W / 2, COVER_H / 2, W * 0.7)
    glow.addColorStop(0, surface)
    glow.addColorStop(1, background)
    ctx.fillStyle = glow
    ctx.fillRect(0, 0, W, COVER_H)
    ctx.font = `200px ${BODY}`
    ctx.textAlign = 'center'
    ctx.fillText(r.escaped ? '🗝️' : '🔒', W / 2, COVER_H / 2 + 70)
    ctx.textAlign = 'left'
  }
  // Fade the picture into the background, so the text below sits on a calm surface.
  const fade = ctx.createLinearGradient(0, COVER_H - 280, 0, COVER_H)
  fade.addColorStop(0, 'rgba(10, 15, 15, 0)')
  fade.addColorStop(1, background)
  ctx.fillStyle = fade
  ctx.fillRect(0, COVER_H - 280, W, 280)

  let y = COVER_H + 50
  ctx.fillStyle = mark
  ctx.font = `600 34px ${BODY}`
  ctx.fillText(r.escaped ? 'E S C A P E D' : 'T R A P P E D', PAD, y)

  ctx.fillStyle = ink
  ctx.font = `700 76px ${DISPLAY}`
  for (const line of wrap(ctx, r.roomTitle, W - PAD * 2, 2)) {
    y += 88
    ctx.fillText(line, PAD, y)
  }

  y += 170
  ctx.fillStyle = mark
  ctx.font = `700 150px ${DISPLAY}`
  ctx.fillText(formatDuration(r.elapsedSeconds), PAD, y)

  y += 70
  ctx.fillStyle = ink
  ctx.font = `500 36px ${BODY}`
  const hints = r.hintsUsed === 1 ? '1 hint' : `${r.hintsUsed} hints`
  ctx.fillText(`${r.solvedCount}/${r.puzzleCount} puzzles · ${hints} · ${DIFFICULTY[r.difficulty].replace(/^\S+\s/, '')} · ${r.timeLimitMinutes} min`, PAD, y)

  if (page.rank) {
    y += 56
    ctx.fillStyle = accent
    ctx.fillText(`🏆 #${page.rank} on the ${r.daily ? "day's challenge" : 'leaderboard'}`, PAD, y)
  }

  ctx.fillStyle = MUTED
  ctx.font = `400 34px ${BODY}`
  for (const line of wrap(ctx, joinNames(r.team.map((p) => p.name)), W - PAD * 2, 2)) {
    y += 52
    ctx.fillText(line, PAD, y)
  }

  // Footer: the game's name and where to find it.
  ctx.fillStyle = surface
  ctx.fillRect(0, H - 110, W, 110)
  ctx.fillStyle = ink
  ctx.font = `700 38px ${DISPLAY}`
  ctx.fillText('The Butler Did It', PAD, H - 44)
  ctx.fillStyle = MUTED
  ctx.font = `400 30px ${BODY}`
  ctx.textAlign = 'right'
  ctx.fillText(site, W - PAD, H - 46)
  ctx.textAlign = 'left'

  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error("The picture couldn't be made."))), 'image/png'))
}

export type ShareOutcome = 'shared' | 'downloaded' | 'cancelled'

/**
 * Shares the card through the phone's share sheet (WhatsApp, Messages, Instagram…), or downloads it where
 * the browser can't share files (most computers). `link` is the recap's address, when the host has shared it.
 */
export async function shareCard(page: EscapeRecapPage, link: string | null): Promise<ShareOutcome> {
  const blob = await drawShareCard(page, window.location.host)
  const r = page.recap
  const name = `${r.roomTitle.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'escape'}-escape.png`
  const file = new File([blob], name, { type: 'image/png' })
  const text = r.escaped ? `We escaped ${r.roomTitle} in ${formatDuration(r.elapsedSeconds)}!` : `We were trapped in ${r.roomTitle}…`
  // The link goes in the text: some apps drop `url` when a file is shared with it.
  const data: ShareData = { files: [file], title: r.roomTitle, text: link ? `${text} ${link}` : text }
  if (navigator.canShare?.(data)) {
    try {
      await navigator.share(data)
      return 'shared'
    } catch (e) {
      if ((e as Error).name === 'AbortError') return 'cancelled' // they closed the share sheet
      // Anything else (a browser that refuses after all): fall back to downloading.
    }
  }
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
  return 'downloaded'
}
