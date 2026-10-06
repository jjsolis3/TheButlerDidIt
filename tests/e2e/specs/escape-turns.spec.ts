import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { startClock } from './escape-play'

// Game-night fixes (#132), on the Pirate Ship a family played: a puzzle each, only its holder answers it and can read
// where its key is written, the TV says who is on what, and the TV's found items and notebook are never cut off.

const SHOTS = 'screenshots/escape-turns'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }
const SHANTY = 'sea-shanty' // a cipher whose key is written on the salt barrel (and decoys elsewhere)
const BARREL = 'apple-barrel' // "The Riddle Barrel": opening it gives the long ladle

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

/**
 * The TV is the whole room on one screen that nobody scrolls: the found items and the notebook stay in view. On a TV
 * (1080p and up) the puzzles fit too; a small laptop (`laptop`) may scroll the puzzles' own box as a last resort.
 */
async function fitsTheScreen(tv: Page, name: string, laptop = false) {
  await expect.poll(() => tv.evaluate(() => document.documentElement.scrollHeight - window.innerHeight)).toBeLessThanOrEqual(0)
  const found = await tv.getByTestId('found').boundingBox()
  const height = tv.viewportSize()!.height
  expect(found!.y + found!.height, `${name}: the found panel runs off the screen`).toBeLessThanOrEqual(height)
  if (!laptop) await expect(tv.locator('[data-testid="tv-puzzles"] [data-fit="scrolls"]')).toHaveCount(0)
  await tv.screenshot({ path: `${SHOTS}/${name}.png` })
}

test('a puzzle each: only its holder answers it and finds its key, and the TV shows who is on what', async ({ browser }) => {
  test.setTimeout(150_000)
  const tv = await (await browser.newContext({ viewport: { width: 1920, height: 1080 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Captain Host')
  await tv.getByLabel('Email').fill(`turns-${Date.now()}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')

  // ---- The host page: taking a puzzle each is where it starts, and empty searches say what they cost.
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await tv.getByRole('tab', { name: /Family/ }).click()
  await tv.getByRole('button', { name: /The Pirate Ship/ }).click()
  await expect(tv.getByRole('radio', { name: /Take a puzzle/ })).toBeChecked()
  await expect(tv.getByText('A search that turns up nothing costs 10 seconds.')).toBeVisible()
  await tv.screenshot({ path: `${SHOTS}/01-host-page.png`, fullPage: true })
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  const ada = await joinAs(browser, code, 'Ada')
  const ben = await joinAs(browser, code, 'Ben')
  await startClock(tv)
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>
  for (const p of [ada, ben]) await p.getByTestId('stage-card').getByRole('button', { name: 'Got it' }).click()

  // ---- Nobody can answer a puzzle they haven't taken, and its key is for whoever takes it.
  const adaShanty = ada.getByTestId(`phone-puzzle-${SHANTY}`)
  await expect(adaShanty.getByRole('button', { name: '🙋 Take this puzzle' })).toBeVisible()
  await expect(adaShanty.getByLabel('Answer for The Sea Shanty')).toHaveCount(0)
  await expect(adaShanty).toContainText('Its key is written on something in this room: whoever takes this one can find them')
  await expect(tv.getByTestId(`holder-${SHANTY}`)).toContainText("Nobody's on it yet")

  // Ben searches the salt barrel first: the writing isn't his to read. It costs time and the barrel stays unsearched.
  await ben.getByTestId('spot-salt-barrel').click()
  await expect(ben.getByRole('status').filter({ hasText: 'salt barrel' })).toContainText('Nothing there (−10 s)')
  await expect(ben.getByRole('button', { name: 'Search the salt barrel' })).toBeVisible()

  // ---- Ada takes the shanty: the TV and Ben's phone say so, and it's hers alone to answer.
  await adaShanty.getByRole('button', { name: '🙋 Take this puzzle' }).click()
  await expect(adaShanty).toContainText("You're on this one.")
  await expect(tv.getByTestId(`holder-${SHANTY}`)).toContainText('🙋 Ada')
  const benShanty = ben.getByTestId(`phone-puzzle-${SHANTY}`)
  await expect(benShanty).toContainText('Ada is on this one.')
  await expect(benShanty.getByLabel('Answer for The Sea Shanty')).toHaveCount(0)
  await ada.screenshot({ path: `${SHOTS}/02-phone-mine.png`, fullPage: true })
  await ben.screenshot({ path: `${SHOTS}/03-phone-theirs.png`, fullPage: true })

  // One puzzle at a time.
  const adaBarrel = ada.getByTestId(`phone-puzzle-${BARREL}`)
  await adaBarrel.getByRole('button', { name: '🙋 Take this puzzle' }).click()
  await expect(adaBarrel.getByRole('alert')).toContainText("You're working on The Sea Shanty")

  // Ada searches the salt barrel: the writing is hers to read.
  await ada.getByTestId('spot-salt-barrel').click()
  await expect(ada.getByRole('button', { name: /^The salt barrel, searched/ })).toBeVisible()
  await expect(tv.getByText(/Ada found writing on the salt barrel/)).toBeVisible()

  // ---- She passes the shanty to Ben, takes the barrel and opens it: the ladle shows on the TV.
  await adaShanty.getByLabel('Pass The Sea Shanty to').selectOption({ label: 'Ben' })
  await expect(tv.getByTestId(`holder-${SHANTY}`)).toContainText('🙋 Ben')
  await adaBarrel.getByRole('button', { name: '🙋 Take this puzzle' }).click()
  await adaBarrel.getByLabel('Answer for The Riddle Barrel').fill(answers[BARREL]!)
  await adaBarrel.getByRole('button', { name: 'Try' }).click()
  await expect(adaBarrel).toContainText('✅')
  await expect(tv.getByTestId('found-items')).toContainText('Long ladle')

  // ---- The host frees Ben's shanty from the TV.
  await tv.getByTestId(`puzzle-${SHANTY}`).getByRole('button', { name: 'Free it' }).click()
  await expect(tv.getByTestId(`holder-${SHANTY}`)).toContainText("Nobody's on it yet")
  await expect(benShanty.getByRole('button', { name: '🙋 Take this puzzle' })).toBeVisible()

  // ---- The TV at three sizes: nothing scrolls, and the found items and the notebook are on screen.
  for (const [name, width, height] of [
    ['04-tv-1080p', 1920, 1080],
    ['05-tv-720p', 1280, 720],
    ['06-tv-1440p', 2560, 1440],
  ] as const) {
    await tv.setViewportSize({ width, height })
    await expect(tv.getByTestId('tv-room')).toBeVisible()
    await fitsTheScreen(tv, name, height < 1080)
  }
})
