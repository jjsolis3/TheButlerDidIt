import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// Payments (#101), through the fake provider (playwright.config.ts): its "checkout" and "billing" pages stand in for
// Stripe's and send the webhooks Stripe would. Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account.
const SHOTS = 'screenshots/billing'
mkdirSync(SHOTS, { recursive: true })

const PHONE = { width: 390, height: 844 }

async function signUp(browser: Browser, name: string) {
  const page = await (await browser.newContext({ viewport: PHONE })).newPage()
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Email').fill(`${name.split(' ')[0].toLowerCase()}-${Date.now()}@e2e.test`)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await page.waitForURL('**/host/new')
  return page
}

async function signIn(browser: Browser, email: string, viewport = { width: 1280, height: 900 }) {
  const page = await (await browser.newContext({ viewport })).newPage()
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL('**/host/new')
  return page
}

async function noSidewaysScroll(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0)
}

test('a host on the free trial subscribes, pays on the checkout page, and cancels on the billing page', async ({ browser }) => {
  const host = await signUp(browser, 'Paying Penny')
  await host.goto('/account')
  const plans = host.getByRole('region', { name: 'Plans' })
  await expect(plans).toBeVisible()
  // Monthly first, each game and both, at the prices Stripe has (the fake's: $8 and $12).
  await expect(plans.getByRole('radio', { name: 'Monthly' })).toHaveAttribute('aria-checked', 'true')
  await expect(plans.getByRole('button', { name: 'Choose Both games, $12.00 a month' })).toBeVisible()
  await expect(plans.getByRole('button', { name: 'Choose Murder mysteries, $8.00 a month' })).toBeVisible()
  await expect(plans.getByText('Subscribe now and your first payment waits until your free trial ends.')).toBeVisible()
  // A pass would add nothing during the trial, so none is offered.
  await expect(plans.getByText('Party pass')).toHaveCount(0)
  await plans.getByRole('radio', { name: 'Yearly' }).click()
  await expect(plans.getByRole('button', { name: 'Choose Both games, $120.00 a year' })).toBeVisible()
  await expect(plans.getByRole('button', { name: /Murder mysteries.*a year/ })).toHaveCount(0) // that price is set up wrong: not for sale
  await noSidewaysScroll(host)
  await plans.screenshot({ path: `${SHOTS}/130-plans-phone.png` })

  await plans.getByRole('radio', { name: 'Monthly' }).click()
  await plans.getByRole('button', { name: 'Choose Both games, $12.00 a month' }).click()

  // The (fake) payment page, then back to the account page, which checks with the provider.
  await expect(host.getByRole('heading', { name: 'Fake checkout' })).toBeVisible()
  await expect(host.getByText(/Free until/)).toBeVisible() // the trial carried over: nothing to pay today
  await host.getByRole('button', { name: 'Pay' }).click()
  await host.waitForURL('**/account')
  await expect(host.getByTestId('billing-return')).toHaveText('Payment confirmed. Thank you, and enjoy the games!')
  await expect(host.getByText('Subscription', { exact: true })).toBeVisible()
  await expect(host.getByText(/^Free until .+, then it renews\.$/)).toBeVisible()
  await expect(plans.getByRole('button', { name: 'Manage billing' })).toBeVisible()
  await expect(plans.getByRole('button', { name: /^Choose / })).toHaveCount(0) // plan changes are made on the billing page
  await host.screenshot({ path: `${SHOTS}/131-subscribed-phone.png`, fullPage: true })

  // Cancelling on the (fake) billing page: the games stay until the paid period ends.
  await plans.getByRole('button', { name: 'Manage billing' }).click()
  await expect(host.getByRole('heading', { name: 'Fake billing' })).toBeVisible()
  await host.getByRole('button', { name: 'Cancel subscription' }).click()
  await expect(host.getByText(/trialing, ends/)).toBeVisible()
  await host.getByRole('link', { name: 'Back to the site' }).click()
  await host.waitForURL('**/account')
  await expect(host.getByText(/^Cancelled: your games stay until .+\.$/)).toBeVisible()
  await expect(host.getByRole('list', { name: 'Games in your plan' })).toContainText('✓ 🔐 Escape rooms')

  // The admin's tab: the setup, the plan set up wrong, and Penny's subscription.
  const admin = await signIn(browser, 'admin@e2e.test')
  await admin.goto('/admin/billing')
  await expect(admin.getByRole('heading', { name: 'Plans & billing' })).toBeVisible()
  await expect(admin.getByTestId('billing-checks')).toContainText('through the fake provider')
  await expect(admin.getByTestId('billing-checks')).toContainText('customer.subscription.updated')
  await expect(admin.getByTestId('billing-plans')).toContainText('⚠ Not for sale: This is a one-time price, but a subscription repeats.')
  const row = admin.getByTestId('billing-paid').getByRole('row').filter({ hasText: 'Paying Penny' })
  await expect(row).toContainText('Subscription: Both games')
  await expect(row).toContainText('Cancelled, paid until the end')
  await row.getByRole('button', { name: "Check Paying Penny's payments with Stripe" }).click()
  await expect(row.getByRole('button', { name: /Check Paying Penny/ })).toHaveText('Sync')
  await admin.screenshot({ path: `${SHOTS}/132-admin-billing.png`, fullPage: true })

  // The overview's checklist mentions payments too.
  await admin.goto('/admin')
  await expect(admin.getByTestId('server-checks')).toContainText('Payments through the fake provider')

  const phone = await signIn(browser, 'admin@e2e.test', PHONE)
  await phone.goto('/admin/billing')
  await expect(phone.getByTestId('billing-paid')).toBeVisible()
  await noSidewaysScroll(phone)
})
