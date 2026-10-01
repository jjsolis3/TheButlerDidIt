import { expect, type Locator, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'

// Plays an escape room through the real screens, the way a thorough group would. Shared by the escape specs.

export const SHOTS = 'screenshots'

export interface Puzzle {
  id: string
  title: string
  kind: 'code' | 'text' | 'use' | 'search' | 'switches'
  minMinutes?: number
  minDifficulty?: string
  generator?: { type: string }
}
export interface RoomFile {
  stages: { title: string; puzzles: string[] }[]
  puzzles: Puzzle[]
  items: { id: string; name: string }[]
  recipes?: { items: string[]; makes: string }[]
}

/** A room's file, for its layout (the answers are shuffled each game: ask the server for them). */
export const loadRoom = (path: string) => JSON.parse(readFileSync(path, 'utf8')) as RoomFile

/** A card's text, or null once it's gone (solving a stage's last puzzle replaces the cards). Never waits for a card to come back. */
export async function textOf(card: Locator) {
  return (await card.count()) === 0 ? null : card.textContent({ timeout: 1000 }).catch(() => null)
}

/** The fewest light presses that turn every light on, by trying every set (at most 2^16). */
export function solveLights(size: number, lit: boolean[]): number[] {
  const cells = size * size
  const toggles = Array.from({ length: cells }, (_, c) => {
    const r = Math.floor(c / size), k = c % size
    let m = 0
    for (const [dr, dk] of [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const rr = r + dr, kk = k + dk
      if (rr >= 0 && rr < size && kk >= 0 && kk < size) m |= 1 << (rr * size + kk)
    }
    return m
  })
  const start = lit.reduce((m, on, c) => (on ? m | (1 << c) : m), 0)
  const all = (1 << cells) - 1
  let best: number[] | null = null
  for (let set = 0; set <= all; set++) {
    let state = start
    const presses: number[] = []
    for (let c = 0; c < cells; c++) if ((set >> c) & 1) { state ^= toggles[c]; presses.push(c) }
    if (state === all && (!best || presses.length < best.length)) best = presses
  }
  return best!
}

/** Presses Start on the TV: the room's intro plays first (#110), and skipping it starts the clock. */
export async function startClock(tv: Page) {
  await tv.getByRole('button', { name: /Start the clock/ }).click()
  const intro = tv.getByTestId('room-reveal')
  await expect(intro).toBeVisible()
  await intro.getByRole('button', { name: /Skip and start the clock/ }).click()
  await expect(intro).toHaveCount(0)
}

/**
 * Plays the room the way a thorough group would, one move at a time, taking turns on the phones:
 * search every spot it can, look closely at everything, put together what fits, then solve what's open.
 * Returns the moments it saw along the way (a stage's reveal is "reveal").
 */
export async function playThrough(tv: Page, phones: Page[], room: RoomFile, answers: Record<string, string | null>, shots: string): Promise<Set<string>> {
  const byId = new Map(room.puzzles.map((p) => [p.id, p]))
  const itemName = (id: string) => room.items.find((i) => i.id === id)!.name
  const locked = new Set<string>() // spots that need a tool nobody holds yet
  const unreadable = new Set<string>() // items whose closer look needs a tool nobody holds yet
  const shot = new Set<string>()
  for (let move = 0; move < 200; move++) {
    if (await tv.getByRole('heading', { name: 'You escaped!' }).isVisible()) return shot
    const p = phones[move % phones.length]

    // On the TV layout (#116), every stage's puzzles must fit their column: nobody scrolls the TV.
    const overflow = await tv.locator('[data-testid="tv-puzzles"]').evaluateAll((els) => els.map((el) => el.scrollHeight - el.clientHeight))
    if (overflow.some((px) => px > 1)) shot.add(`puzzles overflow by ${Math.max(...overflow)}px in ${await tv.getByRole('heading', { level: 1 }).first().textContent()}`)

    // A new stage opens with a reveal on the TV (#110) and a card at the top of every phone. The TV's stays up
    // for a few seconds and the phones' until tapped, so neither gets in the way of the next move.
    if (!shot.has('reveal') && (await tv.getByTestId('room-reveal').isVisible())) {
      shot.add('reveal')
      await tv.screenshot({ path: `${SHOTS}/${shots}-reveal-tv.png` })
      await expect(p.getByTestId('stage-card')).toBeVisible()
      await p.screenshot({ path: `${SHOTS}/${shots}-stage-card.png` })
    }

    // 1. Search a spot nobody has searched (a locked one waits until the group's items change).
    const spots = p.getByRole('button', { name: /^Search the / })
    let searched = false
    for (const spot of await spots.all()) {
      const id = (await spot.getAttribute('data-testid'))!
      if (locked.has(id)) continue
      await spot.click()
      // Either it's searched now, or the room says a tool is needed.
      const done = p.getByTestId(id).and(p.getByRole('button', { name: /, searched/ }))
      const needsTool = p.getByRole('status').filter({ hasText: '🔒' })
      await expect(done.or(needsTool)).toBeVisible()
      if (await needsTool.isVisible()) locked.add(id)
      searched = true
      break
    }
    if (searched) continue

    // 2. Look closely at anything with more to see (one that needs a tool waits until the group's items change).
    let looked = false
    for (const button of await p.getByRole('button', { name: /, more to see/ }).all()) {
      const testId = (await button.getAttribute('data-testid'))!
      if (unreadable.has(testId)) continue
      const item = p.getByTestId(testId)
      await item.click()
      const inspector = p.getByTestId('inspector')
      await inspector.getByRole('button', { name: '🔍 Look closer' }).click()
      const failed = p.getByRole('alert')
      await expect(inspector.getByRole('status').or(failed).first()).toBeVisible()
      if (await failed.isVisible()) unreadable.add(testId)
      else if (!shot.has('inspect')) {
        shot.add('inspect')
        await p.screenshot({ path: `${SHOTS}/${shots}-inspector.png`, fullPage: true })
      }
      await item.click() // close it
      looked = true
      break
    }
    if (looked) {
      locked.clear()
      continue
    }

    // 3. Put together any pair that fits: open the first item and choose the second from the list.
    let combined = false
    for (const recipe of room.recipes ?? []) {
      const [a, b] = recipe.items
      if ((await p.getByTestId(`item-${a}`).count()) === 0 || (await p.getByTestId(`item-${b}`).count()) === 0) continue
      await p.getByTestId(`item-${a}`).click()
      await p.getByRole('button', { name: `Try ${itemName(a)} with ${itemName(b)}` }).click()
      await expect(p.getByTestId(`item-${recipe.makes}`)).toBeVisible()
      combined = true
      break
    }
    if (combined) {
      locked.clear()
      unreadable.clear()
      continue
    }

    // 4. Solve the first open puzzle on this phone.
    let solvedOne = false
    const ids = await p.locator('[data-testid^="phone-puzzle-"]').evaluateAll((cards) => cards.map((c) => c.getAttribute('data-testid')!.replace('phone-puzzle-', '')))
    for (const id of ids) {
      // By id, not position: solving the stage's last puzzle opens the next stage, and the cards change.
      const card = p.getByTestId(`phone-puzzle-${id}`)
      const puzzle = byId.get(id)!
      const text = await textOf(card)
      if (text === null || text.startsWith('✅') || puzzle.kind === 'search') continue
      if (await card.getByText(/^You need /).isVisible()) continue
      if (puzzle.kind === 'use') {
        await card.getByRole('button', { name: 'Use it' }).click()
      } else if (puzzle.kind === 'switches') {
        const cells = card.getByRole('button', { name: /^Light / })
        const lit = await Promise.all((await cells.all()).map(async (c) => (await c.getAttribute('aria-pressed')) === 'true'))
        if (!shot.has('lights')) {
          shot.add('lights')
          await card.screenshot({ path: `${SHOTS}/${shots}-lights.png` })
        }
        for (const cell of solveLights(Math.sqrt(lit.length), lit)) {
          const before = await cells.nth(cell).getAttribute('aria-pressed')
          await cells.nth(cell).click()
          // The light flips, or (on the last press) the panel is solved and its grid gives way to the solved text.
          await expect
            .poll(async () => {
              const now = await textOf(card)
              if (now === null || now.startsWith('✅')) return true
              return (await cells.nth(cell).getAttribute('aria-pressed', { timeout: 500 }).catch(() => before)) !== before
            })
            .toBe(true)
        }
      } else {
        const input = card.getByLabel(`Answer for ${puzzle.title}`)
        if (puzzle.generator?.type === 'deduction') {
          // Line them up with the helper, and let it write the code.
          await card.getByText('🧠 Work it out').click()
          const code = answers[id]!
          const lineUp = card.getByRole('list', { name: 'Line-up' })
          const items = await lineUp.getByRole('listitem').locator('span.flex-1').allTextContents()
          const listed = [...items] // the helper starts in the listed order, the order the code reads
          for (let spot = 0; spot < listed.length; spot++) {
            const want = listed[[...code].findIndex((d) => Number(d) === spot + 1)]
            const now = (await lineUp.getByRole('listitem').locator('span.flex-1').allTextContents()).indexOf(want)
            for (let k = now; k > spot; k--) await card.getByRole('button', { name: `Move ${want} left` }).click()
          }
          await card.getByRole('button', { name: `Use this order (${code})` }).click()
          await expect(input).toHaveValue(code)
          if (!shot.has('logic')) {
            shot.add('logic')
            await card.screenshot({ path: `${SHOTS}/${shots}-logic.png` })
          }
        } else {
          if (puzzle.generator?.type === 'cipher') {
            // The decoder is unlocked: the key has been found by now (the thorough group searched first).
            await expect(card.getByTestId('cipher-tool')).toBeVisible()
            if (!shot.has(`cipher-${id}`)) {
              shot.add(`cipher-${id}`)
              await card.getByText('🔑 Decoder').click()
              await card.screenshot({ path: `${SHOTS}/${shots}-decoder-${id}.png` })
            }
          }
          await input.fill(answers[id]!)
        }
        await card.getByRole('button', { name: 'Try' }).click()
      }
      try {
        await expect.poll(async () => (await textOf(card))?.startsWith('✅') ?? true).toBe(true)
      } catch {
        throw new Error(`Couldn't solve ${id}: ${await textOf(card)}`)
      }
      locked.clear()
      unreadable.clear()
      solvedOne = true
      break
    }
    if (!solvedOne) await p.waitForTimeout(200) // another phone's move is still arriving
  }
  throw new Error('The game should have ended')
}
