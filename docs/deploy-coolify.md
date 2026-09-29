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
| `ALLOW_REGISTRATION` | `true` at first | Set to `false` once your host account exists, so strangers can't sign up. |

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

**Without email**, the sign-in page tells hosts to ask the admin. The admin opens **Hosts** on the home page and presses **Make a reset link**, then sends the link to the host. It works once, for 3 hours.

### Optional: AI game master
To switch on AI (generated mysteries, NPCs you can question, hints and verdicts), add `AI_PROVIDER_NAME`, `AI_PROVIDER_KIND`, `AI_PROVIDER_API_KEY` and the three `AI_*_MODEL` variables, as in `.env.example`. You can also skip these and set everything up later on the **Admin → AI** page. See [ai-setup.md](ai-setup.md).

For **voices and pictures**, also add `AI_MEDIA_PROVIDER_NAME` (e.g. `OpenAI`), `AI_MEDIA_API_KEY`, `AI_VOICE_MODEL` (`tts-1`) and `AI_IMAGE_MODEL` (`dall-e-3`). These use OpenAI even if your main provider is Claude, Gemini or Ollama.

To use **local models with Ollama**, deploy Ollama as another Coolify resource (or add it to the compose file). Then set the provider type to `Ollama` and the base URL to its internal address, e.g. `http://ollama:11434`.

## 4. Deploy

Click **Deploy**. The first build takes a few minutes (it compiles both the React app and the .NET app). On startup the app:

1. applies database migrations (creating the tables), then
2. loads every theme and mystery from `content/` into the database.

Check the app's logs for `Seeded 13 themes and 41 scenarios`, then open `https://mystery.example.com/healthz`, which should say `Healthy`.

Optionally, in the app service's health check settings, use path `/healthz` on port `8080`.

## 5. First run

1. Visit your domain, choose **Sign in to host → Create an account**. The first account becomes the admin.
2. Set `ALLOW_REGISTRATION=false` and redeploy if you want the server to be invite-only.
3. Create a party. Put the stage on a TV and have guests scan the QR code.

## Data and backups

The compose file declares four named volumes, which Coolify keeps across redeploys:

| Volume | Holds |
|---|---|
| `pgdata` | the PostgreSQL database |
| `keys` | ASP.NET Data Protection keys. Without these, every redeploy would sign every host out. |
| `media` | generated pictures and voice clips, and guests' costume selfies. Back it up along with the database: the database only stores where each file is. |
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

### Restoring from a backup

1. Stop the `app` service so nothing writes while you restore.
2. Copy the dump you want onto the server, then replace the database with it:

   ```bash
   docker compose exec -T db psql -U butler -d postgres -c "DROP DATABASE butlerdidit WITH (FORCE)" -c "CREATE DATABASE butlerdidit"
   gunzip -c butlerdidit-latest.sql.gz | docker compose exec -T db psql -U butler -d butlerdidit -v ON_ERROR_STOP=1
   ```

3. Start `app` again. It applies any newer migrations on startup, so an older backup works with a newer version of the app.

These steps were tested by restoring a dump from the `backup` image into a fresh database. The row counts matched, and the app started healthy on the restored copy.

### Automatic clean-up

The app tidies up after itself every 6 hours:

| What | When | Setting |
|---|---|---|
| Lobbies and games nobody has touched | deleted after 14 days (frees the join code) | `Retention__IdlePartyDays` |
| Finished parties | seats, private notes and costume selfies removed after 30 days. Old seat links stop working; the party's record is kept. | `Retention__FinishedPartyDays` |

Set either to `0` to keep things forever.

## Updating

Push to the deployed branch and click **Deploy** (or enable automatic deployments on push). Database migrations run automatically on start.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Hosts are logged out after each deploy | The `keys` volume isn't persisted. |
| Stage says "Reconnecting…" forever | Something between the browser and Coolify blocks WebSockets. SignalR falls back to long polling, but check any extra proxy or CDN in front (e.g. enable WebSockets in Cloudflare). |
| QR code or links show `http://` | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is missing. |
| Container exits with "Set POSTGRES_PASSWORD" | The environment variable isn't set in Coolify. |
