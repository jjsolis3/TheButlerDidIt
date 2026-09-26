import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The two catalogs: Adults (Mature) and Family, each with hand-written mysteries.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

async function joinAs(browser: Browser, code: string, name: string, character: RegExp) {
  const page = await (await browser.newContext(phone)).newPage()
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await page.getByRole('button', { name: character }).click()
  await expect(page.getByText('You will play')).toBeVisible()
  return page
}

test('Family and Adult catalogs: a family pirate party from start to the first clues', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Captain Mum')
  await host.getByLabel('Email').fill(`family-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  // Adults shelf first: the grown-up mysteries.
  await expect(host.getByRole('tab', { name: /Adults/ })).toHaveAttribute('aria-selected', 'true')
  await expect(host.getByText('Death at Blackwood Manor')).toBeVisible()
  await expect(host.getByText('Death Among the Vines')).toBeVisible()
  await expect(host.getByText("The Captain's Last Cocoa")).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/70-adults-shelf.png`, fullPage: true })

  // Family shelf: only family mysteries, the Family level follows, and no drinking games.
  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByText("The Captain's Last Cocoa")).toBeVisible()
  await expect(host.getByText('Death at Blackwood Manor')).toHaveCount(0)
  await expect(host.getByLabel(/Toast prompts/)).toHaveCount(0)
  // No separate content-level step: the shelf is the level. The tone (offered when the AI game
  // master is set up) only has Family choices here, never "Mature".
  await expect(host.getByText('Content level')).toHaveCount(0)
  await expect(host.getByRole('radio', { name: /Mature/ })).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/71-family-shelf.png`, fullPage: true })

  await host.getByLabel('Version').selectOption('the-captains-last-cocoa') // Version A: this test knows the killer
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  const jo = await joinAs(browser, code, 'Jo', /Quartermaster Morgan Flint/)
  await joinAs(browser, code, 'Sam', /First Mate Grace O'Hara/)
  await joinAs(browser, code, 'Kit', /Cookie Pottage/)

  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(host.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  await expect(jo.getByText('You are the murderer.', { exact: true })).toBeVisible() // the quartermaster did it
  await host.screenshot({ path: `${SHOTS}/72-family-cast.png` })

  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await host.getByRole('button', { name: 'Begin Act One' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await expect(host.getByText('The Empty Map Case')).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/73-family-mingle.png` })

  // Same story again: "Surprise me" deals a version this host hasn't played, so a new killer.
  await host.goto('/host/new')
  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByText('3 versions, a different killer each')).toBeVisible()
  await expect(host.getByLabel('Version')).toHaveValue('surprise')
  await expect(host.getByLabel('Version').locator('option', { hasText: 'Version A (played)' })).toHaveCount(1)
  await host.screenshot({ path: `${SHOTS}/74-version-picker.png`, fullPage: true })
  const again = await host.request.post('/api/parties', {
    data: { scenarioId: 'the-captains-last-cocoa', mode: 'sharedScreen', scheduledFor: null, useAi: false, version: 'surprise' },
  })
  expect(again.ok()).toBeTruthy()
  const second = await again.json()
  expect(second.scenarioId).toMatch(/^the-captains-last-cocoa--[bc]$/)
  expect(second.contentLevel).toBe('family') // the level comes from the mystery itself

  // Tidying up: the host deletes the party that never started, from their list on the home page.
  await host.goto('/')
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) })).toBeVisible()
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${code}\\)`) })).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/75-your-parties.png`, fullPage: true })
  host.once('dialog', (d) => d.accept())
  await host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) }).click()
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) })).toHaveCount(0)
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${code}\\)`) })).toBeVisible()
  expect((await host.request.get(`/api/parties/${second.code}`)).status()).toBe(404)
})
