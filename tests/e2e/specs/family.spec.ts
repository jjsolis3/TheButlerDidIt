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
  await host.screenshot({ path: `${SHOTS}/71-family-shelf.png`, fullPage: true })

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
})
