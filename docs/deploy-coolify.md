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

## 4. Deploy

Click **Deploy**. The first build takes a few minutes (it compiles both the React app and the .NET app). On startup the app:

1. applies database migrations (creating the tables), then
2. loads every theme and mystery from `content/` into the database.

Check the app's logs for `Seeded 8 themes and 1 scenarios`, then open `https://mystery.example.com/healthz`, which should say `Healthy`.

Optionally, in the app service's health check settings, use path `/healthz` on port `8080`.

## 5. First run

1. Visit your domain, choose **Sign in to host → Create an account**. The first account becomes the admin.
2. Set `ALLOW_REGISTRATION=false` and redeploy if you want the server to be invite-only.
3. Create a party. Put the stage on a TV and have guests scan the QR code.

## Data and backups

The compose file declares three named volumes, which Coolify keeps across redeploys:

| Volume | Holds |
|---|---|
| `pgdata` | the PostgreSQL database |
| `keys` | ASP.NET Data Protection keys. Without these, every redeploy would sign every host out. |
| `media` | generated images and voices (from milestone 3) |

To back up the database, add a **Scheduled Task** in Coolify on the `db` service, for example nightly:

```bash
pg_dump -U butler butlerdidit | gzip > /var/lib/postgresql/data/backup-$(date +%F).sql.gz
```

Copy backups off the server regularly.

## Updating

Push to the deployed branch and click **Deploy** (or enable automatic deployments on push). Database migrations run automatically on start.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Hosts are logged out after each deploy | The `keys` volume isn't persisted. |
| Stage says "Reconnecting…" forever | Something between the browser and Coolify blocks WebSockets. SignalR falls back to long polling, but check any extra proxy or CDN in front (e.g. enable WebSockets in Cloudflare). |
| QR code or links show `http://` | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is missing. |
| Container exits with "Set POSTGRES_PASSWORD" | The environment variable isn't set in Coolify. |
