# Deploying on Coolify

The whole game is one Docker Compose stack: the `app` container (API + real-time hub + web front end) and a `db` container (PostgreSQL 16). Coolify builds it from this repository and puts it behind HTTPS on your domain.

## Before you start

- A Linux server with Coolify installed and your GitHub account or the Coolify GitHub App connected.
- A domain or subdomain (e.g. `mystery.example.com`) with a DNS **A record** pointing at the server's IP.

## 1. Create the resource

1. In Coolify, open your project and environment and choose **+ New → Resource**.
2. Pick your repository (via the GitHub App or a deploy key) and the branch to deploy.
3. Set **Build Pack** to **Docker Compose** and keep the compose file path as `/docker-compose.yml`.
4. Save. Coolify reads the compose file and lists two services: `app` and `db`.

## 2. Give the app a domain

In the resource's configuration, set the domain for the **app** service. The container listens on port **8080**, so tell Coolify's proxy which port to route to by including it in the domain:

```
https://mystery.example.com:8080
```

Coolify still serves the site on the normal HTTPS port (443) and issues a Let's Encrypt certificate; the `:8080` only tells the proxy where the container listens. WebSockets (used by SignalR) pass through Coolify's Traefik proxy without extra configuration.

Don't give the `db` service a domain. It should only be reachable inside the stack.

## 3. Environment variables

In **Environment Variables**, add:

| Variable | Value | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | a long random string | Used by both containers. Mark it as a secret. |
| `ALLOW_REGISTRATION` | `true` at first | Set to `false` once your host account exists, so strangers can't sign up. New hosts then need an invite link from you (**Admin hub → Sign-ups**). You can also switch sign-ups there, which overrides this setting with no redeploy. |

The compose file already sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. This tells ASP.NET Core to trust the `X-Forwarded-Proto` header from the proxy, so the app knows visitors arrived over HTTPS. Secure cookies and correct QR code links depend on it.

### Optional: email (password reset)
To let hosts reset a forgotten password themselves, add SMTP settings from any email provider (Resend, Mailgun, Postmark, Amazon SES, Gmail with an app password…):

| Variable | Example |
|---|---|
| `PUBLIC_URL` | `https://mystery.example.com`. Links in emails are built from this, never from the incoming request, so a forged `Host` header can't redirect a reset link. |
| `SMTP_HOST`, `SMTP_PORT` | `smtp.resend.com`, `587` (or `465`) |
| `SMTP_USERNAME`, `SMTP_PASSWORD` | from your provider. Mark the password as a secret. |
| `SMTP_FROM` | `The Butler Did It <butler@example.com>` |
| `REQUIRE_CONFIRMED_EMAIL` | `true` to make new hosts click the link in their welcome email before creating parties |

**Making sure it reaches inboxes, not spam.** Mail providers trust email that proves it comes from your domain. Before real hosts sign up:
1. **Use a transactional email provider** (Postmark, Resend, Amazon SES, Mailgun, Brevo…), not a personal Gmail account: they're built for confirmation and reset emails, and keep a good sending reputation.
2. **Send from your own domain**, e.g. `SMTP_FROM` = `The Butler Did It <butler@yourdomain.com>`, and verify that domain with the provider.
3. **Add the DNS records the provider shows you** (in your DNS host, e.g. Cloudflare):
   - **SPF**, a `TXT` record on the domain listing who may send for it, e.g. `v=spf1 include:<the provider's domain> ~all`. A domain has only one SPF record, so if you already have one (for your own mailbox), add the provider's `include:` to it rather than adding a second;
   - **DKIM**, usually two or three `CNAME` or `TXT` records: the provider signs each email, and these let inboxes check the signature;
   - **DMARC**, a `TXT` record at `_dmarc.yourdomain.com` that tells inboxes what to do with mail that fails those checks. Start with `v=DMARC1; p=none; rua=mailto:you@yourdomain.com` to get reports, and move to `p=quarantine` once everything passes;
   - a **custom return-path** (or "bounce domain"), if the provider offers one, so SPF lines up with your domain too.
