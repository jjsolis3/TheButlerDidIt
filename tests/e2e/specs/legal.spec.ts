import { expect, test, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// Terms, privacy and refunds (#103). This server runs with none of the owner's details set and the starter drafts, as a
// fresh site would. Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account.
const SHOTS = 'screenshots/legal'
mkdirSync(SHOTS, { recursive: true })

const PHONE = { width: 390, height: 844 }

async function noSidewaysScroll(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0)
}

test('anyone can read the terms, privacy and refund pages from the foot of every page, on a phone', async ({ browser }) => {
  const page = await (await browser.newContext({ viewport: PHONE })).newPage()
  await page.goto('/how-to-play')
  const footer = page.getByRole('navigation', { name: 'Site policies' })
  await footer.getByRole('link', { name: 'Privacy' }).click()

  await expect(page.getByRole('heading', { level: 1, name: 'Privacy Policy' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Costume selfies' })).toBeVisible()
  // The owner's notes never reach the page, and what isn't set yet says so.
  await expect(page.getByText('Notes for the site')).toHaveCount(0)
  await expect(page.getByText('[contact email not set yet]').first()).toBeVisible()
  // The list of services comes from the site's settings: here, no email, no payments by Stripe and no AI company.
  await expect(page.getByRole('listitem').filter({ hasText: 'Our hosting provider runs the server' })).toBeVisible()
  await noSidewaysScroll(page)
  await page.screenshot({ path: `${SHOTS}/140-privacy-phone.png` })

  // The terms link to a section of the privacy policy, and the link lands on it.
  await footer.getByRole('link', { name: 'Terms' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Terms of Service' })).toBeVisible()
  await expect(page.getByText('New hosts get 14 days free.')).toBeVisible()
  await page.locator('a[href="/privacy#children"]').click()
  await expect(page).toHaveURL(/\/privacy#children$/)
  await expect(page.getByRole('heading', { name: 'Children' })).toBeInViewport()

  await footer.getByRole('link', { name: 'Refunds' }).click()
  await expect(page.getByRole('heading', { name: 'A full refund within 14 days, if unused' })).toBeVisible()
  await noSidewaysScroll(page)
})

test('signing up says what you agree to, and the admin is told the pages are still drafts', async ({ browser }) => {
  const page = await (await browser.newContext({ viewport: PHONE })).newPage()
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  const notice = page.getByTestId('signup-terms')
  await expect(notice).toHaveText('By creating an account, you agree to our Terms of Service and Privacy Policy. You must be 18 or older and live in the United States.')
  await expect(notice.getByRole('link', { name: 'Terms of Service' })).toHaveAttribute('href', '/terms')
  await noSidewaysScroll(page)
  await page.screenshot({ path: `${SHOTS}/141-signup-phone.png` })

  // A guest or a host sees the page as it is; only the admin sees the reminder.
  await page.goto('/terms')
  await expect(page.getByRole('heading', { level: 1, name: 'Terms of Service' })).toBeVisible()
  await expect(page.getByTestId('legal-draft')).toHaveCount(0)

  const admin = await (await browser.newContext()).newPage()
  await admin.goto('/login')
  await admin.getByLabel('Email').fill('admin@e2e.test')
  await admin.getByLabel('Password').fill('password123')
  await admin.getByRole('button', { name: 'Sign in' }).click()
  await admin.waitForURL('**/host/new')
  await admin.goto('/terms')
  await expect(admin.getByTestId('legal-draft')).toContainText('this page is still the starter draft')
  await admin.goto('/admin')
  await expect(admin.getByTestId('server-checks')).toContainText('Set Legal__OperatorName, Legal__ContactEmail and Legal__State')
  await expect(admin.getByTestId('server-checks')).toContainText('still the starter draft')
})
