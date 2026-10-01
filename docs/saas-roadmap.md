# Roadmap: running The Butler Did It as a paid service

This plan turns the site from "my server, my friends" into a service many hosts and families can pay for. It covers who can get an account, paying for mysteries, escape rooms or both, an escape room front door, and proper account and settings pages. Each step names the code it builds on and says why it's shaped that way. Tracking issue: #96.

## The one idea everything hangs on

Keep **who you are** separate from **what you may do**.

```
 Account (identity)            Plan (what you paid for)          Gates (server-side)
 ───────────────────           ─────────────────────────         ──────────────────────────────
 AppUser: email, password ──►  Subscription: plan, status   ──►  Entitlements: games you may start,
 made by sign-up or invite     made by Stripe, a comp grant,     AI budget, AI writing on/off
                               a trial or an invite                    │
                                                                       ▼
                                                    POST /api/parties           (mystery)
                                                    POST /api/parties/escape    (escape room)
                                                    POST …/generate             (AI writing)
```

**Why not "pay to register"?** It's tempting to make payment the price of an account, but every SaaS ends up separating them, for good reasons:

- **A payment needs someone to belong to.** Stripe attaches a subscription to a customer, and the customer to your user id. Creating the account first gives the payment a home.
- **Subscriptions lapse.** A card expires and the renewal fails. The host should keep their account, custom mysteries, AI-written rooms and recaps, and just be unable to *start* a new game until they renew. Deleting or locking the account would punish a bank error.
- **Trials, gifts and family.** "Free for my sister", "a week's trial" and "a year for beta testers" are all grants of access to an existing account. They aren't separate kinds of sign-up.
- **One place to check.** The code asks "may this host start an escape room?" in one place. It never asks "which plan is this?", so prices and bundles can change without a code change.

**What is never gated:** guests (they never pay and never need an account), joining a party, a party that's already been created (a failed renewal must not stop a game mid-evening), recaps, and the host's own content. Only *starting something new* is gated.

## Where the code already helps

| Need | Already there |
|---|---|
| A single place to check before any game starts | Every create endpoint runs the `AuthEndpoints.RequireConfirmedHost` endpoint filter. A `RequireGame(GameKind)` filter goes right next to it. |
| Knowing which game a request is for | `GameKind` (`Mystery`, `EscapeRoom`), architecture §14. An entitlement is "may start this `GameKind`". |
| Limiting the expensive part | `DbAiBudget` caps each host's monthly AI spend, and AI usage is logged per host. A plan sets the budget instead of the one global `Ai:MonthlyBudgetUsd`. |
| Keeping hosts apart | Parties, mysteries and rooms are owned by `HostUserId` / `OwnerUserId`. Guests need a seat token. Joins and sign-ups are rate-limited. |
| Many families at once | Per-party locks, Redis backplane, Postgres advisory locks, S3 media, keys in the database (architecture §13). |
| Closing sign-ups | `ALLOW_REGISTRATION=false` (`Auth:AllowRegistration`). Step 1 makes the sign-in page respect it. |

So the platform doesn't need re-architecting. It needs an access layer and the pages around it.

## The steps

They're ordered so each is useful on its own and later steps build on earlier ones.

### 1. Close sign-ups properly (done in the roadmap PR)

`ALLOW_REGISTRATION=false` already made the server refuse new accounts, but the sign-in page still offered "Create an account", which then failed. `GET /api/auth/options` now reports sign-up as open only while it would work (always on a brand-new server, so the admin can create the first account), and the sign-in page hides the link otherwise.

**Do this today if you don't want strangers signing up:** in Coolify, set `ALLOW_REGISTRATION=false` and redeploy.

### 2. Invite-only sign-ups (#97, done)

With `ALLOW_REGISTRATION=false` the site is invite-only. The admin makes an invite link on the Hosts page and sends it by text or chat, or has the site email it. The link works once, expires after 1 to 30 days, and can be limited to one email address.

- **Stored like seat tokens:** only a SHA-256 hash of the token is kept, so a database leak doesn't leak working invites.
- **Used up in the same transaction that creates the user**, so one link can't make two accounts if it's clicked twice.
- **Two modes, not three.** The plan was `Open`, `InviteOnly` and `Closed`. But "closed even to invites" only blocks an admin from their own invites, so the existing switch stays: open, or invite-only.
- **Plan grants come with plans** (step 5): an invite will be able to carry "Both games, free for a year".

### 3. Account hub (#98)

A header menu on every page (Account, My library, Settings, Admin, Sign out) and an `/account` page:

- **Profile:** name, email (changed only after a confirmation link to the new address), password, and "Sign out everywhere". These are ASP.NET Core Identity's `GenerateChangeEmailTokenAsync`, `ChangePasswordAsync` and `UpdateSecurityStampAsync`.
- **Library:** your parties with a Mystery or Escape badge, your mysteries, your AI-written rooms, your best escape times.
- **Usage:** parties this month and AI spend against your budget.
- **Plan & billing:** from step 6.
- **Danger zone:** download your data; delete your account.

**Why `/api/account/*` takes no user id:** every endpoint acts on the signed-in user, so there's nothing to change in a request to reach someone else's account.

### 4. Escape room front door (#99)

