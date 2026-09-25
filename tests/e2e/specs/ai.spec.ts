import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// This file runs first (alphabetical order), so its host is the first account in
// the fresh database and therefore the admin.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })

const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

async function joinAs(browser: Browser, code: string, name: string, character: RegExp) {
  const page = await (await browser.newContext(phone)).newPage()
  page.on('dialog', (d) => d.accept())
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await page.getByRole('button', { name: character }).click()
  await expect(page.getByText('You will play')).toBeVisible()
  return page
}

test('AI game master: generate a mystery, question an NPC, get a hint and hear the verdicts', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  host.on('dialog', (d) => d.accept())

  // ---- The first account is the admin and can see the AI settings.
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Admin Agatha')
  // Fixed address: each run uses a fresh database, and hosts.spec.ts signs in as this admin.
  await host.getByLabel('Email').fill('admin@e2e.test')
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  await host.goto('/admin/ai')
  await expect(host.getByRole('heading', { name: 'AI game master' })).toBeVisible()
  await expect(host.getByText('from environment variables')).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/20-admin-ai.png`, fullPage: true })

  // ---- Generate a brand-new family-friendly mystery for a "Coming soon" theme.
  await host.goto('/host/new')
  await host.getByRole('button', { name: /Write a brand-new mystery with AI/ }).click()
  await host.getByLabel('Theme').selectOption('speakeasy')
  await host.getByLabel('Content').selectOption('family')
  await host.getByLabel('Length').selectOption('short')
  await host.getByLabel(/Twist/).fill('a stolen trumpet')
  await host.getByRole('button', { name: 'Write my mystery' }).click()
  await expect(host.getByText(/is ready/)).toBeVisible({ timeout: 30_000 })
  await expect(host.getByText('✨ written by AI for you')).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/21-generated-mystery.png`, fullPage: true })

  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  // ---- Three guests take the optional characters, so the essential ones become NPCs.
  const ann = await joinAs(browser, code, 'Ann', /Mrs\. Butterworth/)
  const ben = await joinAs(browser, code, 'Ben', /Mr\. Jeeveston/)
  await joinAs(browser, code, 'Cal', /Dr\. Quill/)

  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await host.getByRole('button', { name: 'Begin Act One' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await expect(host.getByRole('heading', { name: 'The interrogation room' })).toBeVisible()

  // ---- Ann questions the Colonel (an NPC); everyone sees the answer.
  await ann.getByRole('button', { name: 'Question' }).click()
  await ann.getByRole('button', { name: /Colonel Mustardseed/ }).click()
  await ann.getByPlaceholder(/Ask Colonel Mustardseed/).fill('Where were you at nine o\'clock?')
  await ann.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(ann.getByText(/Colonel Mustardseed sniffs/)).toBeVisible()
  await expect(ann.getByText('2 questions left this act')).toBeVisible()
  await expect(host.getByText(/Colonel Mustardseed sniffs/)).toBeVisible()
  await ann.screenshot({ path: `${SHOTS}/22-phone-question-npc.png` })
  await host.screenshot({ path: `${SHOTS}/23-stage-interrogation.png` })

  // ---- Ben asks the Inspector for a private hint.
  await ben.getByRole('button', { name: /Clues/ }).click()
  await ben.getByRole('button', { name: 'Ask the Inspector' }).click()
  await expect(ben.getByText(/Inspector Graves murmurs/)).toBeVisible()
  await expect(ben.getByRole('button', { name: 'Hint used' })).toBeDisabled()
  await ann.getByRole('button', { name: /Clues/ }).click()
  await expect(ann.getByText(/Inspector Graves murmurs/)).toHaveCount(0)
  await ben.screenshot({ path: `${SHOTS}/24-phone-hint.png` })

  // ---- On to the reveal: the Inspector's verdicts appear once the killer is unmasked.
  await host.getByRole('button', { name: 'End Act 1' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await host.getByRole('button', { name: 'Time for accusations' }).click()
  await ann.getByRole('button', { name: /Colonel Mustardseed/ }).click()
  await ann.getByLabel('To escape a debt').check()
  await ann.getByLabel('Poisoned tea').check()
  await ann.getByRole('button', { name: 'Lock in my accusation' }).click()
  await expect(ann.getByText('Locked in.')).toBeVisible()

  await host.getByRole('button', { name: 'Reveal the truth' }).click()
  await host.getByRole('button', { name: 'Unmask the killer' }).click()
  await expect(host.getByRole('heading', { name: 'Colonel Mustardseed' })).toBeVisible()
  await expect(host.getByText(/Fake verdict for guest/).first()).toBeVisible({ timeout: 15_000 })
  await host.screenshot({ path: `${SHOTS}/25-stage-verdicts.png` })
})
