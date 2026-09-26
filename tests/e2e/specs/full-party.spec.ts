import { expect, test, type Browser, type Page } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })

const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

async function newPhone(browser: Browser) {
  const ctx = await browser.newContext(phone)
  const page = await ctx.newPage()
  page.on('dialog', (d) => d.accept())
  return page
}

async function hostCreatesParty(page: Page, mode: 'Dinner party' | 'Pass & play') {
  const email = `host-${Date.now()}-${Math.random().toString(36).slice(2, 7)}@example.com`
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Hostess Hattie')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByText('Death at Blackwood Manor')).toBeVisible()
  await page.getByRole('button', { name: new RegExp(mode) }).click()
  // This test knows the original killer, so it plays Version A rather than "Surprise me".
  await expect(page.getByLabel('Version', { exact: true })).toHaveValue('surprise')
  await page.getByLabel('Version', { exact: true }).selectOption('death-at-blackwood-manor')
  await page.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await page.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  return page.url().split('/').pop()!
}

async function joinAs(browser: Browser, code: string, name: string) {
  const page = await newPhone(browser)
  await page.goto(`/join/${code}`)
  await expect(page.getByText('Death at Blackwood Manor')).toBeVisible()
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await expect(page.getByText('Choose your character')).toBeVisible()
  return page
}

async function hostNext(stage: Page, label: string | RegExp) {
  await stage.getByRole('button', { name: label }).click()
}

