import { expect, test } from '@playwright/test'

// Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's database.
// This server has no email set up, so a forgetful host gets a reset link from the admin.

test('a host who forgot their password gets back in with a link from the admin', async ({ browser }) => {
  // ---- A host signs up, then forgets their password.
  const host = await (await browser.newContext()).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Forgetful Fred')
  await host.getByLabel('Email').fill('fred@e2e.test')
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')
  await host.context().clearCookies()

  await host.goto('/login')
  await expect(host.getByText('Forgot your password? Ask the admin for a reset link.')).toBeVisible()

  // ---- The admin makes a one-time link on the Hosts page.
  const admin = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await admin.goto('/login')
  await admin.getByLabel('Email').fill('admin@e2e.test')
  await admin.getByLabel('Password').fill('password123')
  await admin.getByRole('button', { name: 'Sign in' }).click()
  await admin.waitForURL('**/host/new')
  await admin.goto('/admin/hosts')
  const fred = admin.locator('div.rounded-xl', { hasText: 'fred@e2e.test' })
  await fred.getByRole('button', { name: 'Make a reset link' }).click()
  const link = await admin.getByLabel('Reset link for Forgetful Fred').inputValue()
  await admin.screenshot({ path: 'screenshots/40-admin-hosts.png', fullPage: true })

  // ---- Fred opens it, chooses a new password and is signed straight in.
  await host.goto(link)
  await expect(host.getByRole('heading', { name: 'Choose a new password' })).toBeVisible()
  await host.getByLabel('New password').fill('remembered-now1')
  await host.getByRole('button', { name: 'Save and sign in' }).click()
  await host.waitForURL('**/host/new')
  await expect(host.getByRole('heading', { name: 'Set the scene' })).toBeVisible()

  // The link only works once.
  await host.goto(link)
  await host.getByLabel('New password').fill('another-try1')
  await host.getByRole('button', { name: 'Save and sign in' }).click()
  await expect(host.getByText(/invalid or has expired/)).toBeVisible()
})
