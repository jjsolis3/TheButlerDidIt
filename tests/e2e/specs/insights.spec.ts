import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's database: a hand-written
// mystery's insights are the admin's to see.
const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })

async function joinAs(browser: Browser, code: string, name: string) {
  const page = await (await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage()
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  return page
}

test('guests rate the game on their phones, and the admin reads it on the mystery’s insights', async ({ browser }) => {
  test.setTimeout(180_000)
  const host = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByLabel('Email').fill('admin@e2e.test')
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Sign in' }).click()
  await host.waitForURL('**/host/new')
  await host.getByRole('tab', { name: '🔎 Murder mystery' }).click()
  await host.getByRole('tab', { name: /Adults/ }).click()
  await host.getByRole('button', { name: /Dinner party/ }).click()
  await host.getByLabel('Version', { exact: true }).selectOption('death-at-blackwood-manor')
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  const [ada, ben, cy] = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben'), await joinAs(browser, code, 'Cy')]
  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  for (const step of ['Play the prologue', 'Begin Act One', 'Start mingling', 'End Act 1', 'Start mingling', 'End Act 2', 'Start mingling', 'Time for accusations'])
    await host.getByRole('button', { name: step }).click()
  for (const [phone, suspect] of [[ada, /Dr\. Cornelius Finch/], [ben, /Dr\. Cornelius Finch/], [cy, /Mr\. Alistair Hargrove/]] as const) {
    await expect(phone.getByRole('heading', { name: /Who killed/ })).toBeVisible()
    await phone.getByRole('button', { name: suspect }).click()
    await phone.getByRole('radio').first().check()
    await phone.getByRole('radio').last().check()
    await phone.getByRole('button', { name: 'Lock in my accusation' }).click()
  }
  await host.getByRole('button', { name: 'Reveal the truth' }).click()

  // From the reveal on, each phone asks how it was: stars, how hard, and (an Adults story) a word or two.
  const card = ada.getByTestId('feedback-card')
  await expect(card.getByRole('heading', { name: 'How was it?' })).toBeVisible()
  await card.getByRole('radio', { name: '4 stars' }).click()
  await card.getByRole('radio', { name: 'Just right' }).click()
  await card.getByLabel(/Anything to add/).fill('The séance twist was brilliant!')
  await ada.screenshot({ path: `${SHOTS}/85-phone-feedback.png`, fullPage: true })
  await card.getByRole('button', { name: 'Send' }).click()
  await expect(card).toContainText('Thanks for rating the game ★★★★')
  const benCard = ben.getByTestId('feedback-card')
  await benCard.getByRole('radio', { name: '2 stars' }).click()
  await benCard.getByRole('radio', { name: 'Too hard' }).click()
  await benCard.getByRole('button', { name: 'Send' }).click()
  await expect(benCard).toContainText('Thanks for rating')

  // The admin's My mysteries card sums it up and links to the insights.
  await host.goto('/mysteries')
  const blackwood = host.locator('div.rounded-xl', { hasText: 'Death at Blackwood Manor' }).filter({ hasText: 'Hand-written' }).first()
  await expect(blackwood).toContainText('from 2 guests')
  await blackwood.getByRole('link', { name: '📊 Insights' }).click()
  await expect(host.getByRole('heading', { name: 'Death at Blackwood Manor' })).toBeVisible()
  await expect(host.getByTestId('insights-stats')).toContainText('★ 3')
  await expect(host.getByTestId('insights-comments')).toContainText('The séance twist was brilliant!')
  // Who they accused, on the version they played. (Other specs play Blackwood as written too, so the totals
  // include their games: the API tests check the exact numbers.)
  const asWritten = host.getByTestId('version-insights').filter({ hasText: 'As written' })
  await expect(asWritten).toContainText('Dr. Cornelius Finch (the killer)')
  await expect(asWritten).toContainText(/\d+ games? · \d+% named the killer/)
  await host.screenshot({ path: `${SHOTS}/86-insights.png`, fullPage: true })
  await host.setViewportSize({ width: 390, height: 844 })
  await host.screenshot({ path: `${SHOTS}/87-insights-phone.png`, fullPage: true })
})
