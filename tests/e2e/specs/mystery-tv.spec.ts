import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The murder mystery on a TV (#129): a full evening with eight guests, checked phase by phase on a 1080p and a 720p
// screen. Nobody scrolls a TV, so the page must never scroll, and no phase may be so tall that it falls back to
// scrolling inside its own box (FitToScreen's last resort). A little scaling down on the smaller screen is fine.

const SHOTS = 'screenshots/mystery-tv'
mkdirSync(SHOTS, { recursive: true })
const SCREENS = [
  { name: '1080p', width: 1920, height: 1080 },
  { name: '720p', width: 1280, height: 720 },
]

async function fitsBothScreens(page: Page, phase: string) {
  for (const s of SCREENS) {
    await page.setViewportSize({ width: s.width, height: s.height })
    await expect(page.locator('[data-layout="tv"]')).toBeVisible()
    await expect
      .poll(() => page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight), { message: `${phase} at ${s.name}: the page scrolls` })
      .toBeLessThanOrEqual(0)
    await expect(page.locator('[data-fit="scrolls"]'), `${phase} at ${s.name}: too tall to fit even scaled down`).toHaveCount(0)
    await page.screenshot({ path: `${SHOTS}/${phase}-${s.name}.png` })
  }
  await page.setViewportSize({ width: 1920, height: 1080 })
}

async function joinAs(browser: Browser, code: string, name: string) {
  const page = await (await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage()
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  return page
}

test('the murder mystery fills a TV and never scrolls, from the lobby to the awards', async ({ browser }) => {
  test.setTimeout(240_000)
  const stage = await (await browser.newContext({ viewport: { width: 1920, height: 1080 } })).newPage()
  const guests = ['Ada', 'Ben', 'Cy', 'Dee', 'Eve', 'Fay']
  let asked = 0
  stage.on('dialog', (d) => (d.type() === 'prompt' ? d.accept(guests[asked++ % guests.length]) : d.accept()))
  await stage.goto('/login')
  await stage.getByRole('button', { name: 'Create an account' }).click()
  await stage.getByLabel('Your name').fill('Tilly Telly')
  await stage.getByLabel('Email').fill(`tv-${Date.now()}@example.com`)
  await stage.getByLabel('Password').fill('password123')
  await stage.getByRole('button', { name: 'Create account' }).click()
  await expect(stage.getByText('Death at Blackwood Manor')).toBeVisible()
  await stage.getByLabel('Version', { exact: true }).selectOption('death-at-blackwood-manor')
  await stage.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await stage.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = stage.url().split('/').pop()!

  // Two phones and six pass-and-play guests: a full table of eight.
  const phones = [await joinAs(browser, code, 'Alice'), await joinAs(browser, code, 'Bob')]
  for (let i = 0; i < guests.length; i++) await stage.getByRole('button', { name: 'Add a pass-and-play guest' }).click()
  await expect(stage.getByText('8 of up to')).toBeVisible()
  // The watchers panel is a chip in the top bar on the TV, not a panel under the lobby.
  const watchers = stage.getByTestId('watchers')
  await expect(watchers).toHaveCount(1)
  await expect(stage.locator('header').getByTestId('watchers')).toContainText('watching')
  await fitsBothScreens(stage, '01-lobby')

  await stage.getByRole('button', { name: 'Begin the evening' }).click()
  await stage.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(stage.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  await fitsBothScreens(stage, '02-cast')
  await stage.getByRole('button', { name: 'Play the prologue' }).click()
  await fitsBothScreens(stage, '03-prologue')
  await stage.getByRole('button', { name: 'Begin Act One' }).click()
  await fitsBothScreens(stage, '04-act-scene')
  await stage.getByRole('button', { name: 'Start mingling' }).click()
  await fitsBothScreens(stage, '05-mingle')

  // Every secret out and every clue dropped: the longest the lists get.
  for (const p of phones) {
    await p.getByRole('button', { name: 'Secrets' }).click()
    await p.getByRole('button', { name: 'Reveal to everyone' }).first().click()
  }
  await stage.getByRole('button', { name: 'End Act 1' }).click()
  await stage.getByRole('button', { name: 'Start mingling' }).click()
  await stage.getByRole('button', { name: 'End Act 2' }).click()
  await stage.getByRole('button', { name: 'Start mingling' }).click()
  // "Drop next clue (3)": drop until none are left, waiting for the count to change after each.
  const drop = stage.getByRole('button', { name: /Drop next clue/ })
  for (let left = 0; (left = Number((await drop.textContent())?.match(/\((\d+)\)/)?.[1] ?? 0)) > 0; ) {
    await drop.click()
    await expect(drop).not.toHaveText(`Drop next clue (${left})`)
  }
  // The TV shows the newest evidence in full and names the rest; the phones keep every clue.
  await expect(stage.getByTestId('earlier-evidence')).toContainText('The Silver Candlestick')
  await fitsBothScreens(stage, '06-mingle-every-clue')

  await stage.getByRole('button', { name: 'Time for accusations' }).click()
  await expect(stage.getByRole('heading', { name: /Who killed/ })).toBeVisible()
  await fitsBothScreens(stage, '07-accusation')
  for (const p of phones) {
    await p.getByRole('button', { name: /Dr\. Cornelius Finch|Mr\. Alistair Hargrove/ }).first().click()
    await p.getByRole('radio').first().check()
    await p.getByRole('radio').last().check()
    await p.getByRole('button', { name: 'Lock in my accusation' }).click()
  }
  await stage.getByRole('button', { name: 'Reveal the truth' }).click()
  await fitsBothScreens(stage, '08-accusations')
  await stage.getByRole('button', { name: 'Unmask the killer' }).click()
  await fitsBothScreens(stage, '09-unmasked')
  for (let step = 2; step <= 6; step++) await stage.getByRole('button', { name: 'Continue' }).click()
  await fitsBothScreens(stage, '10-explanation')
  await stage.getByRole('button', { name: 'Continue' }).click()
  await expect(stage.getByRole('heading', { name: "The detectives' scores" })).toBeVisible()
  await fitsBothScreens(stage, '11-finale-and-scores')
  await stage.getByRole('button', { name: 'On to the awards' }).click()
  await fitsBothScreens(stage, '12-voting')
  await stage.getByRole('button', { name: 'Close voting & announce' }).click()
  await expect(stage.getByRole('heading', { name: 'And the winners are…' })).toBeVisible()
  await fitsBothScreens(stage, '13-awards')
})
