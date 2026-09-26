import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The host remote: run the evening from a phone while the TV shows only the show.

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

test('the host runs the evening from a phone remote while the TV shows only the show', async ({ browser }) => {
  const email = `remote-${Date.now()}@example.com`
  const tv = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage()
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Remote Rita')
  await tv.getByLabel('Email').fill(email)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByLabel('Version', { exact: true }).selectOption('death-at-blackwood-manor')
  await tv.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  // On the TV: unlock sound now, show the remote's QR code, then hide the controls.
  await tv.getByRole('button', { name: '🔊 Enable sound on this screen' }).click()
  await tv.getByRole('button', { name: '📱 Use my phone as a remote' }).click()
  await expect(tv.getByRole('dialog', { name: 'Phone remote' })).toContainText(`/remote/${code}`)
  await tv.screenshot({ path: `${SHOTS}/80-remote-qr.png` })
  await tv.getByRole('button', { name: 'Hide the controls on this screen' }).click()
  await expect(tv.getByRole('button', { name: 'Begin the evening' })).toHaveCount(0)
  await expect(tv.getByRole('button', { name: 'Show host controls' })).toBeVisible()

  // On the phone: the remote asks the host to sign in, then comes straight back.
  const remote = await (await browser.newContext(phone)).newPage()
  await remote.goto(`/remote/${code}`)
  await remote.waitForURL(/\/login\?next=/)
  await remote.getByLabel('Email').fill(email)
  await remote.getByLabel('Password').fill('password123')
  await remote.getByRole('button', { name: 'Sign in' }).click()
  await remote.waitForURL(`**/remote/${code}`)
  await expect(remote.getByText('📱 Host remote')).toBeVisible()

  await joinAs(browser, code, 'Ada', /Dr\. Cornelius Finch/)
  await joinAs(browser, code, 'Ben', /Hargrove/)
  await joinAs(browser, code, 'Cy', /Lady Evelyn/)
  await expect(remote.getByText('Ada as Dr. Cornelius Finch')).toBeVisible()
  await remote.screenshot({ path: `${SHOTS}/81-remote-lobby.png`, fullPage: true })

  // Everything from here is pressed on the phone; the TV is never touched again.
  await remote.getByRole('button', { name: 'Begin the evening' }).click()
  await expect(tv.getByRole('heading', { name: 'The suspects' })).toBeVisible() // no "Tap to begin": sound was unlocked in the lobby
  await remote.getByRole('button', { name: 'Ben', exact: true }).click()
  await expect(tv.getByRole('status').filter({ hasText: 'In the spotlight' })).toContainText('Ben as Mr. Alistair Hargrove')

  await remote.getByRole('button', { name: 'Play the prologue' }).click()
  await remote.getByRole('button', { name: 'Begin Act One' }).click()
  await remote.getByRole('button', { name: 'Start mingling' }).click()
  await expect(remote.getByRole('button', { name: /Drop next clue/ })).toBeVisible()
  await expect(remote.getByText('What now?')).toBeVisible()
  await remote.screenshot({ path: `${SHOTS}/82-remote-mingle.png`, fullPage: true })
  await remote.getByRole('button', { name: '⏸ Pause' }).click()
  await expect(remote.getByRole('button', { name: '▶ Resume' })).toBeVisible()
  await expect(tv.getByRole('button', { name: 'Show host controls' })).toBeVisible() // still hidden on the TV
  await tv.screenshot({ path: `${SHOTS}/83-tv-without-controls.png` })

  // Another host can't use this party's remote.
  const stranger = await (await browser.newContext(phone)).newPage()
  await stranger.goto('/login')
  await stranger.getByRole('button', { name: 'Create an account' }).click()
  await stranger.getByLabel('Your name').fill('Nosy Ned')
  await stranger.getByLabel('Email').fill(`nosy-${Date.now()}@example.com`)
  await stranger.getByLabel('Password').fill('password123')
  await stranger.getByRole('button', { name: 'Create account' }).click()
  await stranger.waitForURL('**/host/new')
  await stranger.goto(`/remote/${code}`)
  await expect(stranger.getByText(`Only the host of party ${code} can use its remote.`)).toBeVisible()
})
