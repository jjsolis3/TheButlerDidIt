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
| `ALLOW_REGISTRATION` | `true` at first | Set to `false` once your host account exists, so strangers can't sign up. New hosts then need an invite link from you (**Hosts → Invites**). |

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

**Without email**, the sign-in page tells hosts to ask the admin. The admin opens **Hosts & invites** from the account menu (their name, top right) and presses **Make a reset link**, then sends the link to the host. It works once, for 3 hours.

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
2. Set `ALLOW_REGISTRATION=false` and redeploy to make the site invite-only. (Deployed as a Dockerfile application instead? The variable is `Auth__AllowRegistration`; see the next section.)
3. To add a host, sign in, open **Hosts & invites** from the account menu (your name, top right) and make an invite under **Invites**. Send them the link by text or chat. With email set up, the site can email it for you. Each link makes one account, and you choose how long it works (a day, a week or 30 days). Add their email address and only that address can use it.
4. Create a party. Put the stage on a TV and have guests scan the QR code.

## Deploying the Dockerfile as an application (without Docker Compose)

You can also deploy the app as a single Coolify **Application** (Build Pack: **Dockerfile**), with PostgreSQL as a separate Coolify **Database** resource. Three things differ from the Compose stack above.

**1. Use the app's own variable names.** The friendly names in this guide (`ALLOW_REGISTRATION`, `PUBLIC_URL`, `SMTP_HOST`…) are translated into the app's settings by `docker-compose.yml`. Without Compose nothing translates them, so the app never sees them. Use the real names instead. In each one, a double underscore `__` stands for a section of `appsettings.json` (`Auth__AllowRegistration` is `AllowRegistration` in the `Auth` section).

| Variable | Value | Why |
|---|---|---|
| `ConnectionStrings__Default` | `Host=<the database's internal host>;Port=5432;Database=…;Username=…;Password=…` | The only database setting the app reads. Copy the internal URL's parts from the database resource. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` | Coolify's proxy handles HTTPS. This tells the app visitors arrived over HTTPS, for secure cookies and `https` QR code and invite links. |
| `Auth__AllowRegistration` | `true` at first, then `false` | `false` makes the site invite-only. |
| `App__PublicUrl` | `https://your.domain` | The address used in emailed and invite links. |
| `DataProtection__Store` | `Database` | Keeps the sign-in keys in PostgreSQL, so redeploys don't sign everyone out or make AI keys saved under **AI settings** unreadable. Keys already in `/data/keys` are copied in on the next start. |
| `Plans__TrialDays` | `14` | Optional: how long a new host's free trial of both games lasts. Hosts who had an account before plans keep both games free. |
| `Email__Host`, `Email__Port`, `Email__Username`, `Email__Password`, `Email__From` | as in *Optional: email* above | Optional. Add `Auth__RequireConfirmedEmail=true` once email works. |

The Dockerfile already sets the port (8080), the content folder and the data folders. Untick **Buildtime** on secrets such as the connection string: the app only reads them when it runs.

**2. Storage.** In **Persistent Storage**, add a volume mounted at `/data/media` for generated pictures, voice clips and costume selfies. Without it they're lost on every redeploy. (`/data/keys` needs one too, unless you set `DataProtection__Store=Database`.)

**3. Health check and backups.** In **Healthcheck**, set the path to `/healthz` and the port to `8080`, so Coolify knows when the app is really up. The Compose stack's nightly `backup` service isn't there, so turn on **Scheduled Backups** on the PostgreSQL database resource instead.

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

Files are always served through the app (`/media/assets/…`), so the bucket can stay private.

## Updating

Push to the deployed branch and click **Deploy** (or enable automatic deployments on push). Database migrations run automatically on start.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Hosts are logged out after each deploy | The `keys` volume isn't persisted. |
| Stage says "Reconnecting…" forever | Something between the browser and Coolify blocks WebSockets. SignalR falls back to long polling, but check any extra proxy or CDN in front (e.g. enable WebSockets in Cloudflare). |
| QR code or links show `http://` | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is missing. |
| Container exits with "Set POSTGRES_PASSWORD" | The environment variable isn't set in Coolify. |
