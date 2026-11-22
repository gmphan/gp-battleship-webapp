# ⚓ GP Battleship Webapp

A real-time, multiplayer **Battleship** game for the browser, built with **ASP.NET Core (.NET 10) + SignalR**. Phone-friendly: players scan a QR code to join, two players per game, and an **admin console** can run *many* concurrent games at once. Ships are drawn as connected, color-coded hulls, and every move is broadcast live.

> **Read me first if you're adding a feature.** This document is written to be the single source of truth for *what the app is, how it works, and where everything lives*, so you (or an AI assistant) can orient in minutes and extend it safely. Jump to **§15.Z "Where do I put this?"** for a file map, **§4.1** to get it running locally, and **§6** for configuration/env-file rules.

---

## Table of contents
1. [What the app does](#1-what-the-app-does)
2. [High-level architecture](#2-high-level-architecture)
3. [Project layout](#3-project-layout)
4. [Running locally](#4-running-locally)
   - [4.1 One-time: database + env file](#41-one-time-database--env-file)
   - [4.2 Run it](#42-run-it)
5. [Running with Docker / production](#5-running-with-docker--production)
   - [5.1 Deploy on the OptiPlex + Cloudflare Tunnel](#51-deploy-on-the-optiplex--cloudflare-tunnel)
6. [Configuration](#6-configuration)
   - [6.0 How configuration & env files resolve](#60-how-configuration--env-files-resolve-read-before-adding-a-setting)   - [6.1 Authentication & admin management](#61-authentication--admin-management)
7. [The data model](#7-the-data-model)
8. [The SignalR hub — server API (complete)](#8-the-signalr-hub--server-api-complete)
9. [Client events — server → client (complete)](#9-client-events--server--client-complete)
10. [Client apps: admin console & player](#10-client-apps-admin-console--player)
11. [Game rules & turn logic](#11-game-rules--turn-logic)
12. [Rendering the boards (ships, hits, coordinates)](#12-rendering-the-boards-ships-hits-coordinates)
13. [HTTP endpoints](#13-http-endpoints)
14. [Concurrency & lifecycle rules](#14-concurrency--lifecycle-rules)
15. [How to add a feature (recipes)](#15-how-to-add-a-feature-recipes)
16. [Conventions & gotchas](#16-conventions--gotchas)
17. [Known limitations / future ideas](#17-known-limitations--future-ideas)

---

## 1. What the app does

- **Host (admin)** opens `/` (→ `/index.html`) → clicks **Create Game**. Each created game shows a **room code** and a **QR code**.
- **Players** scan the QR (or open `/play.html?room=CODE`). The first two to join fill that game's **P1** and **P2** seats.
- Each player **places their fleet** (5 ships) on a 10×10 board, then both go to **battle**.
- Players **fire** at the enemy board; hits/misses/sinks are broadcast to both players and reflected in the admin log.
- The **host does not play** — they run the lobby. They can **abort** a game or start a **New Game** (rematch) in the same room, and can run **arbitrarily many games simultaneously**.
- First player to sink all enemy ships **wins**.

Two pages share one server:
- `/index.html` + `/js/host.js` → **admin console** (multi-game dashboard).
- `/play.html` + `/js/player.js` → **player** (single game per tab, keyed by `?room=`).

---

## 2. High-level architecture

```
┌──────────────────────────┐        SignalR (WebSockets)         ┌──────────────────────────────┐
│  Admin console           │  ───────────────────────────────►   │                              │
│  index.html + host.js    │  CreateGame / AbortGame / NewGame   │   BattleshipHub (server)     │
│  (login required;        │  ResumeHost() / ListGames           │   in-memory game state       │
│   one connection,        │  ◄───────────────────────────────   │   Rooms /                    │
│   many rooms)            │  GameCreated / GameUpdated / ...    │   HostGames(username) /      │
└──────────────────────────┘                                      │   ConnectionRooms            │
                                                                  │                              │
┌──────────────────────────┐        SignalR (WebSockets)         │   • GameRoom (one per game)  │
│  Player                  │  ───────────────────────────────►   │   • Player (seat, fleet,     │
│  play.html + player.js   │  JoinGame / PlaceShips / Fire / ... │     shots, reconnect token)  │
│  (anonymous; one         │  ◄───────────────────────────────   │                              │
│   connection, one room)  │  GameJoined / GameStarted / ...     └──────────────────────────────┘
└──────────────────────────┘                                                  │
                                                                              │ /api/qr/{code}, /api/config
                                                                              ▼
                                                                     QRCoder (server-side PNG)
```

- **Game state is in-memory** — all rooms/seats live in **static `ConcurrentDictionary`s inside `BattleshipHub`** (single-container, ephemeral party game; **restarting wipes active games**). **Admin accounts**, however, are persisted in **PostgreSQL** (DB admins) combined with config admins from env — see §6.1.
- **Signalling is SignalR** at `/hub/battleship`. The browser client is loaded from a CDN (`@microsoft/signalr@8.0.7`).
- **Static files** are served from `wwwroot/`. `Program.cs` maps `/` → `/index.html`, serves QR codes and config.
- **QR codes** are generated server-side with `QRCoder` and encode the join URL (`{PUBLIC_BASE_URL}/play.html?room=CODE`).

---

## 3. Project layout

```
gp-battleship-webapp/
├─ README.md                      ← you are here
├─ .gitignore
├─ variables.example.env          ← template: copy to variables.<Env>.env and fill in
├─ variables.Development.local.env  ← YOUR local secrets (gitignored): DB conn + auth admins
├─ variables.<Env>.env            ← per-environment picks for F5 (Development/QA/Production/local) — gitignored
├─ Dockerfile                     ← multi-stage build → aspnet:10.0 runtime, non-root, port 8080
├─ Dockerfile.dockerignore        ← ignore rules (BuildKit picks this up for ./Dockerfile builds)
├─ build-and-publish.sh           ← dev: build + push image to GHCR
├─ stack/
│  └─ docker-compose.yml          ← pull-only container def (image: ghcr.io/... )
├─ stack/variables.env            ← server-side secrets (gitignored)
├─ .vscode/
│  ├─ launch.json                 ← F5 configs + "Select Environment" picker over variables.<Env>.env
│  └─ tasks.json                  ← build / publish / watch tasks
└─ src/
   ├─ src.slnx                    ← solution
   └─ GpBattleshipWebapp/
      ├─ GpBattleshipWebapp.csproj  ← net10.0; QRCoder, EF Core + Npgsql
      ├─ Program.cs                 ← host setup, static files, /api endpoints, hub mapping, injector boot
      ├─ appsettings.json           ← base config (PUBLIC_BASE_URL; DB/auth usually via env file)
      ├─ Properties/launchSettings.json
      ├─ Injectors/                 ← IInjector + discovery; DbInjector, AuthInjector, SignalRInjector
      ├─ Auth/                      ← AuthSetup (cookie), AuthConstants, AuthOptions, PasswordHasher
      ├─ Models/                    ← AdminUser (+ AdminRoles)
      ├─ Data/                      ← AppDbContext, AppDbContextFactory, StartupTasks, Configurations/, Repositories/, Migrations/
      │  ├─ Repositories/           ← IGenericRepository/GenericRepository, IAdminRepository/AdminRepository
      │  └─ Migrations/             ← EF Core migrations (auto-applied at startup)
      ├─ Endpoints/                 ← AuthEndpoints, AdminEndpoints (minimal APIs)
      ├─ Hubs/
      │  └─ BattleshipHub.cs        ← ⭐ ALL server game logic + state models
      └─ wwwroot/
         ├─ index.html              ← admin console page (+ login overlay, Admins panel)
         ├─ play.html               ← player page
         ├─ css/site.css            ← all styling (boards, hulls, labels, dashboard, login)
         └─ js/
            ├─ auth.js              ← login/logout/me helpers (window.Auth)
            ├─ host.js              ← admin console logic (auth-gated, multi-game)
            ├─ player.js            ← player logic (placement, battle, hulls, labels)
            └─ sfx.js               ← Web Audio sound engine (no asset files)
```

**The two files you will touch most when adding gameplay features:**
1. `Hubs/BattleshipHub.cs` — server rules, validation, state, broadcasts.
2. `wwwroot/js/player.js` (player UX) and/or `wwwroot/js/host.js` (admin UX).

---

## 4. Running locally

**Prerequisites:** .NET SDK 10, and a **PostgreSQL 14+** server reachable over TCP. Admin accounts are persisted there (see §6.1), so the app **cannot log anyone in without a DB**.

### 4.1 One-time: database + env file
1. **Create the database, owned by the app's login** (the app auto-creates *tables*, but it does **not** have privileges to `CREATE DATABASE`). Using a superuser:
   ```sql
   CREATE DATABASE gp_battleship OWNER ocbuumaster;   -- owner == the Username in your connection string
   -- (or: ALTER ROLE ocbuumaster CREATEDB; if you want the app to create it itself)
   ```
   > On a **Homebrew** Postgres the superuser is your macOS user (e.g. `giangphan`), **not** `postgres`. Connect with `psql -h localhost -U giangphan -d postgres`.

2. **Create your local env file** `variables.Development.local.env` (gitignored; see §6 / `variables.example.env`). Minimum to log in:
   ```bash
   ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=gp_battleship;Username=<app_user>;Password=<pw>
   Auth__InitialAdmin__Username=SuperAdmin
   Auth__InitialAdmin__Password=<at least 8 chars>
   Auth__InitialAdmin__Role=SuperAdmin
   # optional extra config admin:
   Auth__SeedAdmins__0__Username=AdminUser
   Auth__SeedAdmins__0__Password=<pw>
   Auth__SeedAdmins__0__Role=Admin
   ```

3. **First run** auto-applies EF migrations (`admin_users`). To apply manually: `cd src/GpBattleshipWebapp && dotnet ef database update`.

> **If startup logs `42501: permission denied to create database`** → the DB doesn't exist / isn't owned by your login. Do step 1. In Development the app now **keeps running** and logs the fix (it only hard-fails in Production, or when `Database__FailFast=true`).

### 4.2 Run it
Plain CLI (loads env vars however your shell does — **`source` splits on `;`** in the connection string, so prefer the F5 flow or `export` the whole string):

```bash
cd src/GpBattleshipWebapp
dotnet run   # then open the printed URL (default http://localhost:5184)
```

### Debugging with VS Code (F5) — pick your environment
Open the repo root, **F5**. `.vscode/launch.json`:
- **Launch Battleship** — on start it prompts **"Select Environment"** and loads `variables.<Env>.env` (the single source of truth for `ConnectionStrings__*`, `Auth__*`, `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`). Builds → launches on `http://localhost:5184` → opens the browser.
- **Launch Battleship (Watch / hot reload)** — same env picker, via `dotnet watch`.
- **Attach to Process** — attach to an already-running instance.

**Environment picker options** (🔽 choose in the F5 prompt): `Development`, `QA`, `Production`, `Development.local`.
- Each maps to `variables.<Name>.env` at the repo root. **The file must exist** or launch fails.
- On this machine, use **`Development.local`** — its `variables.Development.local.env` holds the working DB connection + admins. (`Development`/`QA`/`Production` `.env` files are templates.)
- To add an option: edit the `inputs[environment].options` array in `.vscode/launch.json` **and** create `variables.<Name>.env`.

> **Port note:** if F5 says "address already in use", a stale `dotnet` process holds the port: `lsof -nP -iTCP:5184 -sTCP:LISTEN` then `kill <pid>`.

### Quick manual test flow
1. Open `/` → **sign in** (SuperAdmin from your env file) → **Create Game**.
2. Copy the join link / QR. Open `/play.html?room=CODE` in **two** separate tabs/devices → Join as two players (no login needed).
3. Both place fleets → **Ready** → battle begins.
4. Fire on the enemy grid; watch the admin log and the other player's board.

---

## 5. Running with Docker / production

The deployment is **build-here, pull-there**:

1. **On your dev machine** (Docker required here only for the image build): run `./build-and-publish.sh`. It builds the linux/amd64 image and **pushes** it to GHCR as `ghcr.io/gmphan/gp-battleship-webapp:latest`. (This is the only place Docker is used; there's no local compose workflow.)
2. **On the OptiPlex server** (has only `docker-compose.yml` + `variables.env`, no source/Dockerfile): `docker compose pull && docker compose up -d` — compose pulls the prebuilt image and runs it. It does **not** build.

```bash
# dev machine — build + push
./build-and-publish.sh

# OptiPlex server — pull + run (compose file lives there, no source needed)
docker compose pull
docker compose up -d
```

- `stack/docker-compose.yml` is **pull-only** (`image:` + `pull_policy: always`, no `build:`). Run it from the repo root with `docker compose -f stack/docker-compose.yml up -d`. Host port **8082 → container 8080** (`8080`/`8081` are already used on this server: `8080`=signage, `8081`=gmphan_webapp, `80`=cloudflared, `5432`=postgres, `9001/9443`=portainer).
- The container listens on `8080` internally (`ASPNETCORE_URLS=http://+:8080`), runs as the non-root `app` user.
- Designed to sit behind a **Cloudflare Tunnel**/reverse proxy. Point the tunnel's public hostname at **`http://localhost:8082`**. `Program.cs` enables `ForwardedHeaders` (`X-Forwarded-For/Proto/Host`) and clears known networks/proxies so the tunnel is trusted.
- **`PUBLIC_BASE_URL`** (in `docker-compose.yml` environment) MUST be set to your real public HTTPS origin (e.g. `https://battleship.giangmichaelphan.com`). It's used to build the QR/join link so phones hit the tunnel, not `localhost`.
- If the GHCR package is **private**, log in on the server once: `echo $CR_PAT | docker login ghcr.io -u gmphan --password-stdin`. To avoid auth entirely, set the package to **Public** in GitHub → Packages → Package settings.

---

## 5.1 Deploy on the OptiPlex + Cloudflare Tunnel

This is the actual production setup: the app runs in Docker on a home **OptiPlex** server, and a **`cloudflared` tunnel** publishes it under a public HTTPS hostname (no port-forwarding, TLS handled by Cloudflare). Phones must reach it over **HTTPS**, which is also what makes QR-scan → camera → play work smoothly.

### Server context (port map)
The OptiPlex runs several containers already. Host ports in use:

| Host port | Service |
|---|---|
| 80 | `cloudflared` (the tunnel connector) |
| 8080 | `gp_signage_webapp` |
| 8081 | `gmphan_webapp` |
| **8082** | **`gp-battleship` (this app)** — `docker-compose.yml` maps `8082:8080` |
| 5432 | `local_postgres` |
| 9001 / 9443 | `portainer` |

The app container always listens on **`8080` internally** (`ASPNETCORE_URLS=http://+:8080`); only the **host** port is `8082`. The tunnel connects to the host at **`http://localhost:8082`**.

### Step 1 — Run the app on the OptiPlex
The OptiPlex holds only the compose file + env file (no source, no Dockerfile) and **pulls the prebuilt image** from GHCR. Build+push happened on your dev machine via `./build-and-publish.sh`.

```bash
# on the OptiPlex
mkdir -p ~/battleship && cd ~/battleship
# copy BOTH files here, side-by-side (compose's env_file is ./variables.env):
#   stack/docker-compose.yml   →  ./docker-compose.yml
#   stack/variables.env        →  ./variables.env   (real secrets: DB conn + auth)

# EDIT variables.env: set the DB connection + SuperAdmin password, e.g.
#   ConnectionStrings__DefaultConnection=Host=<db-host>;Port=5432;Database=gp_battleship;Username=...;Password=...
#   Auth__InitialAdmin__Password=...
# EDIT docker-compose.yml if needed: PUBLIC_BASE_URL to your real HTTPS hostname

# if the GHCR package is private, log in once:
# echo $CR_PAT | docker login ghcr.io -u gmphan --password-stdin

docker compose pull       # download the latest image
docker compose up -d      # start it
```

Verify locally **on the OptiPlex** before touching Cloudflare:
```bash
curl -I  http://localhost:8082/            # expect: HTTP/1.1 302 (redirect to /index.html)
curl -s  http://localhost:8082/api/config  # expect: {"publicBaseUrl":"https://battleship.yourdomain.com"}
docker logs gp-battleship --tail 50        # errors, if any
```
At this point it's reachable on the **LAN** at `http://<optiplex-ip>:8082`. The next step makes it public with HTTPS.

### Step 2 — Publish it through the Cloudflare Tunnel
Pick **one** of the two ways you drive `cloudflared`. Use whatever matches how your other apps (signage 8080, webapp 8081) are already wired.

#### Option A — Zero Trust dashboard (recommended)
1. **Cloudflare Zero Trust** → **Networks** → **Tunnels** → select your tunnel → **Public Hostname** tab → **Add a public hostname**.
2. Fill in:
   - **Subdomain:** `battleship`
   - **Domain:** `yourdomain.com`
   - **Path:** *(leave blank)*
   - **Service → Type:** `HTTP`
   - **Service → URL:** `localhost:8082`
3. **Save.** Cloudflare auto-creates the DNS `CNAME` and provisions the TLS cert.

> If your `cloudflared` container runs in **bridge** networking (not host), `localhost` points at the connector container itself — then use **`http://host.docker.internal:8082`** as the service URL instead. Match whatever the other working apps use.

#### Option B — `config.yml` (file-based ingress)
Add an ingress rule **above** the catch-all `- service: http_status:404` (the catch-all must stay last):

```yaml
tunnel: <your-tunnel-id>
credentials-file: /etc/cloudflared/<your-tunnel-id>.json

ingress:
  # ... your existing hostname rules (signage, webapp, etc.) ...
  - hostname: battleship.yourdomain.com
    service: http://localhost:8082
  - service: http_status:404          # catch-all MUST be last
```

Create the DNS route once, then reload the connector:
```bash
cloudflared tunnel route dns <your-tunnel-name> battleship.yourdomain.com
docker restart cloudflared
```

### Step 3 — Verify end-to-end
1. **From a phone on cellular** (off your LAN), open `https://battleship.yourdomain.com` → you should see the **admin console**.
2. Click **Create Game** → a card appears with a QR + room code.
3. **Scan the QR** → it must open `https://battleship.yourdomain.com/play.html?room=XXXXX`.
   - If it opens `localhost` or the wrong host, `PUBLIC_BASE_URL` is wrong → fix it and re-run `docker compose up -d`.
4. Join from **two devices**, place fleets, battle.

Connectivity checks if something's off:
```bash
curl -sI https://battleship.yourdomain.com/ | head -1   # expect 302 or 200
docker logs cloudflared --tail 50                       # tunnel route/ingress errors
```

### Cloudflare troubleshooting

| Symptom | Cause / Fix |
|---|---|
| `bind: address already in use` on deploy | Another service holds 8082. Change the left side of `"8082:8080"` and update the tunnel target to match. |
| **502 / 1033** from Cloudflare | Tunnel target port wrong, or the container is down. Check `docker compose ps`, `docker logs gp-battleship`, and that the tunnel points at `localhost:8082` (or `host.docker.internal:8082`). |
| QR opens `localhost` / wrong URL | `PUBLIC_BASE_URL` not set to the real HTTPS hostname, or not restarted after change. |
| Works on LAN but not publicly | DNS route / ingress rule missing, or the connector is down. Re-save the public hostname (Option A) or run `cloudflared tunnel route dns …` (Option B). |
| Realtime (SignalR) flaky | The default tunnel passes WebSocket upgrades; if you added Cloudflare rules/WAF, ensure WebSockets aren't blocked for this hostname. |
| `PUBLIC_BASE_URL` still shows `https://battleship.example.com` | You forgot to edit it before deploy. Edit + `docker compose up -d`. |

---

## 6. Configuration

| Setting | Where | Purpose |
|---|---|---|
| `PUBLIC_BASE_URL` | env var or `appsettings.json` | Public base URL used to build the QR join link and shown on the admin page. Empty → falls back to the request's own scheme/host (fine for localhost). |
| `ASPNETCORE_URLS` | env / Dockerfile | Listening address. Docker sets `http://+:8080`; local dev uses `http://localhost:5184`. |
| `ASPNETCORE_ENVIRONMENT` | env | `Development` (local) vs `Production` (Docker). Affects logging/dev exception page and `Database:FailFast` default. |
| `ConnectionStrings:DefaultConnection` | env file | Postgres connection for admins. **Required.** |
| `Auth:*` | env file | Config admins — see §6.1. |
| `Database:MigrateOnStartup` | env / appsettings | `true` (default) runs EF migrations at boot; `false` skips. |
| `Database:FailFast` | env / appsettings | `true` → a migration failure crashes startup. Default: **true in Production, false otherwise** (dev keeps running and logs the fix). |

`Program.cs` reads `PUBLIC_BASE_URL` into a singleton `PublicUrlOptions` used by `/api/qr` and `/api/config`.

### 6.0 How configuration & env files resolve (read before adding a setting)
- **Sources (later wins):** `appsettings.json` → `appsettings.{Environment}.json` → environment variables → command line. Env-var form uses **`__`** for `:` (e.g. `ConnectionStrings__DefaultConnection`, `Auth__InitialAdmin__Password`).
- **Env files** (`variables.<Env>.env`) are **only** loaded by the **F5/launch** flow (`envFile` in `.vscode/launch.json`) and by Docker (`env_file` in `stack/docker-compose.yml`). `dotnet run` on the CLI does **not** read them automatically.
- **`.env` parsing pitfalls** (these have actually bitten us):
  - **`source variables.X.env` in a shell breaks** on the connection string — bash/zsh split unquoted `;`, so `ConnectionStrings__DefaultConnection` becomes only `Host=localhost`. Prefer F5, or `export ConnectionStrings__DefaultConnection="$(grep ... | cut -d= -f2-)"`.
  - **Values are NOT trimmed** by the .NET env loader. Trailing spaces (e.g. `Role=SuperAdmin    `) survive. `AuthOptions` trims username/role defensively; **passwords are kept verbatim** (spaces may be intentional).
  - **Inline `# comments` are not stripped** either — keep comments on their own line.
- **Adding a new setting:** read it via `builder.Configuration["Section:Key"]` (or bind an options class), document it in this table, and (if it belongs in dev config) add it to `variables.example.env`.

### 6.1 Authentication & admin management

**Admins are real accounts, authenticated with a cookie session.** Players stay anonymous (QR scan).

- **Two kinds of admins, merged into one list:**
  - **Config admins** — declared in config/env (`Auth:InitialAdmin:*`, `Auth:SeedAdmins:*`). They **always exist**, are **read-only** (can't be edited/deleted in the UI/API), and are **authoritative** (a DB admin can't take their username).
  - **DB admins** — created at runtime by a **SuperAdmin** in the *Admins* panel (`/api/admins`). Stored in Postgres (`admin_users`), hashed with PBKDF2.
- **Roles:** `SuperAdmin` and `Admin`. Only `SuperAdmin` may hit `/api/admins/*`. **Both** roles can host games (host methods require just `[Authorize]`).
- **Passwords:** PBKDF2-SHA256, stored as `iterations.saltB64.hashB64` (see `Auth/PasswordHasher.cs`). DB stores only hashes; config passwords live only in env.
- **Cookie:** `gp_battleship_auth`, HttpOnly, SameSite=Lax, `SameAsRequest` secure (so it works on LAN http and HTTPS behind Cloudflare). 12h sliding. **The same cookie authorizes the SignalR hub** for host methods.

**Config keys (env form):**

| Config key | Env (`variables.env`) | Meaning |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `ConnectionStrings__DefaultConnection` | Postgres connection (admins). |
| `Auth:InitialAdmin:Username` | `Auth__InitialAdmin__Username` | Default `SuperAdmin`. |
| `Auth:InitialAdmin:Password` | `Auth__InitialAdmin__Password` | **Required** to have a SuperAdmin account. |
| `Auth:InitialAdmin:Role` | `Auth__InitialAdmin__Role` | Default `SuperAdmin`. |
| `Auth:SeedAdmins:N:Username/Password/Role` | `Auth__SeedAdmins__N__Username` … | Extra config admins (any role). |

**Auth endpoints** (`Endpoints/AuthEndpoints.cs`):

| Route | Auth | Returns |
|---|---|---|
| `POST /api/auth/login` | anon | Sets cookie; returns `{ username, displayName, role, source }` or `401`. |
| `POST /api/auth/logout` | anon | Clears cookie (`204`). |
| `GET /api/auth/me` | required | Current admin, or `401`. |

**Admin-management endpoints** (`Endpoints/AdminEndpoints.cs`, **SuperAdmin only** → `403` otherwise):

| Route | Returns |
|---|---|
| `GET /api/admins` | Merged list of config + DB admins (config items have `isConfig:true`, `id:null`). |
| `POST /api/admins` | Create a DB admin (`409` if username taken/reserved, `400` if invalid). |
| `PUT /api/admins/{id}` | Update a DB admin's `role`/`isActive`/`displayName`. |
| `DELETE /api/admins/{id}` | Delete a DB admin (`204`). |

**Startup** (`Data/StartupTasks.cs`): applies EF migrations (`admin_users`) on boot (unless `Database:MigrateOnStartup=false`); config admins are resolved from configuration (no seeding needed). If migrations fail, it logs an actionable message — including the exact `CREATE DATABASE ... OWNER ...` fix for the common `42501 permission denied to create database` — and **keeps running in Development** (`Database:FailFast` defaults to true only in Production). **Apply migrations manually:** `cd src/GpBattleshipWebapp && dotnet ef database update` (design-time uses `AppDbContextFactory`'s placeholder; runtime uses `DefaultConnection`).

**Where the admins come from locally:** the F5 **`Development.local`** profile → `variables.Development.local.env` (your DB conn + `Auth__InitialAdmin__*`). The `Development`/`QA`/`Production` env files are **templates** point-at-`postgres` placeholders that won't work on a Homebrew Postgres (superuser is your macOS user). See §4.1.

---

## 7. The data model

All **game** models are nested classes at the bottom of `BattleshipHub.cs` and are **in-memory only**. (Admin accounts are a separate Postgres table — see §6.1.)

### Static dictionaries (the whole in-memory game state)
```csharp
Rooms           : ConcurrentDictionary<string, GameRoom>   // roomCode → game
ConnectionRooms : ConcurrentDictionary<string, string>     // player connectionId → roomCode
HostGames       : ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>
                                                           // host USERNAME → set of roomCodes it owns
```
- A **player** connection maps to **exactly one** room (`ConnectionRooms`).
- A **host** is an **authenticated admin**; its rooms are keyed by the admin's **username** (taken from the auth cookie), so ownership survives reconnects and a host can only manage games it owns. `ResumeHost()` re-binds owned rooms to the current connection after a reload.

### `GameRoom`
| Field | Meaning |
|---|---|
| `Code` | 5-char room code (alphabet excludes ambiguous chars). |
| `HostConnectionId` | The admin connection **currently** bound to this game (re-pointed on host reconnect; `null` while the host is disconnected). |
| `HostToken` | The **owner username** of the host (normalized), used to authorize host actions across reconnects. |
| `ShootAgainOnHit` | Turn rule: if `true`, a hit keeps the shooter's turn. |
| `Players` | `List<Player>` — 0–2 seats, in join order (index 0 = P1). |
| `Started` | Battle underway? |
| `CurrentTurnId` | ConnectionId whose turn it is (`null` when not started). |
| `OpponentOf(Player me)` | Returns the other player. |

### `Player`
| Field | Meaning |
|---|---|
| `ConnectionId` | Current SignalR connection (changes on reconnect). |
| `Name` | Sanitized display name (≤20 chars). |
| `ReconnectToken` | Stable token issued on join; used to re-claim the seat. **Also the seat identity** (`OpponentOf` compares by token). |
| `Connected` | `false` while a seat is held during a disconnect grace window. |
| `DisconnectedAt` | When they dropped (for the grace window). |
| `Ships` | `List<ShipPlacement>?` — `null` until placed. Non-null == "ready". |
| `ShipCells` | `HashSet<(int,int)>` — quick lookup of all occupied cells (for hit tests). |
| `ShotsAtOpponent` | `HashSet<string>` of `"x,y"` this player has fired. |
| `HitsOnOpponent` | `HashSet<string>` of `"x,y"` that were hits. |
| `ResetGame()` | Clears fleet+shots for a rematch, keeps seat identity. |

### Other DTOs
- `ShipPlacement { string Name; List<Cell> Cells; }`
- `Cell { int X; int Y; }`
- `ShotDto { int X; int Y; bool Hit; }`
- `ReconnectState` — full snapshot sent to a reconnecting player (see §9).

### Coordinates
- Board is **10×10**. `X` = **column** (0–9 → labels **1–10**), `Y` = **row** (0–9 → labels **A–J**).
- Human cell name helper (client & concept): `cellName(x,y) = String.fromCharCode(65 + y) + (x + 1)` → e.g. `(6,4)` = **"G5"**.
- Shots are keyed by the string `"x,y"`.

### Fleet (hard-coded)
```csharp
Carrier=5, Battleship=4, Cruiser=3, Submarine=3, Destroyer=2   // 5 ships, 17 cells total
```
`FleetSpec` is sent to clients as `"Carrier:5,Battleship:4,Cruiser:3,Submarine:3,Destroyer:2"`.

---

## 8. The SignalR hub — server API (complete)

Hub class: `GpBattleshipWebapp.Hubs.BattleshipHub`, mapped at **`/hub/battleship`**.
These are methods the **client invokes** (client → server). All `async Task` unless noted.

| Method | Params | Who calls | What it does |
|---|---|---|---|
| `CreateGame` | `string hostName, bool shootAgainOnHit` | Host (**auth required**) | Creates a room owned by the logged-in admin (username from cookie), adds host to its group, replies `GameCreated`. **Call repeatedly to host multiple games.** |
| `ResumeHost` | — | Host (**auth required**) | Re-claims all rooms owned by the logged-in admin after a reconnect/reload (re-binds `HostConnectionId` + rejoins groups), then replies `GamesList`. Called by `host.js` on every connect/reconnect. |
| `ListGames` | — | Host (**auth required**) | Replies `GamesList` with all rooms the current admin owns (dashboard restore). |
| `JoinGame` | `string code, string playerName` | Player (anonymous) | Validates room exists & has <2 players; issues `ReconnectToken`; adds to room group; replies `GameJoined` + `LobbyUpdated`. |
| `Reconnect` | `string code, string token` | Player (anonymous) | Re-claims a held seat by token, rebinds to the new connection, replies `Reconnected` (full state) plus lobby/pause updates. |
| `AbortGame` | `string code` | Host (**auth required**) | Only the room's owner. Broadcasts `GameAborted` to the room, detaches players, removes room, replies `GameRemoved` to host. |
| `NewGame` | `string code` | Host (**auth required**) | Rematch: resets `Started`, `CurrentTurnId`, and every player's fleet/shots; broadcasts `NewGameStarted`. Same room/QR. |
| `PlaceShips` | `List<ShipPlacement> ships` | Player | Validates placement (exact fleet, in-bounds, no overlap, straight & contiguous); stores fleet; when both players are ready → starts game and broadcasts `GameStarted`. |
| `Fire` | `int x, int y` | Player | The core turn action — see §11. Broadcasts `ShotResult`, then `GameOver` **or** `TurnChanged`. |
| `OnDisconnectedAsync` (override) | — | framework | Handles host vs player disconnect (§14). |

**Validation that exists today (mirror this style when adding actions):**
- Turn checks: game started, not paused (both connected), it's your turn.
- `Fire`: in-bounds, not already fired there.
- `PlaceShips`: `ValidatePlacement(...)`.

**Host/ownership checks:** `IsHostOf(code)` (owner == current admin's username) gates `AbortGame` and `NewGame`. Host methods carry `[Authorize]`; player methods are anonymous.
**Player/room resolution:** `TryGetRoomAndPlayer(out room, out player)` for player-scoped actions.

---

## 9. Client events — server → client (complete)

These are `connection.on('EventName', ...)` on the client. Group messages go to everyone in the room; `Clients.Caller`/`Clients.Client` go to a specific connection.

| Event | Payload | Sent to | Meaning / client action |
|---|---|---|---|
| `GameCreated` | `code, hostConnectionId, fleetSpec, shootAgainOnHit, hostToken` | caller (host) | Add a dashboard card (`hostToken` is the owner username). |
| `GamesList` | `[{ code, shootAgainOnHit, started, players[] }]` | caller (host) | Restore dashboard. |
| `GameUpdated` | `code, started, players[]` | host (`Clients.Client`) | Authoritative per-card refresh (players join/ready/state change). |
| `GameJoined` | `code, connectionId, fleetSpec, reconnectToken, shootAgainOnHit` | caller (player) | Store token, go to placement. |
| `GameStarted` | `currentTurnId` | room group | Switch player to battle screen; build boards. |
| `LobbyUpdated` | `[{ id, name, ready, connected }]` | room group | Update player chips. |
| `ShipsPlaced` | — | caller (player) | Ack placement. |
| `ShotResult` | `byConnectionId, x, y, hit, sunkShipName?, opponentDefeated` | room group | Animate + log the shot. `sunk` is non-null **only** on the shot that finishes a ship. |
| `TurnChanged` | `currentTurnId, keepTurn` | room group | Update whose turn it is; `keepTurn` true when a hit granted another shot. |
| `GameOver` | `winnerConnectionId` | room group | End state / victory effects. |
| `PauseState` | `bool paused` | room group | Pause banner while an opponent reconnects. |
| `OpponentDisconnected` | `name, graceSeconds` | room group | Starts a live reconnect countdown in the banner. |
| `OpponentLeft` | — | room group | Seat expired. |
| `Reconnected` | `ReconnectState` | caller (player) | Full state snapshot to resume. |
| `ReconnectFailed` | `string reason` | caller (player) | Seat gone → fall back to join. |
| `GameAborted` | `string reason` | room group | Host explicitly aborted the game → players end. (A host *disconnect* no longer aborts; rooms persist and the host can `ResumeHost`.) |
| `GameClosed` | — | room group | Host closed (legacy path). |
| `NewGameStarted` | `shootAgainOnHit` | room group | Rematch → players reset and re-place. |
| `GameRemoved` | `code` | caller (host) | Remove that dashboard card. |
| `Error` | `string message` | caller | Toast an error message. |

> **Naming convention:** server methods are `PascalCase` verbs (`Fire`, `PlaceShips`); events are `PascalCase` nouns/participles (`ShotResult`). Match this.

---

## 10. Client apps: admin console & player

### Admin console (`index.html` + `host.js`)
- **One SignalR connection** that can own **many rooms**.
- A **grid of `.game-card`s**, one per game: room code, QR (`/api/qr/{code}`), join link, live player chips, status pill (Lobby / In progress / Finished), a per-game mini log, and **🔄 New Game** + **⛔ Abort** buttons.
- Local maps: `games` (code → model) and `cards` (code → element refs).
- "Create Game" **adds** a card; never replaces. `GameUpdated` is the authoritative per-card refresh.
- **Login required.** The console is gated by a cookie session (`/api/auth/login`). On load, `host.js` calls `/api/auth/me`; if not signed in it shows a login overlay. The admin's **username** (from the cookie) is the host identity, so a SuperAdmin/Admin can only manage the games it created. On every connect/reconnect it calls `ResumeHost()` (no args) to re-claim its rooms. **SuperAdmins** additionally see an **Admins** panel to create/disable/delete DB admins and toggle roles (see §6.1).

### Player (`play.html` + `player.js`)
- Keyed by `?room=CODE`. One connection per tab.
- **Persistence:** `localStorage['bs_token_' + room]` stores the reconnect token so a page reload resumes the seat (`Reconnect`) instead of re-joining.
- Screens (toggled by `.hidden`): `joinPanel` → `placePanel` → `battlePanel`.
- **Placement:** tap ship to rotate (H/V), tap a cell to place; tap a placed ship to remove. Ships are removed and re-placed via the `own` map (`"x,y" → shipName`).
- **Battle:** fire on `enemyGrid`; own ships show on `ownGrid`. Turn/disabled states managed by `myTurn` + `enemyGrid.classList.toggle('disabled', ...)`.
- **Reconnect:** `onreconnecting` → banner; `onreconnected` → `tryReconnect(token)`; server replies `Reconnected` → `restoreState(...)`. Automatic reconnect retries forever (capped backoff) and an `onclose` handler keeps re-establishing, so an idle/backgrounded phone isn't dropped.
- **Pause countdown:** on `OpponentDisconnected(name, graceSeconds)` the banner shows a live `mm:ss` countdown (`startPauseCountdown`/`renderPauseBanner`); it clears on `PauseState(false)`, `TurnChanged`, game end, or rematch.

### Sound (`sfx.js`)
- `window.SFX.play(name)` with keys: `fire, miss, hit, sunk, turn, win, lose, place`. Mute persisted in `localStorage['bs_muted']`. AudioContext unlocked on first user gesture.

---

## 11. Game rules & turn logic

`Fire(x, y)` is the heart of the game. Flow (see `BattleshipHub.cs`):

1. **Guards:** game started, both players connected (not paused), it's your turn, in-bounds, cell not already fired.
2. Record the shot on `player.ShotsAtOpponent`; compute `hit = opponent.ShipCells.Contains((x,y))`.
3. **Sink detection (important):** a ship is reported `sunk` **only on the shot that lands its last cell** — i.e. *this shot's cell belongs to the ship* **AND** *all the ship's cells are now hit*. (Earlier bug: it re-reported already-sunk ships; do not regress this.)
4. `opponentDefeated` = **all** opponent ship cells are hit → `GameOver`, `Started=false`.
5. Broadcast `ShotResult`.
6. **Turn rule:** `keepTurn = ShootAgainOnHit && hit`; if not keeping, `CurrentTurnId` flips to opponent. Broadcast `TurnChanged(currentTurnId, keepTurn)`.

**Starting:** when both players have `Ships != null`, `Started=true`, `CurrentTurnId = Players[0]`, broadcast `GameStarted`.

---

## 12. Rendering the boards (ships, hits, coordinates)

All in `player.js` + `site.css`.

### Hulls (graphical, on the OWNER board)
- `layoutShips(gridEl)` decorates each occupied cell based on the `own` map:
  - `conn-n/s/e/w` — has a same-ship neighbor on that side.
  - `ship-bow` (degree 1) / `ship-mid` (degree ≥2), plus `orient-h`/`orient-v`.
  - `ship-<name>` color class (e.g. `ship-carrier`).
- CSS fuses cells: each hull piece paints a `::before`, and **directional `box-shadow`s extend it exactly 3px (the grid gap)** into neighbors so a ship reads as one continuous hull with rounded ends and a deck line.
- **Per-ship colors** are defined on the bare `.ship-<name>` class so both board cells and placement chips pick up `--ship-color`:
  `carrier #f59e0b` (amber), `battleship #22c55e` (green), `cruiser #a855f7` (violet), `submarine #06b6d4` (cyan), `destroyer #ec4899` (pink).
- A hit cell gets `ship-hit` → red damaged hull (hatch pattern); `layoutShips` preserves shot-state classes.

### Shots
- `animateShot(grid, x, y, hit, incoming)` adds `hit`/`miss`/`ship-hit`, a splash `impact` overlay, a `marker` emoji (💥 / •), plus screen shake on incoming hits.
- `paintStatic(...)` is used when restoring shots from a `Reconnected` snapshot.

### Coordinates (A–J rows, 1–10 columns)
- `addBoardLabels(gridEl)` wraps the `.grid` in a `.board-frame` and injects `.board-cols` (1–10) and `.board-rows` (A–J) strips + a corner. Idempotent (`data-labeled="1"`). Called once at boot for all three grids.
- Label strips use the **same 32px cells + 3px gap + 6px padding** as the grid so they align exactly.

### Grid geometry (keep in sync)
`.grid` is a `repeat(10,1fr)` CSS grid, cells **32×32**, `gap: 3px`, `padding: 6px`. The hull gap-fill (`--gap: 3px`) and label strips depend on these numbers — if you change cell size/gap, update all of: `.cell{width/height}`, `.grid{gap/padding}`, `--gap`, `--bx-cell/--bx-gap/--bx-pad`, and the `.board-cols`/`.board-rows` templates.

---

## 13. HTTP endpoints

Defined in `Program.cs` (minimal APIs):

| Route | Returns |
|---|---|
| `GET /` | Redirect to `/index.html`. |
| `GET /api/qr/{code}` | PNG QR encoding `{PUBLIC_BASE_URL}/play.html?room=CODE`. 400 if code length 0 or >12. |
| `GET /api/config` | `{ publicBaseUrl }` — used by the admin page to show the join link. |
| `POST /api/auth/login` · `/logout` · `GET /api/auth/me` | Cookie session (see §6.1). |
| `GET/POST /api/admins` · `PUT/DELETE /api/admins/{id}` | SuperAdmin-only admin management (see §6.1). |
| `MapHub /hub/battleship` | The SignalR hub (players anonymous; host methods `[Authorize]`). |
| static | Everything in `wwwroot/` via `UseStaticFiles`/`UseDefaultFiles`. |

---

## 14. Concurrency & lifecycle rules

- **Single container assumption.** State is static in-process; do not scale out horizontally without an external store/backplane.
- **Host identity = authenticated username** (from the auth cookie), *not* the connection id. A host disconnect does **not** tear rooms down — it only clears the room's live host connection; players keep playing. When the host page reloads/reconnects it calls `ResumeHost()` to re-bind its rooms (new connection id + rejoin groups). Only an explicit `AbortGame` removes a game.
- **Player disconnect** → seat held for `ReconnectGrace` (**30 min**) with `Connected=false`; if a game was running it **pauses** (`PauseState true` + `OpponentDisconnected(name, 1800)`, which the client renders as a live countdown). An `ExpirePlayerAsync` task runs after the grace window: if still disconnected, the seat is removed (game un-starts, or room dropped if empty).
- **Player client resilience:** `withAutomaticReconnect` retries **forever** (capped backoff), a ~15s keep-alive / 60s server-timeout is set, and an `onclose` handler keeps re-establishing + re-claiming the seat — so an idle/backgrounded phone isn't dropped within the grace window.
- **All players disconnected** → room removed.
- **Reconnect identity** = `ReconnectToken` (players) / authenticated username (host), never the connection id. Seats & host ownership rebind to new connection ids.
- Operations are `async` and use `ConcurrentDictionary`; group broadcasts use `Clients.Group(room.Code)`.

---

## 15. How to add a feature (recipes)

Below are the common extension points, end-to-end (server + both clients). **Always: add the server method/state first, then wire the clients, then test with two tabs.**

### A. New ship / change the fleet
1. Edit the `Fleet` dictionary in `BattleshipHub.cs` (name → length). `FleetSpec` and `ValidatePlacement` derive from it automatically.
2. Add a color class `.ship-<newname> { --ship-color: … }` in `site.css` (slug = lowercase, non-alphanumerics stripped — see `shipSlug` in `player.js`).
3. Nothing else — placement UI is data-driven from `fleetSpec`.

### B. New turn rule or game option (e.g. "salvo mode", "no repeat fire")
1. Add a field to `GameRoom` (e.g. `SalvoEnabled`).
2. Thread it through `CreateGame(...)` (extra param) and the `GameCreated` / `GameJoined` / `Reconnected` payloads.
3. Enforce it inside `Fire(...)`. Adjust turn logic there.
4. Surface the toggle in `index.html`/`host.js` (create controls) and read the flag in `player.js`.

### C. New player action (e.g. "surrender", "chat", "reveal")
1. Add a hub method `public async Task MyAction(...)` that: resolves room+player (`TryGetRoomAndPlayer`), validates, mutates state, and broadcasts a **new event** to `Clients.Group(room.Code)`, plus `NotifyLobbyAndHost(room)` if the host dashboard should update.
2. Add a handler `connection.on('MyEvent', ...)` in `player.js` (and `host.js` if relevant).
3. Add a button/UI in the appropriate page + CSS.
4. Keep the **server authoritative** — never trust client-sent state.

### D. New admin control (e.g. "pause all", "reset scores")
1. Add hub method gated by `IsHostOf(code)`.
2. Emit a `GameUpdated` (or a new event) and a host-facing event carrying `code` so `host.js` can update/remove the right card.
3. Add a button to the `.game-card` in `host.js` (`ensureCard`/`updateCard`).

### E. Change board size (use sparingly)
- `BoardSize` is `const 10` in the hub; the client `BOARD` const in `player.js` and `buildGrid`/`layoutShips`/`addBoardLabels` assume 10.
- Update CSS grid templates, `repeat(10, …)` in labels, and cell sizing. Letter labels (A–J) only go to J at 10.

### F. Change the QR / join base URL behavior
- See `Program.cs` `/api/qr` and `PublicUrlOptions`. Set `PUBLIC_BASE_URL`.

### G. Add/change a DB entity or column (EF migration)
1. Edit the model in `Models/` and its `Data/Configurations/*Configuration.cs` (fluent config + `ToTable`).
2. Add `DbSet<T>` to `AppDbContext` if it's a new entity.
3. Generate a migration:
   ```bash
   cd src/GpBattleshipWebapp
   dotnet ef migrations add <Name> --output-dir Data/Migrations
   ```
   (The design-time `AppDbContextFactory` supplies a placeholder connection string; no DB needed to scaffold.)
4. Review the generated migration, then apply it by **just running the app** (auto-applies at startup) or `dotnet ef database update`.
5. If you add a repository, follow the `GenericRepository<T>` / `IAdminRepository` pattern (§16) and register it in `DbInjector`.

### H. Add a new configuration setting or F5 environment
1. Read it via `builder.Configuration["Section:Key"]` (or bind an options class in `DbInjector`/`Configure<>`).
2. Document it in the §6 table.
3. Add a **non-secret** default/key to `variables.example.env` so the next person sees it.
4. For a new **F5 environment**: add the name to `inputs[environment].options` in `.vscode/launch.json` **and** create `variables.<Name>.env`. Keep `ASPNETCORE_ENVIRONMENT` / `ASPNETCORE_URLS` in that file (the launch config no longer sets them).
5. For Docker: add it to `stack/variables.env` (server-side) and, if non-secret, to the `environment:` block of `stack/docker-compose.yml`.

### Z. "Where do I put this?" — quick map
| I'm adding… | File(s) |
|---|---|
| A game rule / new hub action / state | `Hubs/BattleshipHub.cs` (server), then `wwwroot/js/player.js` + `host.js` |
| A new HTTP endpoint (JSON/file) | `Endpoints/*Endpoints.cs` (map in `Program.cs`, or add a new `MapXxxEndpoints`) |
| A new admin/DB entity | `Models/` + `Data/Configurations/` + `AppDbContext` + migration (§15.G) |
| A repository / data access | `Data/Repositories/` (extend `GenericRepository<T>`), register in `Injectors/DbInjector.cs` |
| A DI service / option | `Injectors/*Injector.cs` (implement `IInjector`; auto-discovered) |
| A config setting / env var | read in `Program.cs` or an options class; document §6; template `variables.example.env` |
| Login / auth policy / cookie | `Auth/` (`AuthSetup`, `AuthOptions`, `AuthConstants`) |
| Admin-management UI | `wwwroot/index.html` + `wwwroot/js/host.js` (+ `auth.js` helpers) |
| Styling / board rendering | `wwwroot/css/site.css` + `wwwroot/js/player.js` |
| A migration | `dotnet ef migrations add <Name> --output-dir Data/Migrations` (§15.G) |

---

## 16. Conventions & gotchas

- **Server is authoritative.** Clients are dumb views; all validation/state lives in the hub.
- **Seat identity = `ReconnectToken`.** Don't compare players by `ConnectionId` for identity (it changes on reconnect); use it only for routing/turns.
- **Host identity = authenticated username** (cookie), never the connection id.
- **Sink detection** must remain "this shot completed the ship" — see §11/§3 bug note.
- **`GameUpdated`** (host, `Clients.Client`) is the authoritative per-card refresh; group events like `ShotResult` are matched to the right card by scanning `games` for the shooter's id.
- **Idempotent rendering helpers**: `addBoardLabels` guards with `data-labeled`; `layoutShips` resets hull classes each call but preserves `ship-hit/hit/miss`.
- **CSS class slugs**: `shipSlug(name)` must match your `.ship-<slug>` color rules (`Battleship` → `ship-battleship`).
- **Rebuild paths**: `buildGrid` clears `innerHTML`; the `.board-frame` wrapper and labels persist because they live *around* the grid element, which is reused. Don't replace the grid element itself without re-running `addBoardLabels`.
- **localStorage keys**: `bs_token_<ROOM>` (player resume), `bs_muted` (sound).
- **Build config**: `net10.0`, nullable + implicit usings enabled; NuGet deps: `QRCoder 1.6.0`, `Microsoft.EntityFrameworkCore* 10.0.0`, `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0`.
- **Build/verify before shipping:** `dotnet build -c Release` (expect 0 warnings/errors) and `node --check` on edited JS; then `./build-and-publish.sh` to build+push the image. On the server: `docker compose -f stack/docker-compose.yml pull && docker compose -f stack/docker-compose.yml up -d`.
- **DB is required for login but optional for gameplay.** Only admin auth + admin management touch Postgres; players connect anonymously. A migration failure won't stop players joining an *existing* room in memory, but nobody can log in to host new games.
- **`CREATE DATABASE` is the app's job only when it can.** The app creates *tables* (`MigrateAsync`), never the *database* unless the login has `CREATEDB`. Provision the DB as its owner once (§4.1). The `42501` error means exactly this.
- **Env files are per-machine and gitignored** (`variables*.env`). Never commit real secrets. Templates live in `variables.example.env`; server secrets in `stack/variables.env`.
- **`.env` values are not trimmed and inline `#` isn't stripped** — keep values clean, comments on their own lines (§6.0).
- **Config admins are read-only.** Password/role changes → edit the env file and restart. DB admins are managed at runtime via `/api/admins` (SuperAdmin only).
- **Adding DI**: implement `IInjector`, it's auto-discovered by `InjectServicesFromAssemblies` — no `Program.cs` edit needed. Put registrations in `Injectors/*Injector.cs`.

---

## 17. Known limitations / future ideas

- **No persistence** — server restart loses all games (by design). Persist to Redis/DB if you need durability or horizontal scale (SignalR backplane).
- **Two players only**, no matchmaking/queue; join is via shared QR.
- **No spectators** (besides the host log), no chat.
- **Bow/stern direction is not tracked**, so both free ends of a hull render with symmetric rounded caps.
- **Reconnect grace is 30 minutes**; a seat expires (and the game ends) only if a player stays disconnected that long. There's a live countdown, but no "give up now" button.
- **Admins are authenticated** (cookie login). Anyone with a room code can still *join* a game as a player — that's by design (players scan a QR). Only the **host/admin console** and the **admin-management API** are protected. Config admins (from env) are read-only; DB admins are created/edited by a SuperAdmin.
- Possible additions: sound themes, animations for full-fleet reveal at game over, ELO/scoreboard (needs persistence), configurable fleet, spectator mode, mobile layout tweaks.

---

_Last whole-app review: ships as connected color-coded hulls, A–J/1–10 coordinate labels, multi-game admin console with abort/rematch, per-ship colors. Resilience pass: 30-minute reconnect grace + live pause countdown, and forever-retry client reconnects (fixing idle "timeout"). Fixed "always says sunk" sink-detection bug. **Auth pass:** admin console now requires cookie login; hosts are identified by authenticated username (config or DB admins, `SuperAdmin`/`Admin` roles); SuperAdmins manage DB admins via `/api/admins`; Postgres-backed `admin_users` (EF migrations auto-applied); DI organized via `Injectors/`. **Docs + DX pass:** F5 "Select Environment" picker over `variables.<Env>.env`; DB provisioning steps + `42501` handling (`Database:MigrateOnStartup`/`FailFast`); env-file parsing gotchas; §15.G/H/Z recipes and the "where do I put this?" map._

> **When you add a feature later**, update this README in the same PR: the §15 recipe you followed, any new config key (§6), new hub method/event (§8/§9), new endpoint (§13), and SQL/DB changes (§7/§15.G). The checklist in §16 ("Build/verify before shipping") is the minimum gate.
