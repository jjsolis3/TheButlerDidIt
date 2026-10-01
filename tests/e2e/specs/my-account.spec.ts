import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The host's own account: the header menu on every page, and /account for their plan, usage,
// details, devices and data. On a phone, where hosts will mostly use it.
//
// Named to run after ai.spec.ts, whose admin@e2e.test must be the run's first account (the admin).

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

test('a host renames themselves, changes their password, downloads their data and deletes their account', async ({ browser }) => {
  const page = await (await browser.newContext(phone)).newPage()
  const email = `account-${Date.now()}@example.com`
  // Someone else signs up first, so this host is never the site's first account (the admin, who can't
  // delete themselves), even when this spec runs on its own against a fresh database.
  const first = await (await browser.newContext()).request.post('/api/auth/register', {
    data: { email: `first-${Date.now()}@example.com`, password: 'password123', displayName: 'First Host' },
  })
  expect(first.ok()).toBeTruthy()

  // ---- Sign up; the header shows who's signed in.
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Morgan Host')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await page.waitForURL('**/host/new')

  // ---- The account menu, on any page.
  const menu = page.getByRole('button', { name: 'Morgan Host: account menu' })
  await menu.click()
  await expect(page.getByRole('navigation', { name: 'Account' })).toBeVisible()
  await page.screenshot({ path: `${SHOTS}/95-account-menu.png` })
  await page.getByRole('link', { name: 'Your account' }).click()
  await page.waitForURL('**/account')
  await expect(page.getByRole('heading', { name: 'Morgan Host' })).toBeVisible()
  await expect(page.getByText('Free trial', { exact: true })).toBeVisible() // a new host starts on the free trial
  await expect(page.getByText('0 mysteries and 0 escape rooms hosted')).toBeVisible()
  await page.screenshot({ path: `${SHOTS}/96-account-page.png`, fullPage: true })

  // ---- A new name shows on the page and in the header.
  await page.getByLabel('Your name').fill('Morgan the Magnificent')
  await page.getByRole('button', { name: 'Save name' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Morgan the Magnificent' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Morgan the Magnificent: account menu' })).toBeVisible()

  // ---- A new password: the wrong current one is refused, then it works.
  await page.getByLabel('Current password', { exact: true }).fill('not-my-password')
  await page.getByLabel('New password').fill('a-better-pass1')
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText("That isn't your current password.")).toBeVisible()
  await page.getByLabel('Current password', { exact: true }).fill('password123')
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText(/Password changed/)).toBeVisible()

  // ---- Download my data: a JSON file of the account.
  const exported = await page.request.get('/api/account/export')
  expect(exported.ok()).toBeTruthy()
  expect((await exported.json()).account.email).toBe(email)
  await expect(page.getByRole('link', { name: 'Download my data (JSON)' })).toHaveAttribute('href', '/api/account/export')

  // ---- Delete: needs the password and a tick, then signs out and goes home.
  const remove = page.getByRole('button', { name: 'Delete my account' })
  await expect(remove).toBeDisabled()
  await page.getByLabel('Your password').fill('a-better-pass1')
  await page.getByLabel("I understand this can't be undone.").check()
  page.once('dialog', (d) => d.accept())
  await remove.click()
  await page.waitForURL((url) => url.pathname === '/')
  await expect(page.getByRole('link', { name: 'Host sign in' })).toBeVisible()

  // The account is gone.
  const login = await page.request.post('/api/auth/login', { data: { email, password: 'a-better-pass1' } })
  expect(login.status()).toBe(401)
})

test('a visitor sees "Host sign in" in the header, and /account asks them to sign in', async ({ page }) => {
  await page.goto('/escape')
  await expect(page.getByRole('link', { name: 'Host sign in' })).toBeVisible()
  await page.goto('/account')
  await page.waitForURL(/\/login\?next=%2Faccount/)
})
