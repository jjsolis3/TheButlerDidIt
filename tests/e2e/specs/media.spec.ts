import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// Phase 3: prepared voices and pictures, the printable kit, costume selfies and toast cues.
// The server's Voice and Illustrator roles use the Fake provider (see playwright.config.ts).

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })

const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

// A 1×1 red PNG: the smallest real image the server will accept as a selfie.
const TINY_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==',
  'base64',
)

test('media: prepared portraits, printable kit, costume selfies and toast prompts', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  host.on('dialog', (d) => d.accept())

  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Media Mabel')
  await host.getByLabel('Email').fill(`media-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  // ---- Toast prompts are an adults-only option: hidden for Family, offered for Mature.
  await host.getByRole('button', { name: /^Family/ }).click()
  await expect(host.getByLabel(/Toast prompts/)).toHaveCount(0)
  await host.getByRole('button', { name: /^Mature/ }).click()
  await host.getByRole('button', { name: /Death at Blackwood Manor/ }).click()
  await host.getByLabel(/Toast prompts/).check()
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  // ---- Voices and pictures are prepared in the background, then every screen refreshes.
  await expect(host.getByText(/voice clips and pictures ready/)).toBeVisible({ timeout: 60_000 })
  await expect(host.locator('img[src^="/media/assets/"]').first()).toBeVisible()
  await expect(host.getByText("Tonight's cocktails")).toBeVisible()

  // ---- The printable kit downloads as real PDFs for the host.
  await host.getByText('Printable party kit').click()
  const href = await host.getByRole('link', { name: /Invitations/ }).getAttribute('href')
  const pdf = await host.request.get(href!)
  expect(pdf.ok()).toBeTruthy()
  expect(pdf.headers()['content-type']).toContain('application/pdf')
  await host.screenshot({ path: `${SHOTS}/30-stage-lobby-media.png`, fullPage: true })

  // ---- Three guests join; one uploads a costume selfie, which appears on the big screen.
  const guests = []
  for (const [name, character] of [
    ['Ada', /Dr\. Cornelius Finch/],
    ['Bea', /Mr\. Alistair Hargrove/],
    ['Cy', /Lady Evelyn/],
  ] as const) {
    const page = await (await browser.newContext(phone)).newPage()
    page.on('dialog', (d) => d.accept())
    await page.goto(`/join/${code}`)
    await page.getByLabel('Your name').fill(name)
    await page.getByRole('button', { name: 'Take my seat' }).click()
    await page.waitForURL(`**/play/${code}`)
    await page.getByRole('button', { name: character }).first().click()
    await expect(page.getByText('You will play')).toBeVisible()
    guests.push(page)
  }
  const ada = guests[0]
  await ada.getByLabel('Costume selfie').setInputFiles({ name: 'costume.png', mimeType: 'image/png', buffer: TINY_PNG })
  await expect(ada.getByRole('img', { name: 'Your costume selfie' })).toBeVisible()
  await expect(host.getByRole('img', { name: "Ada's costume" }).first()).toBeVisible()
  await ada.screenshot({ path: `${SHOTS}/31-phone-selfie.png` })

  // Removing it takes it off the big screen too.
  await ada.getByRole('button', { name: 'Remove' }).click()
  await expect(host.getByRole('img', { name: "Ada's costume" })).toHaveCount(0)

  // ---- The prologue calls for a toast, with the alcohol-free alternative beside it.
  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await expect(host.getByText(/Raise your glasses to our host/)).toBeVisible({ timeout: 30_000 })
  await expect(host.getByText(/Not drinking\?/)).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/32-stage-toast.png` })
})
