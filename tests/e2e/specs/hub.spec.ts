import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The admin hub (#102). Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's
// database, and after hosts.spec.ts, so there are other hosts to count.
const SHOTS = 'screenshots/admin-hub'
mkdirSync(SHOTS, { recursive: true })

async function signIn(browser: Browser, email: string, viewport = { width: 1440, height: 900 }) {
  const page = await (await browser.newContext({ viewport })).newPage()
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL('**/host/new')
  return page
}

/** Nothing on an admin page may push the page itself sideways on a phone; a wide table scrolls in its own box. */
async function noSidewaysScroll(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0)
}

/** A new host, on the free trial like every new host. */
async function signUp(browser: Browser, name: string) {
  const page = await (await browser.newContext()).newPage()
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Email').fill(`${name.split(' ')[0].toLowerCase()}-${Date.now()}@e2e.test`)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await page.waitForURL('**/host/new')
  return page
}

test('the admin runs the site from one hub: overview, games, hosts, sign-ups and AI', async ({ browser }) => {
  await signUp(browser, 'Trial Theo')
  const admin = await signIn(browser, 'admin@e2e.test')

  // ---- From the account menu to the overview.
  await admin.getByRole('button', { name: /account menu/ }).click()
  await admin.getByRole('link', { name: 'Admin hub' }).click()
  await admin.waitForURL('**/admin')
  await expect(admin.getByRole('heading', { name: 'Overview' })).toBeVisible()
  const stats = admin.getByTestId('overview-stats')
  await expect(stats.getByText('Hosts', { exact: true })).toBeVisible()
  await expect(stats.getByText('Parties this week')).toBeVisible()
  await expect(admin.getByTestId('weekly-games').locator('li')).toHaveCount(8)
  await expect(admin.getByTestId('plans')).toContainText('Free trial') // Theo
  // This e2e server has no email, and its AI is the fake provider: the checklist says both.
  await expect(admin.getByTestId('server-checks')).toContainText("isn't set up")
  await expect(admin.getByTestId('server-checks')).toContainText('Storyteller')
  await expect(admin.getByTestId('signups-mode')).toContainText('Anyone can sign up')
  await admin.screenshot({ path: `${SHOTS}/01-overview.png`, fullPage: true })

  // ---- Games: every mystery and room, and the way into one's insights and back.
  await admin.getByRole('navigation', { name: 'Admin' }).getByRole('link', { name: 'Games' }).click()
  await expect(admin.getByRole('heading', { name: 'Games', exact: true })).toBeVisible()
  const blackwood = admin.getByTestId('admin-game').filter({ hasText: 'Death at Blackwood Manor' })
  await expect(blackwood).toContainText('Built in')
  await expect(admin.getByTestId('admin-game').filter({ hasText: 'The Workshop' })).toBeVisible()
  await admin.getByRole('button', { name: '🔐 Escape rooms' }).click()
  await expect(admin.getByTestId('admin-game').filter({ hasText: 'Death at Blackwood Manor' })).toHaveCount(0)
  await admin.getByRole('button', { name: 'All', exact: true }).click()
  await admin.getByLabel('Find a game or host').fill('blackwood')
  await expect(admin.getByTestId('admin-game')).toHaveCount(1)
  await admin.screenshot({ path: `${SHOTS}/02-games.png`, fullPage: true })
  await blackwood.getByRole('link', { name: 'Insights for Death at Blackwood Manor' }).click()
  await expect(admin.getByRole('heading', { name: 'Death at Blackwood Manor' })).toBeVisible()
  await admin.getByRole('link', { name: '← Admin · Games' }).click()
  await expect(admin.getByRole('heading', { name: 'Games', exact: true })).toBeVisible()

  // ---- Hosts: find one by name or email.
  await admin.getByRole('navigation', { name: 'Admin' }).getByRole('link', { name: 'Hosts' }).click()
  await admin.getByLabel('Find a host').fill('admin@e2e.test')
  await expect(admin.getByText('1 of')).toBeVisible()
  await expect(admin.locator('div.rounded-xl', { hasText: 'admin@e2e.test' })).toContainText(/Joined (today|yesterday)/)
  await admin.screenshot({ path: `${SHOTS}/03-hosts.png`, fullPage: true })

  // ---- AI keeps its page, now a tab.
  await admin.getByRole('navigation', { name: 'Admin' }).getByRole('link', { name: 'AI' }).click()
  await expect(admin.getByRole('heading', { name: 'AI game master' })).toBeVisible()
})

test('the admin closes sign-ups to invites without a redeploy, and opens them again', async ({ browser }) => {
  const admin = await signIn(browser, 'admin@e2e.test')
  const visitor = await (await browser.newContext()).newPage()
  try {
    await admin.goto('/admin/signups')
    await expect(admin.getByRole('heading', { name: 'Sign-ups' })).toBeVisible()
    await expect(admin.getByRole('radio', { name: /Anyone/ })).toHaveAttribute('aria-checked', 'true')
    await expect(admin.getByTestId('signups-source')).toContainText("the server's setting")

    await admin.getByRole('radio', { name: /Invites only/ }).click()
    await expect(admin.getByRole('radio', { name: /Invites only/ })).toHaveAttribute('aria-checked', 'true')
    await expect(admin.getByText('Sign-ups are invite-only.')).toBeVisible() // the invites panel follows the switch
    await admin.screenshot({ path: `${SHOTS}/04-signups-invites-only.png`, fullPage: true })

    // A visitor no longer gets the sign-up form.
    await visitor.goto('/login')
    await expect(visitor.getByText('New host accounts are by invitation.')).toBeVisible()
    await expect(visitor.getByRole('button', { name: 'Create an account' })).toHaveCount(0)

    // Handing the choice back to the server's setting (open, on this server) brings the form back.
    await admin.getByRole('button', { name: "Use the server's setting" }).click()
    await expect(admin.getByRole('radio', { name: /Anyone/ })).toHaveAttribute('aria-checked', 'true')
    await visitor.reload()
    await expect(visitor.getByRole('button', { name: 'Create an account' })).toBeVisible()
  } finally {
    // Later specs sign up new hosts, so never leave the switch closed, even when a step above failed.
    await admin.request.put('/api/admin/signups', { data: { open: null } })
  }
})

test('the hub fits a phone, and a host who is not the admin is kept out', async ({ browser }) => {
  const admin = await signIn(browser, 'admin@e2e.test', { width: 390, height: 844 })
  for (const [path, heading, shot] of [
    ['/admin', 'Overview', '05-phone-overview'],
    ['/admin/games', 'Games', '06-phone-games'],
    ['/admin/signups', 'Sign-ups', '07-phone-signups'],
  ] as const) {
    await admin.goto(path)
    await expect(admin.getByRole('heading', { name: heading, exact: true })).toBeVisible()
    await noSidewaysScroll(admin)
    await admin.screenshot({ path: `${SHOTS}/${shot}.png`, fullPage: true })
  }

  const host = await signUp(browser, 'Nosy Nora')
  await host.getByRole('button', { name: /account menu/ }).click()
  await expect(host.getByRole('link', { name: 'Admin hub' })).toHaveCount(0)
  await host.goto('/admin/games')
  await expect(host.getByText('Only the admin can open the admin hub.')).toBeVisible()
  await expect(host.getByTestId('admin-games')).toHaveCount(0)
})