test('a full dinner party: join, play three acts, accuse, reveal and vote', async ({ browser }) => {
  const stageCtx = await browser.newContext({ viewport: { width: 1440, height: 900 } })
  const stage = await stageCtx.newPage()
  stage.on('dialog', (d) => d.accept())
  const code = await hostCreatesParty(stage, 'Dinner party')

  await expect(stage.getByText(code, { exact: true }).first()).toBeVisible()
  await expect(stage.getByRole('img', { name: /QR code to join/ })).toBeVisible()

  // Four guests join on phones.
  const alice = await joinAs(browser, code, 'Alice')
  const bob = await joinAs(browser, code, 'Bob')
  const cara = await joinAs(browser, code, 'Cara')
  const dan = await joinAs(browser, code, 'Dan')

  // Two pick characters; the rest are assigned when the game starts.
  await alice.getByRole('button', { name: /Dr\. Cornelius Finch/ }).click()
  await expect(alice.getByText('You will play')).toBeVisible()
  await alice.getByRole('button', { name: "I'm ready" }).click()
  await bob.getByRole('button', { name: /Mr\. Alistair Hargrove/ }).click()
  await expect(bob.getByText('What to wear')).toBeVisible()
  // Secrets stay locked before the party.
  await expect(bob.getByText('half-brother')).toHaveCount(0)

  await expect(stage.getByText('Alice ✓')).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/01-stage-lobby.png` })
  await alice.screenshot({ path: `${SHOTS}/02-phone-invitation.png` })

  await hostNext(stage, 'Begin the evening')
  await stage.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(stage.getByRole('heading', { name: 'The suspects' })).toBeVisible()

  // Each phone gets only its own private dossier.
  await expect(alice.getByText('You are the murderer.', { exact: true })).toBeVisible()
  await expect(bob.getByText('Who you are')).toBeVisible()
  await expect(bob.getByText('You are the murderer.', { exact: true })).toHaveCount(0)
  await bob.getByRole('button', { name: 'Secrets' }).click()
  await expect(bob.getByText(/half-brother/)).toBeVisible()
  await expect(cara.getByText('Who you are')).toBeVisible()
  await expect(cara.getByText(/half-brother/)).toHaveCount(0)
  // Introductions: the host gives everyone a turn in cast order (Lady Evelyn, then Hargrove, then Finch);
  // the big screen shows a question card, and the speaker's phone tells them they're up.
  await stage.getByRole('button', { name: /🎤 Spotlight/ }).click()
  await expect(stage.getByRole('status').filter({ hasText: 'In the spotlight' })).toContainText('Introduce yourself')
  await stage.getByRole('button', { name: /Next speaker/ }).click()
  await expect(bob.getByText("You're up!")).toBeVisible()
  await stage.getByRole('button', { name: /Next speaker/ }).click()
  await expect(alice.getByText("You're up!")).toBeVisible()
  await expect(bob.getByText("You're up!")).toHaveCount(0)
  await stage.screenshot({ path: `${SHOTS}/03-stage-cast.png` })

  // The live guide explains the moment, on the big screen and on a phone.
  await stage.getByRole('button', { name: 'Guide' }).click()
  await expect(stage.getByRole('dialog', { name: 'How to play' }).getByRole('heading', { name: 'Meet the suspects' })).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/03b-stage-guide.png` })
  await stage.getByRole('button', { name: 'Close the guide' }).click()
  await bob.getByRole('button', { name: 'Guide' }).click()
  await expect(bob.getByText('How do they ever come out?', { exact: false })).toBeVisible()
  await bob.screenshot({ path: `${SHOTS}/03c-phone-guide.png` })
  await bob.getByRole('button', { name: 'Close the guide' }).click()
  await stage.getByRole('button', { name: 'Clear spotlight' }).click()
  await alice.screenshot({ path: `${SHOTS}/04-phone-murderer-dossier.png` })

  // Prologue and act one.
  await hostNext(stage, 'Play the prologue')
  await hostNext(stage, 'Begin Act One')
  await expect(stage.getByRole('heading', { name: /Act One/ })).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/05-stage-cinematic.png` })
  await hostNext(stage, 'Start mingling')
  await expect(stage.getByRole('heading', { name: 'The Silver Candlestick' })).toBeVisible()
  await expect(stage.getByText(/\d+:\d\d/).first()).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/06-stage-mingle.png` })
  await alice.getByRole('button', { name: 'Guide' }).click()
  await expect(alice.getByRole('dialog').getByText(/to say aloud this act|no scripted lines this act/)).toBeVisible()
  await alice.getByRole('button', { name: 'Close the guide' }).click()

  // A phone that reloads mid-game lands back in the same seat.
  await bob.reload()
  await expect(bob.getByText('Mr. Alistair Hargrove').first()).toBeVisible()
  await bob.getByRole('button', { name: /Clues/ }).click()
  await expect(bob.getByRole('heading', { name: 'The Silver Candlestick' })).toBeVisible()

  // Bob reveals a secret; it appears on the stage.
  await bob.getByRole('button', { name: 'Secrets' }).click()
  await bob.getByRole('button', { name: 'Reveal to everyone' }).first().click()
  await expect(stage.getByText(/I am Reginald's half-brother/)).toBeVisible()

  // Act two: drop the midway clues early and crack the diary riddle.
  await hostNext(stage, 'End Act 1')
  await hostNext(stage, 'Start mingling')
  for (let i = 0; i < 3; i++) await stage.getByRole('button', { name: /Drop next clue/ }).click({ timeout: 10_000 }).catch(() => {})
  await expect(stage.getByRole('heading', { name: "Reginald's Locked Diary" })).toBeVisible()
  await dan.getByRole('button', { name: /Clues/ }).click()
  const diary = dan.locator('article', { hasText: "Reginald's Locked Diary" })
  await diary.getByPlaceholder('Your answer').fill('The Clock!')
  await diary.getByRole('button', { name: 'Try' }).click()
  await expect(stage.getByText(/Solved by Dan/)).toBeVisible()
  await dan.screenshot({ path: `${SHOTS}/07-phone-clues.png` })

  // Act three, then accusations.
  await hostNext(stage, 'End Act 2')
  await hostNext(stage, 'Start mingling')
  await hostNext(stage, 'Time for accusations')
  await expect(stage.getByRole('heading', { name: /Who killed/ })).toBeVisible()

  const accuse = async (p: Page, suspect: RegExp, motive: string, method: string) => {
    await expect(p.getByRole('heading', { name: /Who killed/ })).toBeVisible()
    await p.getByRole('button', { name: suspect }).click()
    await p.getByLabel(motive).check()
    await p.getByLabel(method).check()
    await p.getByRole('button', { name: 'Lock in my accusation' }).click()
    await expect(p.getByText('Locked in.')).toBeVisible()
  }
  await accuse(bob, /Dr\. Cornelius Finch/, 'To stop him exposing a past crime', 'Poisoned port (digitalis)')
  await accuse(cara, /Captain Jack Rafferty|Mr\. Alistair Hargrove/, 'To secure an inheritance', 'Struck with the silver candlestick')
  await accuse(alice, /Mr\. Alistair Hargrove/, 'To secure an inheritance', 'Struck with the silver candlestick')
  await bob.screenshot({ path: `${SHOTS}/08-phone-accusation.png` })
  await expect(stage.getByText('3 of 4 accusations locked in')).toBeVisible()

  // The reveal, step by step.
  await hostNext(stage, 'Reveal the truth')
  await expect(stage.getByRole('heading', { name: 'The accusations' })).toBeVisible()
  await hostNext(stage, 'Unmask the killer')
  await expect(stage.getByRole('heading', { name: 'Dr. Cornelius Finch' })).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/09-stage-unmasked.png` })
  // Five explanation paragraphs, then the scores: six more steps.
  for (let step = 2; step <= 7; step++) {
    await hostNext(stage, 'Continue')
    if (step < 7) await expect(stage.locator('p.font-display.text-xl')).toHaveCount(step - 1)
  }
  await expect(stage.getByRole('heading', { name: "The detectives' scores" })).toBeVisible()
  // Bob named the killer, motive and method: 3 + 1 + 1.
  await expect(stage.locator('li', { hasText: 'Bob' }).getByText('5', { exact: true })).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/10-stage-scores.png` })

  // Awards.
  await hostNext(stage, 'On to the awards')
  await expect(bob.getByRole('heading', { name: 'Cast your votes' })).toBeVisible()
  await bob.getByRole('button', { name: /Alice/ }).first().click()
  await alice.getByRole('button', { name: /Bob/ }).first().click()
  await expect(stage.getByText('2 of 4 ballots cast')).toBeVisible()
  await hostNext(stage, 'Close voting & announce')
  await expect(stage.getByRole('heading', { name: 'And the winners are…' })).toBeVisible()
  await expect(bob.getByText(/Best Detective: Bob/)).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/11-stage-awards.png`, fullPage: true })

  // The recap: private until the host shares it, then readable by anyone with the link.
  await stage.getByRole('button', { name: 'Share the recap' }).click()
  const recapLink = await stage.getByLabel('Recap link').inputValue()
  const reader = await (await browser.newContext()).newPage() // no account, no seat
  await reader.goto(recapLink)
  await expect(reader.getByRole('heading', { name: 'Death at Blackwood Manor' })).toBeVisible()
  await expect(reader.getByText('played by Alice')).toBeVisible()
  await expect(reader.getByText('The killer')).toBeVisible()
  await expect(reader.getByRole('heading', { name: "Everyone's secrets" })).toBeVisible()
  await reader.screenshot({ path: `${SHOTS}/14-recap.png`, fullPage: true })

  await stage.getByRole('button', { name: 'Stop sharing' }).click()
  await expect(stage.getByRole('button', { name: 'Share the recap' })).toBeVisible()
  await reader.reload()
  await expect(reader.getByText("This recap isn't available")).toBeVisible()
})

test('pass and play: one device, private hand-offs', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 820, height: 1180 }, hasTouch: true })
  const stage = await ctx.newPage()
  const names = ['Grandma', 'Uncle Ted', 'Priya']
  let n = 0
  stage.on('dialog', (d) => d.accept(names[n++ % names.length]))
  const code = await hostCreatesParty(stage, 'Pass & play')

  for (let i = 0; i < 3; i++) {
    await stage.getByRole('button', { name: 'Add a pass-and-play guest' }).click()
    await expect(stage.getByText(`${i + 1} of up to 8 guests`)).toBeVisible()
  }
  await hostNext(stage, 'Begin the evening')
  await stage.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(stage.getByRole('heading', { name: 'The suspects' })).toBeVisible()

  await stage.getByRole('link', { name: 'Pass-and-play dossiers' }).click()
  await stage.getByRole('button', { name: 'Uncle Ted' }).click()
  await expect(stage.getByText('Hand the device to')).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/12-pass-handoff.png` })

  // Press and hold to open the dossier.
  const hold = stage.getByRole('button', { name: /Press and hold/ })
  const box = (await hold.boundingBox())!
  await stage.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await stage.mouse.down()
  // Keep holding until the dossier opens, instead of guessing how long the hold takes.
  await expect(stage.getByText('Private dossier: Uncle Ted')).toBeVisible()
  await stage.mouse.up()

  await expect(stage.getByText('Who you are')).toBeVisible()
  await stage.screenshot({ path: `${SHOTS}/13-pass-dossier.png` })
  await stage.getByRole('button', { name: 'Hide & pass on' }).click()
  await expect(stage.getByText('Who you are')).toHaveCount(0)
  await expect(stage.getByRole('button', { name: 'Grandma' })).toBeVisible()
})

test('the printable how-to-play sheet', async ({ page }) => {
  await page.goto('/how-to-play')
  await expect(page.getByRole('heading', { name: 'How to play' })).toBeVisible()
  await expect(page.getByText('My dossier says to keep my secrets. How do they ever come out?')).toBeVisible()
  await page.emulateMedia({ media: 'print' })
  await expect(page.getByRole('button', { name: 'Print this page' })).toBeHidden() // buttons don't print
  await page.pdf({ path: `${SHOTS}/60-how-to-play.pdf` })
})
