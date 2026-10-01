import { expect, test } from '@playwright/test'

// Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's database.
// This server has no email set up, so a forgetful host gets a reset link from the admin, and a new
// host gets an invite link the same way.

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

test('a new host signs up with a one-time invite link from the admin', async ({ browser }) => {
  // ---- The admin makes an invite that only Ivy's address can use.
  const admin = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await admin.goto('/login')
  await admin.getByLabel('Email').fill('admin@e2e.test')
  await admin.getByLabel('Password').fill('password123')
  await admin.getByRole('button', { name: 'Sign in' }).click()
  await admin.waitForURL('**/host/new')
  await admin.goto('/admin/hosts')
  await admin.getByLabel("Who's it for?").fill('Ivy, my cousin')
  await admin.getByLabel('Their email (optional)').fill('ivy@e2e.test')
  await admin.getByRole('button', { name: 'Make an invite link' }).click()
  const link = await admin.getByLabel('New invite link').inputValue()
  expect(link).toMatch(/\/login\?invite=[\w-]{43}$/)
  await expect(admin.getByText('Ivy, my cousin · ivy@e2e.test')).toBeVisible()
  await admin.screenshot({ path: 'screenshots/41-admin-invites.png', fullPage: true })

  // ---- Ivy opens it: the sign-up form, with her address filled in and fixed.
  const ivy = await (await browser.newContext()).newPage()
  await ivy.goto(link)
  await expect(ivy.getByRole('heading', { name: 'Create a host account' })).toBeVisible()
  await expect(ivy.getByText('Admin Agatha invited you to host on The Butler Did It.')).toBeVisible()
  await expect(ivy.getByLabel('Email')).toHaveValue('ivy@e2e.test')
  await expect(ivy.getByLabel('Email')).not.toBeEditable()
  await ivy.getByLabel('Your name').fill('Invited Ivy')
  await ivy.getByLabel('Password').fill('password123')
  await ivy.screenshot({ path: 'screenshots/42-invited-sign-up.png', fullPage: true })
  await ivy.getByRole('button', { name: 'Create account' }).click()
  await ivy.waitForURL('**/host/new')

  // ---- The link is used up: anyone opening it again is told so, and sent to sign in.
  const stranger = await (await browser.newContext()).newPage()
  await stranger.goto(link)
  await expect(stranger.getByText('This invite has already been used. If it was you, sign in instead.')).toBeVisible()
  await expect(stranger.getByRole('heading', { name: 'Welcome back, host' })).toBeVisible()

  // ---- The admin sees who used it.
  await admin.reload()
  await expect(admin.getByText(/Used by Invited Ivy/)).toBeVisible()
})