Today escape rooms are only inside `/host/new?game=escape`, behind sign-in, so a visitor can't see them at all.

- **`/`** becomes the front door for both games: "I have a party code" and two big cards, **Murder Mystery** (`/mystery`) and **Escape Room** (`/escape`).
- **`/mystery`** gets today's home page.
- **`/escape`** gets its own look and copy:
  - "How it works" in three steps;
  - the room shelf, with Adults / Family and Halloween chips and filters for players, length and difficulty;
  - each room's best time;
  - today's challenge leaderboard.
- **Everything shown already comes from public endpoints** (`GET /api/escape-rooms`, the leaderboards), which return no puzzles and no answers.
- "Host this room" links to the existing `/host/new?game=escape&room=…`.
- The room card moves out of `NewEscapeParty.tsx` into a shared component, so both pages show the same thing.

### 5. Plans and entitlements, granted by hand (#100)

This is the heart of "pay for murder, escape, or both".

- `SubscriptionEntity` (one per host): plan, status (`Trialing`, `Active`, `PastDue`, `Canceled`, `Comp`), period end, and the payment provider's ids (empty for hand grants).
- An `Entitlements` service turns that into what the code checks: the `GameKind`s this host may start, their AI budget, and whether AI writing is on. The admin is always entitled.
- A `RequireGame(GameKind)` endpoint filter on the four create endpoints.
- The admin can grant **Comp** access from the hosts page.
- `/api/auth/me` gains the host's games and plan, mirrored in `types.ts`, so pages can show "Unlock Escape Rooms". As everywhere else in this app, the page only displays the answer; the server is what enforces it.

**Why before payments?** Stripe is just one *source* of access. Hand grants, invites and trials are others. Building the check first means:

- every source feeds the same gate;
- the gating can be tested with no payment provider at all;
- you can run a paid beta by hand before wiring up Stripe.

**Plans to start with:** Mysteries, Escape Rooms, and Both (at a discount), monthly or yearly. Party games are seasonal (Halloween, holidays), so a one-off "party pass" (say, 72 hours of one game) is worth considering later. Stripe sells one-off passes as easily as subscriptions.

**Households, later.** One subscription belongs to one host for now. If families want several hosts on one plan, add a `Household` with members and move the subscription onto it. Because every check goes through `Entitlements`, only that service changes.

### 6. Billing with Stripe (#101)

- **Checkout** is Stripe's hosted payment page. Card numbers never reach this server, which keeps PCI compliance to the simplest level.
- **Customer Portal** is Stripe's hosted page for changing card, switching plan, cancelling and invoices, so there are no billing screens to build.
- **Webhooks are the truth.** The "payment succeeded" redirect is just a page the browser visits, and anyone can visit it. Access changes only when Stripe calls `POST /api/billing/webhook`. The handler:
  - verifies the `Stripe-Signature` header, so a forged call is refused;
  - records each event id, because Stripe retries and a retried event must count once;
  - re-reads the subscription from Stripe, because events can arrive out of order.

  The success page says "Confirming your payment…" until `/api/auth/me` shows the new plan.
- **Grace period:** a `PastDue` host keeps access for a few days while Stripe retries the card.
- **Behind an interface:** like `IEmailSender` and `IMediaStore`, billing sits behind an `IBillingProvider` with a fake for tests. If handling sales tax or VAT yourself becomes a burden, a *Merchant of Record* (Paddle, Lemon Squeezy) sells on your behalf and handles tax, and switching to one changes one class.
- **Several servers:** the webhook handler is an idempotent database upsert, so any server can receive it.

### 7. Settings and admin hubs (#102)

- **`/settings`** (every host): defaults the create-party pages start from: party mode, shelf, tone, drinking prompts, escape length and difficulty, AI on or off, TV sound. They're stored as jsonb on the host, so a new setting needs no migration.
- **`/admin`** (admin only): one hub for an overview (hosts, active plans, parties this week, AI spend), hosts, invites, plans and billing, AI, and the sign-up mode.

### 8. Ready for paying customers (#103)

- **An isolation test suite:** host B tries everything against host A's parties, kits, mysteries, rooms and usage. The checks exist today but are spread across files; one suite makes them a promise.
- **Terms, Privacy Policy and refund policy.** Stripe asks for these. Costume selfies and Family games played by children need care: COPPA covers services aimed at under-13s. Guests have no accounts, and selfies are stripped of metadata and deleted by `RetentionWorker`, which helps, but it has to be written down.
- **Monitoring:** error tracking, uptime checks on `/healthz`, and alerts on AI spend and failed payments.
- **Backups with a tested restore**, and a transactional email provider (SPF and DKIM) so receipts and reset links arrive.
- **Bots:** when sign-ups are open, a CAPTCHA (Cloudflare Turnstile) on the form, alongside the existing rate limit and email confirmation.

## Open questions for the owner

- **Prices**, and whether to offer a free tier, a trial, or a one-off party pass.
- **Branding:** does "The Butler Did It" stay the umbrella name for escape rooms too, or does the escape side get its own name under it?
- **Who you'll sell to first:** US-only (Stripe plus Stripe Tax is simplest) or international (a Merchant of Record handles VAT for you).
