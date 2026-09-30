import { expect, test, type Browser, type Locator, type Page } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'

// Harder rooms: the test-only Laboratory (tests/ButlerDidIt.Escape.Tests/Fixtures, put on the shelf by
// Escape__TestRoomsRoot) uses every newer kind of puzzle: spots to search, items to look at and put
// together, a shift cipher and a Morse cipher whose keys must be found, a number pattern, a logic
// puzzle and a light panel. It's played through the real screens, solo on Hard and with three phones.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }
// A click on something that's gone (the stage moved on) fails in seconds rather than at the test's timeout.
test.use({ actionTimeout: 15_000 })

interface Puzzle {
  id: string
  title: string
  kind: 'code' | 'text' | 'use' | 'search' | 'switches'
  generator?: { type: string }
}
const lab = JSON.parse(readFileSync('../ButlerDidIt.Escape.Tests/Fixtures/the-laboratory.json', 'utf8')) as { puzzles: Puzzle[] }
const byId = new Map(lab.puzzles.map((p) => [p.id, p]))

async function hostTv(browser: Browser, difficulty: RegExp) {
  const tv = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Professor')
  await tv.getByLabel('Email').fill(`lab-${Date.now()}-${Math.random().toString(36).slice(2)}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await tv.getByRole('tab', { name: /Family/ }).click()
  await tv.getByRole('button', { name: /The Laboratory/ }).click()
  // Normal is picked by default; each difficulty has its own leaderboard.
  await expect(tv.getByRole('radio', { name: /Normal/ })).toBeChecked()
  await tv.getByText(difficulty).click()
  return tv
}

async function joinAs(browser: Browser, code: string, name: string) {
  const page = await (await browser.newContext(phone)).newPage()
  page.on('dialog', (d) => d.accept())
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await expect(page.getByText(`You're in, ${name}.`)).toBeVisible()
  return page
}

/** A card's text, or null once it's gone (solving a stage's last puzzle replaces the cards). Never waits for a card to come back. */
async function textOf(card: Locator) {
  return (await card.count()) === 0 ? null : card.textContent({ timeout: 1000 }).catch(() => null)
}

/** The fewest light presses that turn every light on, by trying every set (at most 2^16). */
function solveLights(size: number, lit: boolean[]): number[] {
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

/**
 * Plays the room the way a thorough group would, one move at a time, taking turns on the phones:
 * search every spot it can, look closely at everything, put together what fits, then solve what's open.
 */
async function playThrough(tv: Page, phones: Page[], answers: Record<string, string | null>, shots: string) {
  const locked = new Set<string>() // spots that need a tool nobody holds yet
  const shot = new Set<string>()
  for (let move = 0; move < 200; move++) {
    if (await tv.getByRole('heading', { name: 'You escaped!' }).isVisible()) return
    const p = phones[move % phones.length]

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

    // 2. Look closely at anything with more to see.
    const inspectable = p.getByRole('button', { name: /, more to see/ })
    if ((await inspectable.count()) > 0) {
      const item = p.getByTestId((await inspectable.first().getAttribute('data-testid'))!)
      await item.click()
      const inspector = p.getByTestId('inspector')
      await inspector.getByRole('button', { name: '🔍 Look closer' }).click()
      await expect(inspector.or(p.getByRole('alert')).first()).toBeVisible()
      if (!shot.has('inspect')) {
        shot.add('inspect')
        await expect(inspector).toContainText('🔍 ')
        await p.screenshot({ path: `${SHOTS}/${shots}-inspector.png`, fullPage: true })
      }
      await item.click() // close it
      locked.clear()
      continue
    }

    // 3. Put the bulb in the lamp: choose the second item from the list.
    const bulb = p.getByTestId('item-bulb')
    if ((await bulb.count()) > 0 && (await p.getByTestId('item-lamp-body').count()) > 0) {
      await bulb.click()
      await p.getByRole('button', { name: 'Try a glass bulb with an empty lamp' }).click()
      await expect(p.getByTestId('item-uv-lamp')).toBeVisible()
      locked.clear()
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
          const now = await textOf(card)
          if (now !== null && !now.startsWith('✅')) await expect(cells.nth(cell)).not.toHaveAttribute('aria-pressed', before!)
        }
      } else {
        const input = card.getByLabel(`Answer for ${puzzle.title}`)
        if (puzzle.generator?.type === 'deduction') {
          // Line the jars up with the helper, and let it write the code.
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
      solvedOne = true
      break
    }
    if (!solvedOne) await p.waitForTimeout(200) // another phone's move is still arriving
  }
  throw new Error('The game should have ended')
}

test('harder rooms: a solo player searches, decodes and reasons their way out of the Laboratory on Hard', async ({ browser }) => {
  test.setTimeout(180_000)
  const tv = await hostTv(browser, /😈 Hard/)
  await expect(tv.getByRole('radio', { name: /Hard/ })).toBeChecked()
  await tv.screenshot({ path: `${SHOTS}/96-escape-difficulty.png`, fullPage: true })
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!
  await expect(tv.getByText(/😈 Hard/)).toBeVisible()

  const ada = await joinAs(browser, code, 'Ada')
  await tv.getByRole('button', { name: /Start the clock/ }).click()
  await expect(tv.getByRole('heading', { name: 'The Bench' })).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // The scene is on both screens; nothing in it has been searched, so nothing is given away.
  await expect(tv.getByTestId('scene')).toBeVisible()
  await expect(ada.getByRole('button', { name: 'Search the crate' })).toBeVisible()
  await expect(ada.getByTestId('phone-puzzle-formula')).toContainText('🔒 A decoder unlocks once you find')
  await ada.screenshot({ path: `${SHOTS}/97-escape-scene-phone.png`, fullPage: true })

  // A tool-locked spot says so in the room's own words; a decoy costs time on Hard.
  await ada.getByRole('button', { name: 'Search the poster' }).click()
  await expect(ada.getByRole('status').filter({ hasText: 'Maybe in a different light?' })).toBeVisible()
  await ada.getByRole('button', { name: 'Search the plant' }).click()
  await expect(ada.getByRole('status').filter({ hasText: '(−10 s)' })).toBeVisible()
  // Zoom in on the scene.
  await ada.getByRole('group', { name: 'Zoom' }).getByRole('button', { name: '2×' }).click()
  await expect(ada.getByRole('group', { name: 'Zoom' }).getByRole('button', { name: '2×' })).toHaveAttribute('aria-pressed', 'true')

  await playThrough(tv, [ada], answers, '98-escape-solo')

  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(ada.getByText('You escaped!')).toBeVisible()
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Hard')
  await expect(tv.getByTestId('your-rank')).toContainText(/You ranked #\d+/)
})

test('harder rooms: three phones share the search and the clues on Normal', async ({ browser }) => {
  test.setTimeout(180_000)
  const tv = await hostTv(browser, /😐 Normal/)
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!
  const phones = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben'), await joinAs(browser, code, 'Cy')]
  await tv.getByRole('button', { name: /Start the clock/ }).click()
  await expect(tv.getByRole('heading', { name: 'The Bench' })).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // A search on one phone shows on the others and on the TV.
  await phones[0].getByRole('button', { name: 'Search the crate' }).click()
  await expect(phones[1].getByRole('button', { name: /^The crate, searched/ })).toBeVisible()
  await expect(tv.getByRole('img', { name: /^The crate, searched/ })).toBeVisible()
  await expect(phones[2].getByTestId('item-bulb')).toBeVisible()

  await playThrough(tv, phones, answers, '99-escape-group')
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Normal')
})
