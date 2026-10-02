import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS } from './escape-play'

// My escape rooms, and how the admin improves a built-in room for everyone: copy it, edit the copy, share the copy
// with every host, and take the original off the shelf. Another host then finds only the improved room, and hosts it.
// Uses The Bunker, which no other spec plays, and puts everything back at the end.

mkdirSync(SHOTS, { recursive: true })

/** The run's admin (ai.spec.ts makes it first); run on its own, this spec makes it, as the first account. */
async function signInAsAdmin(browser: Browser): Promise<Page> {
  const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  page.on('dialog', (d) => d.accept())
  await page.goto('/login')
  await page.getByLabel('Email').fill('admin@e2e.test')
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  try {
    await page.waitForURL('**/host/new', { timeout: 5_000 })
  } catch {
    await page.getByRole('button', { name: 'Create an account' }).click()
    await page.getByLabel('Your name').fill('Admin Agatha')
    await page.getByLabel('Email').fill('admin@e2e.test')
    await page.getByLabel('Password').fill('password123')
    await page.getByRole('button', { name: 'Create account' }).click()
    await page.waitForURL('**/host/new')
  }
  return page
}

const row = (page: Page, title: string) => page.locator('div.rounded-xl', { has: page.getByText(title, { exact: true }) })

test('the admin improves a built-in escape room for every host', async ({ browser }) => {
  test.setTimeout(150_000)
  const admin = await signInAsAdmin(browser)

  // ---- My escape rooms is in the account menu.
  await admin.locator('button[aria-controls="account-menu"]').click()
  await admin.getByRole('link', { name: 'My escape rooms' }).click()
  await admin.waitForURL('**/escape/rooms')
  await expect(admin.getByRole('heading', { name: 'My escape rooms' })).toBeVisible()
  await expect(admin.getByText('As the admin:')).toBeVisible()

  // ---- Copy The Bunker and improve the copy.
  await row(admin, 'The Bunker').getByRole('button', { name: '📄 Make my own copy' }).click()
  await admin.waitForURL(/\/escape\/rooms\/the-bunker-[0-9a-f]{6}$/)
  await admin.getByRole('button', { name: 'Show me everything' }).click()
  await expect(admin.getByTestId('room-check')).toContainText('✓ Ready to play', { timeout: 30_000 })
  await admin.getByLabel('Title').fill('The Bunker (revised)')
  await expect(admin.getByTestId('room-check')).toContainText('✓ Ready to play')
  await admin.getByRole('button', { name: 'Save' }).click()
  await expect(admin.getByRole('status')).toHaveText('Saved ✓')

  // ---- Share it, and take the original off the shelf.
  await admin.goto('/escape/rooms')
  const revised = row(admin, 'The Bunker (revised)')
  await expect(revised).toContainText('📄 Your copy')
  await revised.getByRole('button', { name: '🌍 Share with every host' }).click()
  await expect(revised).toContainText('shared with every host')
  const original = row(admin, 'The Bunker')
  await original.getByRole('button', { name: 'Take off the shelf' }).click()
  await expect(original).toContainText('🙈 off the shelf')
  await expect(original.getByRole('link', { name: 'Host it' })).toHaveCount(0)
  await admin.screenshot({ path: `${SHOTS}/120-my-escape-rooms-admin.png`, fullPage: true })

  // ---- Another host's shelf has the improved room and not the original; they host it.
  const host = await (await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Bunker Fan')
  await host.getByLabel('Email').fill(`bunker-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')
  await host.getByRole('tab', { name: /Escape room/ }).click()
  const card = host.getByRole('button', { name: /The Bunker \(revised\)/ })
  await expect(card).toBeVisible()
  await expect(host.getByRole('button', { name: /The Bunker(?! \(revised\))/ })).toHaveCount(0)
  await expect(host.getByRole('button', { name: /The Workshop/ })).toBeVisible() // the rest of the shelf is as it was

  await host.goto('/escape/rooms')
  const shared = row(host, 'The Bunker (revised)')
  await expect(shared).toContainText('🌍 Shared by the admin')
  await expect(shared.getByRole('button', { name: '🌍 Share with every host' })).toHaveCount(0)
  await expect(host.getByText('The Bunker', { exact: true })).toHaveCount(0)
  await expect(host.getByText('As the admin:')).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/121-my-escape-rooms-phone.png`, fullPage: true })
  await shared.getByRole('link', { name: 'Host it' }).click()
  await host.waitForURL(/\/host\/new\?game=escape&room=the-bunker-[0-9a-f]{6}$/)
  await expect(host.getByRole('button', { name: /The Bunker \(revised\)/ })).toHaveAttribute('aria-pressed', 'true')
  await host.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(host.getByRole('heading', { name: 'The Bunker (revised)' })).toBeVisible()

  // ---- Put everything back for the other specs.
  await admin.goto('/escape/rooms')
  await row(admin, 'The Bunker').getByRole('button', { name: 'Put back on the shelf' }).click()
  await expect(row(admin, 'The Bunker').getByRole('button', { name: 'Take off the shelf' })).toBeVisible()
  await expect(row(admin, 'The Bunker')).not.toContainText('🙈')
  await row(admin, 'The Bunker (revised)').getByRole('button', { name: 'Stop sharing' }).click()
  await expect(row(admin, 'The Bunker (revised)').getByRole('button', { name: '🌍 Share with every host' })).toBeVisible()
})