4. **Check it:** use **Forgot your password?** to send yourself an email, open it in Gmail and choose **Show original**. SPF, DKIM and DMARC should each say `PASS`. [mail-tester.com](https://www.mail-tester.com) gives a score as well.

The site sends: email confirmations, password resets, email-change links, invites, and alerts to the admin. **Receipts and payment emails come from Stripe**, not from the site. In Stripe, turn on receipts for successful payments and refunds under **Settings → Customer emails**, and the reminders before a renewal and after a failed payment under **Settings → Billing → Subscriptions and emails** (some states require a reminder before a yearly plan renews). Set your support address and branding there too.

**Without email**, the sign-in page tells hosts to ask the admin. The admin opens **Admin hub → Hosts** from the account menu (their name, top right) and presses **Make a reset link**, then sends the link to the host. It works once, for 3 hours.

### Optional: a check that new hosts are people (Cloudflare Turnstile)
Once anyone can sign up, scripts can make accounts too, each with a free trial (and its AI budget). Sign-ups are already limited per address (`RateLimits__RegisterPerHour`, 10) and can require a confirmed email. To check that a person is filling in the form as well, use Cloudflare's **Turnstile**, which is free and usually needs no clicking:
1. In the Cloudflare dashboard, open **Turnstile → Add widget**, name it, add your domain (and `localhost` to try it locally), and choose **Managed**.
2. Copy the **site key** into `TURNSTILE_SITE_KEY` and the **secret key** into `TURNSTILE_SECRET_KEY` (mark it as a secret), and redeploy. For a Dockerfile application, the names are `Turnstile__SiteKey` and `Turnstile__SecretKey`.

The sign-up form then shows the widget and its button waits for it; the server checks each answer with Cloudflare, and refuses the sign-up if Cloudflare can't be reached. Signing in isn't checked. The privacy policy lists Cloudflare by itself, and **Admin hub → Overview** shows the check is on. With only one of the two keys, it stays off.

### Optional: AI game master
To switch on AI (generated mysteries, NPCs you can question, hints and verdicts), add `AI_PROVIDER_NAME`, `AI_PROVIDER_KIND`, `AI_PROVIDER_API_KEY` and the three `AI_*_MODEL` variables, as in `.env.example`. You can also skip these and set everything up later on the **Admin → AI** page. See [ai-setup.md](ai-setup.md).

For **voices and pictures**, also add `AI_MEDIA_PROVIDER_NAME` (e.g. `OpenAI`), `AI_MEDIA_API_KEY`, `AI_VOICE_MODEL` (`tts-1`) and `AI_IMAGE_MODEL` (`dall-e-3`). These use OpenAI even if your main provider is Claude, Gemini or Ollama. `AI_MEDIA_PROVIDER_KIND` can also be `Gemini`, `ElevenLabs` (voices only), `Piper` (local voices) or `StableDiffusion` (local pictures); the local ones need `AI_MEDIA_BASE_URL`. See [ai-setup.md](ai-setup.md).

To use **local models with Ollama**, deploy Ollama as another Coolify resource (or add it to the compose file). Then set the provider type to `Ollama` and the base URL to its internal address, e.g. `http://ollama:11434`. **Piper** (voices) and a **Stable Diffusion WebUI** (pictures) work the same way, at e.g. `http://piper:5000` and `http://stable-diffusion:7860` (start the WebUI with `--api`).

### Optional: payments
To sell plans (subscriptions and party passes) with Stripe, add `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET` and a `STRIPE_PRICE_…` for each plan you sell. Until then the site works as before: hosts get their games from the free trial and from you. See [Payments with Stripe](#payments-with-stripe) below.

## 4. Deploy

Click **Deploy**. The first build takes a few minutes (it compiles both the React app and the .NET app). On startup the app:

1. applies database migrations (creating the tables), then
2. loads every theme and mystery from `content/` into the database.

Check the app's logs for `Seeded 13 themes and 41 scenarios`, then open `https://mystery.example.com/healthz`, which should say `Healthy`.

Optionally, in the app service's health check settings, use path `/healthz` on port `8080`. The image includes `curl`, which Coolify's check runs inside the container, and a Docker `HEALTHCHECK` of its own.

## 5. First run

1. Visit your domain, choose **Sign in to host → Create an account**. The first account becomes the admin.
2. Make the site invite-only: open **Admin hub → Sign-ups** from the account menu (your name, top right) and choose **Invites only**. It takes effect at once, with no redeploy. (Or set `ALLOW_REGISTRATION=false` and redeploy; on a Dockerfile application the variable is `Auth__AllowRegistration`, see the next section. The switch in the hub wins over the variable until you press **Use the server's setting**.)
3. To add a host, make an invite on the same **Sign-ups** tab. Send them the link by text or chat. With email set up, the site can email it for you. Each link makes one account, and you choose how long it works (a day, a week or 30 days). Add their email address and only that address can use it.
4. Create a party. Put the stage on a TV and have guests scan the QR code.

## Deploying the Dockerfile as an application (without Docker Compose)

You can also deploy the app as a single Coolify **Application** (Build Pack: **Dockerfile**), with PostgreSQL as a separate Coolify **Database** resource. Three things differ from the Compose stack above.

**1. Use the app's own variable names.** The friendly names in this guide (`ALLOW_REGISTRATION`, `PUBLIC_URL`, `SMTP_HOST`…) are translated into the app's settings by `docker-compose.yml`. Without Compose nothing translates them, so the app never sees them. Use the real names instead. In each one, a double underscore `__` stands for a section of `appsettings.json` (`Auth__AllowRegistration` is `AllowRegistration` in the `Auth` section).

| Variable | Value | Why |
|---|---|---|
| `ConnectionStrings__Default` | `Host=<the database's internal host>;Port=5432;Database=…;Username=…;Password=…` | The only database setting the app reads. Copy the internal URL's parts from the database resource. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` | Coolify's proxy handles HTTPS. This tells the app visitors arrived over HTTPS, for secure cookies and `https` QR code and invite links. |
| `Auth__AllowRegistration` | `true` at first, then `false` | `false` makes the site invite-only. The admin hub's **Sign-ups** switch overrides it without a redeploy. |
| `App__PublicUrl` | `https://your.domain` | The address used in emailed and invite links. |
| `DataProtection__Store` | `Database` | Keeps the sign-in keys in PostgreSQL, so redeploys don't sign everyone out or make AI keys saved under **Admin hub → AI** unreadable. Keys already in `/data/keys` are copied in on the next start. |
| `Plans__TrialDays` | `14` | Optional: how long a new host's free trial of both games lasts. Hosts who had an account before plans keep both games free. |
| `Email__Host`, `Email__Port`, `Email__Username`, `Email__Password`, `Email__From` | as in *Optional: email* above | Optional. Add `Auth__RequireConfirmedEmail=true` once email works. |
| `Media__MaxVideoMb` | `100` | Optional: the largest video a host can upload for an escape room or a mystery, in MB. |
| `Media__UploadQuotaMb` | `2048` | Optional: how much each host can upload in all, for both games, in MB. The admin has no limit. |
| `Billing__Stripe__SecretKey`, `Billing__Stripe__WebhookSecret`, `Billing__Prices__BothMonthly`… | as in [Payments with Stripe](#payments-with-stripe) | Optional: selling plans. The table there gives each one's app name. |
| `Legal__OperatorName`, `Legal__ContactEmail`, `Legal__State`… | as in [Terms, privacy and refunds](#terms-privacy-and-refunds) | Who runs the site, as the terms and privacy pages name it. Set them before charging anyone. |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`… | as in [Monitoring](#monitoring) | Optional: errors and timings sent to an OpenTelemetry service. |
| `Turnstile__SiteKey`, `Turnstile__SecretKey` | from Cloudflare Turnstile | Optional: the sign-up form checks that a person is filling it in. See *Optional: a check that new hosts are people* above. |

The Dockerfile already sets the port (8080), the content folder and the data folders. Untick **Buildtime** on secrets such as the connection string: the app only reads them when it runs.

**2. Storage.** In **Persistent Storage**, add a volume mounted at `/data/media` for generated pictures, voice clips, costume selfies and the pictures, videos, sounds and music hosts upload for their escape rooms and mysteries. Without it they're lost on every redeploy. A new volume takes effect on the next deploy, so redeploy once you've added it. (`/data/keys` needs one too, unless you set `DataProtection__Store=Database`.)

If the site is behind Cloudflare's proxy (the orange cloud), its free plan refuses uploads over 100 MB, so leave `Media__MaxVideoMb` at 100 or less.

**3. Health check and backups.** In **Healthcheck**, set the path to `/healthz` and the port to `8080`, so Coolify knows when the app is really up. The Compose stack's nightly `backup` service (and its restore check) isn't there, so turn on **Scheduled Backups** on the PostgreSQL database resource instead, and turn on Coolify's notifications for failed backups. Test a restore yourself every few months: restore the newest backup into a new database resource and point a copy of the app at it.

**Troubleshooting a deploy that rolls back:**
- **`curl: not found` / `wget: not found`, then "New container is unhealthy".** Coolify checks the health by running `curl` inside the container. Images built before curl was added to the Dockerfile don't have it, so the check fails even though the app is fine. Update to the current code, or switch the health check off until you do. CI checks that curl is in the image, so this can't come back unnoticed.
- **`Cannot load library libgssapi_krb5.so.2` in the log.** This is harmless. The PostgreSQL driver looks for Kerberos (a corporate sign-in system) before using the normal password. The image now includes the library, so the line no longer appears.

## Data and backups

The compose file declares four named volumes, which Coolify keeps across redeploys:

| Volume | Holds |
|---|---|
| `pgdata` | the PostgreSQL database |
| `keys` | ASP.NET Data Protection keys. Without these, every redeploy would sign every host out. |
| `media` | generated pictures and voice clips, guests' costume selfies, and the pictures, videos and sounds hosts upload for their rooms. Back it up along with the database: the database only stores where each file is. |
| `backups` | nightly database dumps from the `backup` service (see below) |

### Database backups

The compose file includes a `backup` service that dumps the database every night into a fourth volume, `backups`. It keeps 7 daily, 4 weekly and 6 monthly copies, and deletes older ones. To change the time, set `BACKUP_SCHEDULE` to a cron expression, e.g. `0 4 * * *` for 4 a.m. The default is `@daily`, at midnight.

```
backups/
  last/     butlerdidit-latest.sql.gz   ← the most recent dump
  daily/    butlerdidit-20261031.sql.gz …
  weekly/   …
  monthly/  …
```

**Copy them off the server.** A backup on the same disk won't survive the disk. Either:
- in Coolify, add an S3-compatible destination (Cloudflare R2, Backblaze B2, AWS S3) and a nightly **Scheduled Task** that uploads `backups/last/`, or
- run `rclone copy` from the server's cron to any storage rclone supports.

Back up the `media` volume too: the database only records where each picture and voice clip is.

**To take a backup right now:** open a terminal on the `backup` service in Coolify and run `/backup.sh`.

### Every backup is test-restored

A backup nobody has restored is only a hope. So straight after each nightly dump, the `backup` service restores it into a scratch database next to the real one (`butlerdidit_restore_check`), checks it, and drops the copy (`deploy/backup/check-restore`, built into the service's image). It fails when:
- the dump doesn't restore (a broken or truncated file);
- the app's own tables are missing or empty (the migrations, the host accounts, the mysteries);
- it holds far fewer host accounts than the live database (the wrong database, or an empty one);
- or, run by hand, the newest dump is more than 30 hours old (`RESTORE_CHECK_MAX_AGE_HOURS`): backups have stopped.

Each night's result is in the `backup` service's logs, e.g. *Backup restore check passed: … AspNetUsers 12 (now 12), Parties 40 (now 41), … Last migration: 20261007211228_Alerts.*

**Get told when it fails** (or when backups stop altogether). A free [Healthchecks.io](https://healthchecks.io) check is made for this. Create one with a period of 1 day and a grace time of a few hours, then set:

| Variable | Value |
|---|---|
| `BACKUP_OK_URL` | the check's ping URL, `https://hc-ping.com/<uuid>`: called after a backup that passed its check |
| `BACKUP_FAILED_URL` | the same URL with `/fail` on the end: called when the backup or its check failed |

Healthchecks.io then emails you on a failure, and also when no backup arrives at all, which a failure alert alone can't catch. An Uptime Kuma **Push** monitor works too: its push URL with `?status=up` and `?status=down`, and a heartbeat interval of 25 hours.

**To run the check by hand:** in the `backup` service's terminal, run `/hooks/50-check-restore` (the newest dump), or `/hooks/50-check-restore check /backups/daily/<file>` (any other).

A full restore drill, with the app started on the restored copy, is still worth doing once or twice a year, following the steps below on a spare server.

### Restoring from a backup

1. Stop the `app` service so nothing writes while you restore.
2. Copy the dump you want onto the server, then replace the database with it:

   ```bash
   docker compose exec -T db psql -U butler -d postgres -c "DROP DATABASE butlerdidit WITH (FORCE)" -c "CREATE DATABASE butlerdidit"
   gunzip -c butlerdidit-latest.sql.gz | docker compose exec -T db psql -U butler -d butlerdidit -v ON_ERROR_STOP=1
   ```

3. Start `app` again. It applies any newer migrations on startup, so an older backup works with a newer version of the app.

These steps were tested by restoring a dump from the `backup` image into a fresh database. The row counts matched, and the app started healthy on the restored copy. The nightly check above does the restore part every night.

### Automatic clean-up

The app tidies up after itself every 6 hours:

| What | When | Setting |
|---|---|---|
| Lobbies and games nobody has touched | deleted after 14 days (frees the join code) | `Retention__IdlePartyDays` |
| Finished parties | seats, private notes and costume selfies removed after 30 days. Old seat links stop working; the party's record is kept. | `Retention__FinishedPartyDays` |

Set either to `0` to keep things forever.

## Running more than one server

One server comfortably runs many parties at once, so most hosts never need this. To run two or more copies of the app behind a load balancer (for redundancy or very large events), all copies must share the database, and each of these switches on something they need:

| Variable | Value | Why |
|---|---|---|
| `SCALE_MULTI_INSTANCE` | `true` | Servers take turns through the database: one command per party at a time, one server drops the timed clues, one runs the clean-up. |
| `REDIS_URL` | e.g. `redis:6379` | Live updates reach every screen, whichever server it's connected to. Add a Redis service (Coolify has a one-click Redis). |
| `DATA_PROTECTION_STORE` | `Database` | Every server uses the same sign-in keys, so hosts stay signed in whichever server answers. On first start, the keys already in the `keys` volume are copied into the database, so saved AI keys keep working. Keep the volume mounted for that first start. |
| `MEDIA_STORAGE` | `S3` | Pictures, voices and selfies go in a bucket every server can reach. Also set `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, and for anything but AWS, `S3_SERVICE_URL` (e.g. `https://<account>.r2.cloudflarestorage.com`) and `S3_REGION` (`auto` for R2). |
| `MIGRATE_ON_STARTUP` | `false` (optional) | Run migrations once as a deploy step instead: `dotnet ButlerDidIt.Api.dll --migrate` migrates, loads the content, and exits. Without this, servers starting together simply take turns. |

**Moving existing media to S3:** copy the `media` volume into the bucket, keeping the folder layout (`2026-09/…`), for example with `rclone copy /path/to/media r2:your-bucket`. The database stores each file's path, so the same paths work in the bucket.

Files are always served through the app (`/media/assets/…`), so the bucket can stay private. For now the app reads a file from the bucket whole before serving it, which is fine for pictures and voices but slow for large uploaded videos; the local volume streams them from disk (#119).

## Monitoring

Three things tell you when something's wrong, so you hear about it before your hosts do.

### 1. Is the site up? (uptime checks)

`https://your.domain/healthz` answers `Healthy` when the app is running and can reach its database, and an error otherwise. Coolify's own health check restarts a stuck container, but doesn't tell you. Point an uptime checker at the address too:
- **Uptime Kuma** runs on your own server: Coolify can add it in one click. Add an HTTP monitor for `/healthz`, checking every minute, and a notification (email, Telegram, Discord…).
- **A hosted one** (UptimeRobot, Better Stack…) is better still, because it notices when the whole server is down. Their free plans check every few minutes.

Check from outside the server where you can: a checker on the same machine goes down with it.

### 2. What went wrong? (errors and performance, with OpenTelemetry)

The app can send its errors, logs and timings to any service that speaks **OpenTelemetry** (OTLP): Honeycomb, Grafana Cloud, Axiom, SigNoz, Uptrace, or a collector of your own. You see each request with the database queries and calls to AI providers and Stripe inside it, the exceptions with their stack traces, and the app's logs, all searchable. Switching service is a matter of settings.

It's off until you set the endpoint. On the service's "OpenTelemetry" or "OTLP" page, copy:

| Compose variable (and app setting) | Value |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | the OTLP address, e.g. `https://api.honeycomb.io` |
| `OTEL_EXPORTER_OTLP_HEADERS` | the key it asks for, as `name=value`, e.g. `x-honeycomb-team=your-key`. Mark it as a secret. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (the default) or `http/protobuf`, whichever the service says |
| `OTEL_SERVICE_NAME` | how the app is named there (`butler-did-it`) |
| `LEGAL_MONITORING_PROVIDER` (`Legal__MonitoringProvider`) | the service's name, for the privacy policy's list, e.g. `Honeycomb` |

These are OpenTelemetry's standard names, so a Dockerfile application uses the same ones.

**What's sent, and what isn't.** Requests are sent without the visitor's IP address or browser details, calls to other services without their query strings, and the app's logs never contain names, email addresses or what people type. Uptime checks of `/healthz` aren't traced. Once it's on, the privacy policy's list of services says so by itself.

Set up an alert there for errors (for example, "more than 5 errors in 10 minutes") and you'll hear about a broken release straight away.

### 3. Did something jump? (the site's own alerts)

Every hour, the app compares the last 24 hours with a usual day (the average of the week before), and tells the admin when:
- **AI spending jumped:** a leaked key, or a host running up costs. It shows how much, and **Admin hub → AI** shows who and what.
- **Failed payments jumped:** cards suddenly failing, or something wrong with Stripe.

Each alert is emailed to every admin account (when email is set up) and listed at the top of **Admin hub → Overview** for a week. A kind of alert is sent at most once a day, and a "jump" is measured against your own usual day, so a growing site isn't alerted all the time.

| Compose variable | App setting | Value |
|---|---|---|
| `ALERT_AI_SPEND_MIN_USD` | `Alerts__AiSpendMinUsd` | never alert below this much AI spending in a day ($5) |
| `ALERT_FAILED_PAYMENTS_MIN` | `Alerts__FailedPaymentsMin` | never alert below this many failed payments in a day (3) |
| `ALERT_EMAIL` | `Alerts__Email` | where to send them; empty: every admin account |
| | `Alerts__AiSpendFactor`, `Alerts__FailedPaymentsFactor` | how many times a usual day counts as a jump (3) |
| | `Alerts__IntervalMinutes` | how often to check (60); `0` turns the alerts off |

## Payments with Stripe

Hosts can buy a plan on their account page (**Your account → Plans**): a subscription to murder mysteries, escape rooms or both, monthly or yearly, or a one-off **party pass** of one game (or both) for 72 hours. Payments are taken on Stripe's own checkout page, so card numbers never reach your server. Hosts change plan, update their card, see invoices or cancel on Stripe's billing page (**Manage billing**). You never build or run a billing screen.

What hosts get from each:
- **A subscription** gives its games until the end of what's been paid for. Bought during the free trial, the first payment waits until the trial ends.
- **Cancelling** keeps the games until the end of the paid period.
- **A failed renewal** keeps the games for `PAYMENT_GRACE_DAYS` (7) while Stripe retries the card. After that, starting a new game asks them to update their card.
- **A party pass** starts when it's paid for.
- **Deleting an account** cancels its subscription first, so nobody is charged for an account that's gone.
- **Guests never pay**, and a party already under way is never stopped.

Payments stay off until `STRIPE_SECRET_KEY` is set. Start in Stripe's **test mode**: everything below works the same with test keys, and no real money moves.

### 1. Products and prices
In the Stripe dashboard, under **Product catalog**, add a product for each set of games you sell, e.g. "Murder mysteries", "Escape rooms" and "Both games". On each, add the prices you want:
- **subscriptions:** a **recurring** price, monthly and/or yearly;
- **party passes:** a **one-time** price, on the same product or one of its own.

Copy each price's id (`price_…`). Which games a price gives is decided by the setting you put it in, so name the products however you like.

**Changing a price later:** add the new price to the same product and swap the setting. Subscribers on the old price keep their games, because the old price belongs to the same product.

### 2. Settings
| Compose variable | App setting (Dockerfile application) | Value |
|---|---|---|
| `STRIPE_SECRET_KEY` | `Billing__Stripe__SecretKey` | Developers → API keys → **Secret key** (`sk_test_…`, later `sk_live_…`). Mark it as a secret. A restricted key (`rk_…`) works too if it can write Checkout Sessions, Customers, the customer portal and Subscriptions, and read Prices. |
| `STRIPE_WEBHOOK_SECRET` | `Billing__Stripe__WebhookSecret` | The webhook's signing secret (`whsec_…`), from step 3. Mark it as a secret. |
| `STRIPE_PRICE_MYSTERIES_MONTHLY`, `…_MYSTERIES_YEARLY` | `Billing__Prices__MysteriesMonthly`, `…MysteriesYearly` | price ids; leave a plan empty to not sell it |
| `STRIPE_PRICE_ESCAPE_MONTHLY`, `…_ESCAPE_YEARLY` | `Billing__Prices__EscapeRoomsMonthly`, `…EscapeRoomsYearly` | |
| `STRIPE_PRICE_BOTH_MONTHLY`, `…_BOTH_YEARLY` | `Billing__Prices__BothMonthly`, `…BothYearly` | |
| `STRIPE_PRICE_MYSTERIES_PASS`, `…_ESCAPE_PASS`, `…_BOTH_PASS` | `Billing__Prices__MysteriesPass`, `…EscapeRoomsPass`, `…BothPass` | one-time price ids |
| `PASS_HOURS` | `Billing__PassHours` | how long a pass lasts (72) |
| `PAYMENT_GRACE_DAYS` | `Billing__GraceDays` | days of games after a failed renewal (7) |
| `STRIPE_AUTOMATIC_TAX` | `Billing__AutomaticTax` | `true` to let Stripe Tax add sales tax or VAT; set Stripe Tax up in the dashboard first |

Set `PUBLIC_URL` too: Stripe sends hosts back to that address after paying.

### 3. The webhook
Stripe tells the site about payments by calling it. Under **Developers → Webhooks**, add an endpoint:
- **URL:** `https://your.domain/api/billing/webhook`
- **Events:** `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid` and `invoice.payment_failed`.

Copy its **signing secret** into `STRIPE_WEBHOOK_SECRET` and redeploy. The site refuses any webhook without a valid signature from that secret, so nobody else can fake a payment.

### 4. Stripe's billing page
Under **Settings → Billing → Customer portal**, turn on what hosts may do: update their payment method, see invoices, and cancel (choose "at the end of the billing period"). To let them switch plans there, turn on subscription updates and add your products. **Save** it: Stripe needs the portal saved once, in test mode and again in live mode, before **Manage billing** works.

Under **Settings → Billing → Subscriptions and emails**, choose what happens when a renewal fails. Smart Retries, then **cancel the subscription** after the last retry, works well with the grace days.

### 5. Try it
1. Open **Admin hub → Plans & billing**. It shows:
   - whether payments are on, and in which mode;
   - whether the webhook secret is set;
   - each plan with its price as Stripe has it.

   A plan whose price can't be sold as set up is marked with the reason (a typo, a test-mode price with a live key, a one-time price on a monthly plan…) and isn't offered to hosts.
2. As a host (not the admin, who never pays), open **Your account → Plans**, choose a plan and pay with Stripe's test card `4242 4242 4242 4242`, any future date and any CVC.
3. You're sent back to the account page, which confirms the payment. **Plans & billing** shows the subscription and the webhook that arrived.

If a webhook ever goes missing, the site still checks with Stripe when a renewal is overdue. You can also press **Sync** next to the host.

### 6. Going live
1. Get the [terms, privacy and refund pages](#terms-privacy-and-refunds) ready: Stripe asks for them, and so does the law.
2. Activate your Stripe account.
3. In live mode, create the same products and prices, add the live webhook endpoint, and save the live customer portal.
4. Swap in the live secret key, the live webhook secret and the live price ids, then redeploy.

**Refunds** are made in the Stripe dashboard. Refunding a subscription's payment doesn't cancel it: cancel the subscription there as well, and the host's games end when it does. A refunded party pass runs out on its own within its hours (#147 would end it at once). The site's [refund policy](#terms-privacy-and-refunds) says when you give one.

## Terms, privacy and refunds

The site has a **Terms of Service** (`/terms`), a **Privacy Policy** (`/privacy`) and a **Refund Policy** (`/refunds`). They're linked:
- at the foot of every page;
- on the sign-up form, which says that creating an account means agreeing to the terms and privacy policy (the moment each host agreed is recorded, and is in their data download);
- next to the plans on **Your account**, with the renewal terms;
- on Stripe's payment page, by the pay button: how the plan renews or ends, and the links to the terms and refund policy.

**They are starter drafts, not legal advice.** They were written for this site as it works today, for customers in the United States, with a 14-day refund for an unused payment. Before you charge anyone, have a lawyer review them. The privacy policy's "Children" section matters most: Family games are meant to be played by children as guests, and a photo of a child is personal information under COPPA. Lines starting `REVIEW:` in the files are questions for your lawyer; they never reach a browser. #149 is the checklist, and #150 a possible change for selfies at Family parties.

**1. Your details.** The pages name you, so set these. Until they're set, the pages show "[… not set yet]" in their place, and the admin hub's overview (**Legal pages**) lists what's missing.

| Compose variable | App setting (Dockerfile application) | Value |
|---|---|---|
| `LEGAL_OPERATOR_NAME` | `Legal__OperatorName` | who runs the site: your name, or your business's ("Butler Games LLC") |
| `LEGAL_CONTACT_EMAIL` | `Legal__ContactEmail` | where people write about their account, their data or a refund |
| `LEGAL_STATE` | `Legal__State` | the US state whose law governs the terms, usually where you're based |
| `REFUND_DAYS` | `Legal__RefundDays` | days after a payment in which an unused one is refunded in full (14) |
| `LEGAL_HOSTING_PROVIDER`, `LEGAL_EMAIL_PROVIDER`, `LEGAL_STORAGE_PROVIDER` | `Legal__HostingProvider`, `Legal__EmailProvider`, `Legal__StorageProvider` | optional: the companies behind your server, your email and (with `MEDIA_STORAGE=S3`) your file storage, e.g. "Hetzner", "Postmark", "Cloudflare R2". Left empty, the privacy policy says "our hosting provider" and so on. |

**2. What fills itself in.** Everything else the pages say about how the site works comes from its own settings, so the pages can't drift from the truth: the trial length, the pass hours, the grace days after a failed payment, the refund days, how long parties and selfies are kept (`Retention__…`), and the list of outside services in the privacy policy. That list names Stripe only when payments are on, the email service only when email is set up, and the AI companies you've given a role under **Admin hub → AI** (Anthropic, OpenAI, Google, ElevenLabs; self-hosted models are described as such). Change a setting and the pages follow.

**3. Review, then mark them reviewed.** Each file starts with a line `<!-- starter draft: … -->`. Once a page has been reviewed, delete that line: the **Legal pages** check turns green when all three are done. Until then, you (and only you) see a "starter draft" note at the top of each page.

**Editing the pages.** They're Markdown files in `content/legal/` (`terms.md`, `privacy.md`, `refunds.md`):
- headings (`#`, `##`), paragraphs, lists, `**bold**`, `_italic_` and links (`[Privacy Policy](/privacy)`) are shown; anything fancier appears as plain text;
- notes between `<!--` and `-->` are removed before the page is sent, so they're for you alone;
- `{{Operator}}`, `{{ContactEmail}}`, `{{State}}`, `{{SiteName}}`, `{{SiteUrl}}`, `{{TrialDays}}`, `{{PassHours}}`, `{{GraceDays}}`, `{{RefundDays}}`, `{{IdlePartyDays}}`, `{{FinishedPartyDays}}`, `{{BackupMonths}}` and `{{ServiceProviders}}` are filled in by the server. A misspelt one shows as it is, so you'll spot it.

Commit and redeploy to publish a change. To edit them on the server instead, mount a folder (a Coolify **Persistent Storage** volume), copy the three files into it, and set `Legal__Folder` to its path (e.g. `/data/legal`).

**4. In Stripe:**
- Under **Settings → Business → Public details**, add the addresses of your terms and privacy pages (`https://your.domain/terms`, `https://your.domain/privacy`): Stripe shows them on receipts and its pages.
- **United States only:** the terms say hosts must live in the US. To refuse cards issued elsewhere, add a Radar rule such as `Block if :card_country: != 'US'` (**More → Radar → Rules**; custom rules may need Radar for Fraud Teams).
- **Giving a refund:** see **Refunds** at the end of [Payments with Stripe](#payments-with-stripe). To check a payment was unused, **Admin hub → Hosts** shows when each host last made a party: if that's before the payment, nothing was started with it.

## Updating

Push to the deployed branch and click **Deploy** (or enable automatic deployments on push). Database migrations run automatically on start.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Hosts are logged out after each deploy | The `keys` volume isn't persisted. |
| Stage says "Reconnecting…" forever | Something between the browser and Coolify blocks WebSockets. SignalR falls back to long polling, but check any extra proxy or CDN in front (e.g. enable WebSockets in Cloudflare). |
| QR code or links show `http://` | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is missing. |
| Container exits with "Set POSTGRES_PASSWORD" | The environment variable isn't set in Coolify. |
