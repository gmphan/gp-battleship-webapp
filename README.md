# ⚓ GP Battleship Webapp

A real-time, multiplayer **Battleship** game for the browser, built with **ASP.NET Core (.NET 10) + SignalR**. Phone-friendly: players scan a QR code to join, two players per game, and an **admin console** can run *many* concurrent games at once. Ships are drawn as connected, color-coded hulls, and every move is broadcast live.

> **Read me first if you're adding a feature.** This document is written to be the single source of truth for *what the app is, how it works, and where everything lives*, so you (or an AI assistant) can orient in minutes and extend it safely.

---

## Table of contents
1. [What the app does](#1-what-the-app-does)
2. [High-level architecture](#2-high-level-architecture)
3. [Project layout](#3-project-layout)
4. [Running locally](#4-running-locally)
5. [Running with Docker / production](#5-running-with-docker--production)
6. [Configuration](#6-configuration)
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
┌──────────────────────────┐        SignalR (WebSockets)         ┌─────────────────────────────┐
│  Admin console           │  ───────────────────────────────►   │                             │
│  index.html + host.js    │  CreateGame / AbortGame / NewGame   │   BattleshipHub (server)    │
│  (one connection,        │  ◄───────────────────────────────   │   in-memory state           │
│   many rooms)            │  GameCreated / GameUpdated / ...    │   Rooms / HostGames /       │
└──────────────────────────┘                                      │   ConnectionRooms           │
                                                                  │                             │
┌──────────────────────────┐        SignalR (WebSockets)         │   • GameRoom (one per game) │
│  Player                  │  ───────────────────────────────►   │   • Player (seat, fleet,    │
│  play.html + player.js   │  JoinGame / PlaceShips / Fire / ... │     shots, reconnect token) │
│  (one connection,        │  ◄───────────────────────────────   │                             │
│   one room)              │  GameJoined / GameStarted / ...     └─────────────────────────────┘
└──────────────────────────┘                                                  │
                                                                              │ /api/qr/{code}, /api/config
                                                                              ▼
                                                                     QRCoder (server-side PNG)
```

- **No database.** All state lives in **static `ConcurrentDictionary`s inside `BattleshipHub`**. This is intentional (single-container, ephemeral party game). **Restarting the server wipes all games.**
- **Signalling is SignalR** at `/hub/battleship`. The browser client is loaded from a CDN (`@microsoft/signalr@8.0.7`).
- **Static files** are served from `wwwroot/`. `Program.cs` maps `/` → `/index.html`, serves QR codes and config.
- **QR codes** are generated server-side with `QRCoder` and encode the join URL (`{PUBLIC_BASE_URL}/play.html?room=CODE`).

---

## 3. Project layout

```
gp-battleship-webapp/
├─ README.md                      ← you are here
├─ .gitignore
├─ .vscode/
│  ├─ launch.json                 ← F5 debugging configs
│  └─ tasks.json                  ← build / publish / watch tasks
└─ src/
   ├─ src.slnx                    ← solution
   ├─ docker-compose.yml          ← container definition
   └─ GpBattleshipWebapp/
      ├─ GpBattleshipWebapp.csproj  ← net10.0, references QRCoder 1.6.0
      ├─ Program.cs                 ← host setup, static files, /api endpoints, hub mapping
      ├─ Dockerfile                 ← multi-stage build → aspnet:10.0 runtime, non-root, port 8080
      ├─ appsettings.json           ← base config (PUBLIC_BASE_URL read from here or env)
      ├─ Properties/launchSettings.json
      ├─ Hubs/
      │  └─ BattleshipHub.cs        ← ⭐ ALL server game logic + state models
      └─ wwwroot/
         ├─ index.html              ← admin console page
         ├─ play.html               ← player page
         ├─ css/site.css            ← all styling (boards, hulls, labels, dashboard)
         └─ js/
            ├─ host.js              ← admin console logic (multi-game)
            ├─ player.js            ← player logic (placement, battle, hulls, labels)
            └─ sfx.js               ← Web Audio sound engine (no asset files)
```

**The two files you will touch most when adding gameplay features:**
1. `Hubs/BattleshipHub.cs` — server rules, validation, state, broadcasts.
2. `wwwroot/js/player.js` (player UX) and/or `wwwroot/js/host.js` (admin UX).

---

## 4. Running locally

**Prerequisites:** .NET SDK 10.

```bash
cd src/GpBattleshipWebapp
dotnet run
```

Then open the URL it prints (default from `launchSettings.json`, e.g. `http://localhost:5184`).

### Debugging with VS Code (F5)
Open the repo root, press **F5**. `.vscode/launch.json` provides:
- **Launch Battleship (Debug)** — builds then launches the Debug DLL on `http://localhost:5184`, auto-opens the browser.
- **Launch Battleship (Watch / hot reload)** — `dotnet watch` for live C#/Razor reload.
- **Attach to Process** — attach to an already-running instance.

> **Port note:** if F5 says "address already in use", a stale `dotnet` process is holding the port. Find and kill it: `lsof -nP -iTCP:5184 -sTCP:LISTEN` then `kill <pid>`.

### Quick manual test flow
1. Open `/` in one browser tab (admin) → **Create Game**.
2. Copy the join link / QR. Open `/play.html?room=CODE` in **two** separate tabs/devices → Join as two players.
3. Both place fleets → **Ready** → battle begins.
4. Fire on the enemy grid; watch the admin log and the other player's board.

---

## 5. Running with Docker / production

The compose file builds the image and exposes it on host port **8080**:

```bash
cd src
docker compose up -d --build
```

- The container listens on `8080` internally (`ASPNETCORE_URLS=http://+:8080`), runs as the non-root `app` user.
- Designed to sit behind a **Cloudflare Tunnel**/reverse proxy pointing at `http://localhost:8080`. `Program.cs` enables `ForwardedHeaders` (`X-Forwarded-For/Proto/Host`) and clears known networks/proxies so the tunnel is trusted.
- **`PUBLIC_BASE_URL`** (in `docker-compose.yml` environment) MUST be set to your real public HTTPS origin (e.g. `https://battleship.example.com`). It's used to build the QR/join link so phones hit the tunnel, not `localhost`.

---

## 6. Configuration

| Setting | Where | Purpose |
|---|---|---|
| `PUBLIC_BASE_URL` | env var or `appsettings.json` | Public base URL used to build the QR join link and shown on the admin page. Empty → falls back to the request's own scheme/host (fine for localhost). |
| `ASPNETCORE_URLS` | env / Dockerfile | Listening address. Docker sets `http://+:8080`; local dev uses `launchSettings.json`. |
| `ASPNETCORE_ENVIRONMENT` | env | `Development` (local) vs `Production` (Docker). Affects logging/dev exception page. |

`Program.cs` reads `PUBLIC_BASE_URL` into a singleton `PublicUrlOptions` used by `/api/qr` and `/api/config`.

---

## 7. The data model

All models are nested classes at the bottom of `BattleshipHub.cs`. **In-memory only.**

### Static dictionaries (the whole game state)
```csharp
Rooms            : ConcurrentDictionary<string, GameRoom>   // roomCode → game
ConnectionRooms  : ConcurrentDictionary<string, string>     // player connectionId → roomCode
HostGames        : ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>
                                                            // host connectionId → set of roomCodes it owns
```
- A **player** connection maps to **exactly one** room (`ConnectionRooms`).
- A **host** connection can own **many** rooms (`HostGames`).

### `GameRoom`
| Field | Meaning |
|---|---|
| `Code` | 5-char room code (alphabet excludes ambiguous chars). |
| `HostConnectionId` | The admin connection that owns this game. |
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
| `CreateGame` | `string hostName, bool shootAgainOnHit` | Host | Creates a room, adds host to its group, registers in `HostGames`, replies `GameCreated`. **Call repeatedly to host multiple games.** |
| `ListGames` | — | Host | Replies `GamesList` with all rooms this host owns (used to restore the dashboard after reconnect/reload). |
| `JoinGame` | `string code, string playerName` | Player | Validates room exists & has <2 players; issues `ReconnectToken`; adds to room group; replies `GameJoined` + `LobbyUpdated`. |
| `Reconnect` | `string code, string token` | Player | Re-claims a held seat by token, rebinds to the new connection, replies `Reconnected` (full state) plus lobby/pause updates. |
| `AbortGame` | `string code` | Host | Only the room's host. Broadcasts `GameAborted` to the room, detaches players, removes room, replies `GameRemoved` to host. |
| `NewGame` | `string code` | Host | Rematch: resets `Started`, `CurrentTurnId`, and every player's fleet/shots; broadcasts `NewGameStarted`. Same room/QR. |
| `PlaceShips` | `List<ShipPlacement> ships` | Player | Validates placement (exact fleet, in-bounds, no overlap, straight & contiguous); stores fleet; when both players are ready → starts game and broadcasts `GameStarted`. |
| `Fire` | `int x, int y` | Player | The core turn action — see §11. Broadcasts `ShotResult`, then `GameOver` **or** `TurnChanged`. |
| `OnDisconnectedAsync` (override) | — | framework | Handles host vs player disconnect (§14). |

**Validation that exists today (mirror this style when adding actions):**
- Turn checks: game started, not paused (both connected), it's your turn.
- `Fire`: in-bounds, not already fired there.
- `PlaceShips`: `ValidatePlacement(...)`.

**Host/ownership checks:** `IsHostOf(code)` gates `AbortGame` and `NewGame`.
**Player/room resolution:** `TryGetRoomAndPlayer(out room, out player)` for player-scoped actions.

---

## 9. Client events — server → client (complete)

These are `connection.on('EventName', ...)` on the client. Group messages go to everyone in the room; `Clients.Caller`/`Clients.Client` go to a specific connection.

| Event | Payload | Sent to | Meaning / client action |
|---|---|---|---|
| `GameCreated` | `code, hostConnectionId, fleetSpec, shootAgainOnHit` | caller (host) | Add a dashboard card. |
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
| `OpponentDisconnected` | `name, graceSeconds` | room group | Info banner. |
| `OpponentLeft` | — | room group | Seat expired. |
| `Reconnected` | `ReconnectState` | caller (player) | Full state snapshot to resume. |
| `ReconnectFailed` | `string reason` | caller (player) | Seat gone → fall back to join. |
| `GameAborted` | `string reason` | room group | Host aborted / host disconnected → players end. |
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
- On reconnect it invokes `ListGames` to re-sync.

### Player (`play.html` + `player.js`)
- Keyed by `?room=CODE`. One connection per tab.
- **Persistence:** `localStorage['bs_token_' + room]` stores the reconnect token so a page reload resumes the seat (`Reconnect`) instead of re-joining.
- Screens (toggled by `.hidden`): `joinPanel` → `placePanel` → `battlePanel`.
- **Placement:** tap ship to rotate (H/V), tap a cell to place; tap a placed ship to remove. Ships are removed and re-placed via the `own` map (`"x,y" → shipName`).
- **Battle:** fire on `enemyGrid`; own ships show on `ownGrid`. Turn/disabled states managed by `myTurn` + `enemyGrid.classList.toggle('disabled', ...)`.
- **Reconnect:** `onreconnecting` → banner; `onreconnected` → `tryReconnect(token)`; server replies `Reconnected` → `restoreState(...)`.

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
| `MapHub /hub/battleship` | The SignalR hub. |
| static | Everything in `wwwroot/` via `UseStaticFiles`/`UseDefaultFiles`. |

---

## 14. Concurrency & lifecycle rules

- **Single container assumption.** State is static in-process; do not scale out horizontally without an external store/backplane.
- **Host disconnect** → all rooms that host owned are removed and their players get `GameAborted`.
- **Player disconnect** → seat held for `ReconnectGrace` (**5 min**) with `Connected=false`; if a game was running it **pauses** (`PauseState true`, `OpponentDisconnected`). A `ExpirePlayerAsync` task runs after the grace window: if still disconnected, the seat is removed (game un-starts, or room dropped if empty).
- **All players disconnected** → room removed.
- **Reconnect identity** = `ReconnectToken` (not connection id). Seats rebind to new connection ids.
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

---

## 16. Conventions & gotchas

- **Server is authoritative.** Clients are dumb views; all validation/state lives in the hub.
- **Seat identity = `ReconnectToken`.** Don't compare players by `ConnectionId` for identity (it changes on reconnect); use it only for routing/turns.
- **Sink detection** must remain "this shot completed the ship" — see §11/§3 bug note.
- **`GameUpdated`** (host, `Clients.Client`) is the authoritative per-card refresh; group events like `ShotResult` are matched to the right card by scanning `games` for the shooter's id.
- **Idempotent rendering helpers**: `addBoardLabels` guards with `data-labeled`; `layoutShips` resets hull classes each call but preserves `ship-hit/hit/miss`.
- **CSS class slugs**: `shipSlug(name)` must match your `.ship-<slug>` color rules (`Battleship` → `ship-battleship`).
- **Rebuild paths**: `buildGrid` clears `innerHTML`; the `.board-frame` wrapper and labels persist because they live *around* the grid element, which is reused. Don't replace the grid element itself without re-running `addBoardLabels`.
- **localStorage keys**: `bs_token_<ROOM>` (player resume), `bs_muted` (sound).
- **Build config**: `net10.0`, nullable + implicit usings enabled; only external NuGet dep is `QRCoder 1.6.0`.
- **Build/verify before shipping:** `dotnet build -c Release` (expect 0 warnings/errors) and `node --check` on edited JS; `docker compose up -d --build` for the container.

---

## 17. Known limitations / future ideas

- **No persistence** — server restart loses all games (by design). Persist to Redis/DB if you need durability or horizontal scale (SignalR backplane).
- **Two players only**, no matchmaking/queue; join is via shared QR.
- **No spectators** (besides the host log), no chat.
- **Bow/stern direction is not tracked**, so both free ends of a hull render with symmetric rounded caps.
- **No reconnection UI beyond auto-retry**; a seat expires after 5 minutes.
- **No auth** — anyone with the room code can join; host is identified only by its connection.
- Possible additions: sound themes, animations for full-fleet reveal at game over, ELO/scoreboard (needs persistence), configurable fleet, spectator mode, mobile layout tweaks.

---

_Last whole-app review: ships as connected color-coded hulls, A–J/1–10 coordinate labels, multi-game admin console with abort/rematch, fixed "always says sunk" sink-detection bug, per-ship colors._
