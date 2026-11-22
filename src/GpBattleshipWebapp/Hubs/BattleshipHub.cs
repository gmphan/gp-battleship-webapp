using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GpBattleshipWebapp.Hubs;

/// <summary>
/// Real-time Battleship hub.
///
/// Multiple concurrent games: every game is an independent room in <see cref="Rooms"/>
/// with its own group, QR code and two player seats. A single host connection may
/// own *many* rooms at once (the admin dashboard), tracked in <see cref="HostGames"/>.
///
/// Each room has one shared QR; the first two players to scan join P1 and P2.
/// Reconnect: each player gets a stable token; a dropped seat is held for a grace
/// window and paused, and a client presenting the token re-claims the seat.
///
/// All state is in-memory, so this is intended to run as a single container instance.
///
/// AUTH: Players connect anonymously (they scan a QR to play). The HOST/admin methods
/// (CreateGame, ListGames, ResumeHost, AbortGame, NewGame) require an authenticated
/// admin session ([Authorize]); host identity is the authenticated username taken
/// from the auth cookie, NOT a client-supplied token. So a host can only manage games
/// it owns, and an anonymous connection can't create or abort games.
/// </summary>
public class BattleshipHub : Hub
{
    private static readonly ConcurrentDictionary<string, GameRoom> Rooms = new();
    // Player connection -> room code (a player is in at most one room).
    private static readonly ConcurrentDictionary<string, string> ConnectionRooms = new();
    // STABLE host identity (authenticated username) -> set of room codes it owns.
    // Keyed by username (from the auth cookie), NOT a client token or connection id,
    // so a host survives reconnects/reloads and can only manage its own games.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> HostGames = new();

    private const int BoardSize = 10;
    // How long a disconnected player's seat is held before removal. Generous so a
    // phone that locks / backgrounds / briefly loses signal doesn't end the game.
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<string, int> Fleet = new()
    {
        ["Carrier"] = 5,
        ["Battleship"] = 4,
        ["Cruiser"] = 3,
        ["Submarine"] = 3,
        ["Destroyer"] = 2,
    };

    private static string FleetSpec =>
        string.Join(",", Fleet.Select(kv => $"{kv.Key}:{kv.Value}"));

    // ----- Room lifecycle -----

    /// <summary>
    /// Host creates a new room. <paramref name="shootAgainOnHit"/> controls the turn rule.
    /// A host may call this repeatedly to run several games at once. Requires login;
    /// the host identity is the authenticated username (cookie), never a client token.
    /// </summary>
    [Authorize]
    public async Task CreateGame(string hostName, bool shootAgainOnHit)
    {
        var token = CurrentHostToken();
        if (token == null)
        {
            await Clients.Caller.SendAsync("Error", "You must be signed in to create a game.");
            return;
        }

        var code = GenerateRoomCode();
        var room = new GameRoom(code)
        {
            HostConnectionId = Context.ConnectionId,
            HostToken = token,
            ShootAgainOnHit = shootAgainOnHit
        };
        Rooms[code] = room;

        // Track this room under the host's authenticated identity.
        var owned = HostGames.GetOrAdd(token, _ => new ConcurrentDictionary<string, byte>());
        owned[code] = 0;

        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        await Clients.Caller.SendAsync("GameCreated", code, Context.ConnectionId, FleetSpec, shootAgainOnHit, token);
    }

    /// <summary>
    /// Host re-claims its dashboard after a reconnect/reload. The owner is derived
    /// from the authenticated session, so this just re-binds owned rooms to the
    /// current connection and replies with the list.
    /// </summary>
    [Authorize]
    public async Task ResumeHost()
    {
        var token = CurrentHostToken();
        if (token == null) { await ListGamesForToken(null); return; }

        if (HostGames.TryGetValue(token, out var owned))
        {
            foreach (var code in owned.Keys)
            {
                if (Rooms.TryGetValue(code, out var room))
                {
                    // Re-point the room at this (new) connection and rejoin its group.
                    room.HostConnectionId = Context.ConnectionId;
                    await Groups.AddToGroupAsync(Context.ConnectionId, code);
                }
            }
        }

        await ListGamesForToken(token);
    }

    /// <summary>Returns all games owned by the calling (authenticated) host.</summary>
    [Authorize]
    public async Task ListGames() => await ListGamesForToken(this.CurrentHostToken());

    private async Task ListGamesForToken(string? token)
    {
        var list = new List<object>();
        if (token != null && HostGames.TryGetValue(token, out var owned))
        {
            foreach (var code in owned.Keys)
            {
                if (!Rooms.TryGetValue(code, out var r)) continue;
                list.Add(new
                {
                    code = r.Code,
                    shootAgainOnHit = r.ShootAgainOnHit,
                    started = r.Started,
                    players = r.Players.Select(p => new { id = p.ConnectionId, name = p.Name, ready = p.Ships != null, connected = p.Connected, token = p.ReconnectToken }).ToArray()
                });
            }
        }
        await Clients.Caller.SendAsync("GamesList", list);
    }

    /// <summary>A player scans the shared QR and joins the room.</summary>
    public async Task JoinGame(string code, string playerName)
    {
        code = (code ?? "").Trim().ToUpperInvariant();

        if (!Rooms.TryGetValue(code, out var room))
        {
            await Clients.Caller.SendAsync("Error", "Game not found.");
            return;
        }

        if (room.Players.Count >= 2)
        {
            await Clients.Caller.SendAsync("Error", "This game already has two players.");
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        var player = new Player(Context.ConnectionId) { Name = Sanitize(playerName), ReconnectToken = token };
        room.Players.Add(player);
        ConnectionRooms[Context.ConnectionId] = code;

        await Groups.AddToGroupAsync(Context.ConnectionId, code);

        await Clients.Caller.SendAsync("GameJoined", code, Context.ConnectionId, FleetSpec, token, room.ShootAgainOnHit);
        await NotifyLobbyAndHost(room);
    }

    /// <summary>
    /// A dropped player returns with their token. Rebinds the existing seat to the
    /// new connection id and replays current state so the client can resume.
    /// </summary>
    public async Task Reconnect(string code, string token)
    {
        code = (code ?? "").Trim().ToUpperInvariant();

        if (!Rooms.TryGetValue(code, out var room))
        {
            await Clients.Caller.SendAsync("ReconnectFailed", "Game is no longer available.");
            return;
        }

        var player = room.Players.FirstOrDefault(p => p.ReconnectToken == token);
        if (player == null)
        {
            await Clients.Caller.SendAsync("ReconnectFailed", "Your seat is no longer held.");
            return;
        }

        player.ConnectionId = Context.ConnectionId;
        player.Connected = true;
        player.DisconnectedAt = null;
        ConnectionRooms[Context.ConnectionId] = code;

        await Groups.AddToGroupAsync(Context.ConnectionId, code);

        var state = new ReconnectState
        {
            PlayerId = Context.ConnectionId,
            ConnectId = player.ConnectionId,
            FleetSpec = FleetSpec,
            ShootAgainOnHit = room.ShootAgainOnHit,
            Started = room.Started,
            CurrentTurnId = room.CurrentTurnId,
            Ships = player.Ships ?? new(),
            MyShots = player.ShotsAtOpponent.Select(k =>
            {
                var parts = k.Split(',');
                return new ShotDto { X = int.Parse(parts[0]), Y = int.Parse(parts[1]), Hit = player.HitsOnOpponent.Contains(k) };
            }).ToList(),
        };

        await NotifyLobbyAndHost(room);
        await BroadcastPauseState(room);

        await Clients.Caller.SendAsync("Reconnected", state);
    }

    // ----- Host controls -----

    /// <summary>The authenticated host identity (username) for this connection, or null if anonymous.</summary>
    private string? CurrentHostToken()
    {
        var name = Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.Identity.Name
            : null;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
    }

    /// <summary>Host aborts and removes one of their games.</summary>
    [Authorize]
    public async Task AbortGame(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();

        if (!IsHostOf(code))
        {
            await Clients.Caller.SendAsync("Error", "You are not the host of that game.");
            return;
        }

        if (Rooms.TryRemove(code, out var room))
        {
            // Tell everyone in the room it's over.
            await Clients.Group(code).SendAsync("GameAborted", "The host aborted this game.");

            // Detach players so they aren't left mapped to a dead room.
            foreach (var p in room.Players)
            {
                ConnectionRooms.TryRemove(p.ConnectionId, out _);
            }

            // Remove our group membership on the server side for the host connection.
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, code);
        }

        RemoveFromHostGames(room?.HostToken ?? CurrentHostToken(), code);
        await Clients.Caller.SendAsync("GameRemoved", code);
    }

    /// <summary>Host resets one of their games back to the lobby for a rematch (same room, same QR).</summary>
    [Authorize]
    public async Task NewGame(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();

        if (!IsHostOf(code) || !Rooms.TryGetValue(code, out var room))
        {
            await Clients.Caller.SendAsync("Error", "Cannot start a new game for that room.");
            return;
        }

        // Reset every player's fleet/shots but keep their seats & tokens.
        room.Started = false;
        room.CurrentTurnId = null;
        foreach (var p in room.Players) p.ResetGame();

        await Clients.Group(code).SendAsync("NewGameStarted", room.ShootAgainOnHit);
        await NotifyLobbyAndHost(room);
    }

    private bool IsHostOf(string code)
    {
        if (Rooms.TryGetValue(code, out var room))
        {
            // Authoritative: the current connection represents this room's host token.
            var cur = CurrentHostToken();
            if (cur != null && room.HostToken == cur) return true;
        }
        return false;
    }

    private static void RemoveFromHostGames(string? hostToken, string code)
    {
        if (hostToken == null) return;
        if (HostGames.TryGetValue(hostToken, out var owned))
        {
            owned.TryRemove(code, out _);
            if (owned.IsEmpty) HostGames.TryRemove(hostToken, out _);
        }
    }

    private async Task NotifyLobbyAndHost(GameRoom room)
    {
        await BroadcastLobby(room);
        // Also push an update to the host so its dashboard card reflects the change.
        if (room.HostConnectionId != null)
        {
            await Clients.Client(room.HostConnectionId).SendAsync("GameUpdated", room.Code, room.Started,
                room.Players.Select(p => new { id = p.ConnectionId, name = p.Name, ready = p.Ships != null, connected = p.Connected }).ToArray());
        }
    }

    private async Task BroadcastLobby(GameRoom room)
    {
        var players = room.Players
            .Select(p => new { id = p.ConnectionId, name = p.Name, ready = p.Ships != null, connected = p.Connected })
            .ToArray();
        await Clients.Group(room.Code).SendAsync("LobbyUpdated", players);
    }

    private async Task BroadcastPauseState(GameRoom room)
    {
        if (!room.Started) return;
        var paused = room.Players.Any(p => !p.Connected);
        await Clients.Group(room.Code).SendAsync("PauseState", paused);
    }

    // ----- Ship placement -----

    public async Task PlaceShips(List<ShipPlacement> ships)
    {
        if (!TryGetRoomAndPlayer(out var room, out var player)) return;

        if (!ValidatePlacement(ships, out var error))
        {
            await Clients.Caller.SendAsync("Error", $"Invalid ship placement: {error}");
            return;
        }

        player.Ships = ships;
        player.ShipCells = ships.SelectMany(s => s.Cells).Select(c => (c.X, c.Y)).ToHashSet();

        await Clients.Caller.SendAsync("ShipsPlaced");
        await NotifyLobbyAndHost(room);

        if (room.Players.Count == 2 && room.Players.All(p => p.Ships != null))
        {
            room.Started = true;
            room.CurrentTurnId = room.Players[0].ConnectionId;
            await Clients.Group(room.Code).SendAsync("GameStarted", room.CurrentTurnId);
            await NotifyLobbyAndHost(room);
        }
    }

    // ----- Firing -----

    public async Task Fire(int x, int y)
    {
        if (!TryGetRoomAndPlayer(out var room, out var player)) return;

        if (!room.Started)
        {
            await Clients.Caller.SendAsync("Error", "Game not started yet.");
            return;
        }
        if (room.Players.Any(p => !p.Connected))
        {
            await Clients.Caller.SendAsync("Error", "Game is paused while a player reconnects.");
            return;
        }
        if (room.CurrentTurnId != Context.ConnectionId)
        {
            await Clients.Caller.SendAsync("Error", "It's not your turn.");
            return;
        }
        if (x < 0 || x >= BoardSize || y < 0 || y >= BoardSize)
        {
            await Clients.Caller.SendAsync("Error", "Shot out of bounds.");
            return;
        }

        var opponent = room.OpponentOf(player);
        if (opponent == null) return;

        var key = $"{x},{y}";
        if (player.ShotsAtOpponent.Contains(key))
        {
            await Clients.Caller.SendAsync("Error", "You already fired there.");
            return;
        }

        player.ShotsAtOpponent.Add(key);
        bool hit = opponent.ShipCells.Contains((x, y));
        if (hit) player.HitsOnOpponent.Add(key);

        // A ship counts as "sunk" only on the shot that lands its final cell.
        // Require that this shot's cell belongs to the ship AND all its cells are hit,
        // so already-sunk ships are never re-announced on later hits.
        string? sunkShip = null;
        if (hit && opponent.Ships != null)
        {
            foreach (var ship in opponent.Ships)
            {
                bool thisShipHit = ship.Cells.Any(c => c.X == x && c.Y == y);
                bool fullyHit = ship.Cells.All(c => player.ShotsAtOpponent.Contains($"{c.X},{c.Y}"));
                if (thisShipHit && fullyHit)
                {
                    sunkShip = ship.Name;
                    break;
                }
            }
        }

        bool opponentDefeated = opponent.ShipCells.Count > 0 &&
                                opponent.ShipCells.All(c => player.ShotsAtOpponent.Contains($"{c.Item1},{c.Item2}"));

        await Clients.Group(room.Code).SendAsync("ShotResult",
            Context.ConnectionId, x, y, hit, sunkShip, opponentDefeated);

        if (opponentDefeated)
        {
            room.Started = false;
            await Clients.Group(room.Code).SendAsync("GameOver", Context.ConnectionId);
            await NotifyLobbyAndHost(room);
            return;
        }

        bool keepTurn = room.ShootAgainOnHit && hit;
        if (!keepTurn)
        {
            room.CurrentTurnId = opponent.ConnectionId;
        }
        await Clients.Group(room.Code).SendAsync("TurnChanged", room.CurrentTurnId, keepTurn);
    }

    // ----- Disconnect handling -----

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // 1) Host disconnecting: do NOT tear the rooms down. The host owns its rooms
        //    via its authenticated identity and can resume them (ResumeHost) after a
        //    reconnect/reload. Mark its rooms as having no live host connection so
        //    host-targeted updates pause until the host returns.
        var hostToken = CurrentHostToken();
        if (hostToken != null && HostGames.TryGetValue(hostToken, out var ownedRooms))
        {
            foreach (var code in ownedRooms.Keys)
            {
                if (Rooms.TryGetValue(code, out var ownedRoom)) ownedRoom.HostConnectionId = null;
            }
        }

        // 2) Player disconnecting: hold the seat for the grace window, pause the game.
        if (ConnectionRooms.TryRemove(Context.ConnectionId, out var pcode) &&
            Rooms.TryGetValue(pcode, out var proom))
        {
            var player = proom.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player != null)
            {
                player.Connected = false;
                player.DisconnectedAt = DateTime.UtcNow;

                if (proom.Started)
                {
                    await Clients.Group(pcode).SendAsync("PauseState", true);
                    await Clients.Group(pcode).SendAsync("OpponentDisconnected", player.Name, (int)ReconnectGrace.TotalSeconds);
                }

                await NotifyLobbyAndHost(proom);
                _ = ExpirePlayerAsync(pcode, player.ReconnectToken);
            }

            if (proom.Players.All(p => !p.Connected))
            {
                Rooms.TryRemove(pcode, out _);
                RemoveFromHostGames(proom.HostToken, pcode);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task ExpirePlayerAsync(string code, string token)
    {
        await Task.Delay(ReconnectGrace);

        if (!Rooms.TryGetValue(code, out var room)) return;

        var player = room.Players.FirstOrDefault(p => p.ReconnectToken == token);
        if (player == null || player.Connected) return;

        room.Players.Remove(player);

        if (room.Started)
        {
            room.Started = false;
            room.CurrentTurnId = null;
        }

        if (room.Players.Count == 0)
        {
            Rooms.TryRemove(code, out _);
            RemoveFromHostGames(room.HostToken, code);
        }
        else
        {
            await Clients.Group(code).SendAsync("OpponentLeft");
            await NotifyLobbyAndHost(room);
        }
    }

    // ----- Helpers -----

    private bool TryGetRoomAndPlayer(out GameRoom room, out Player player)
    {
        room = null!;
        player = null!;

        if (!ConnectionRooms.TryGetValue(Context.ConnectionId, out var code) ||
            !Rooms.TryGetValue(code, out var r))
        {
            return false;
        }
        room = r;
        player = r.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId)!;
        return player != null;
    }

    private static bool ValidatePlacement(List<ShipPlacement> ships, out string error)
    {
        error = "";
        if (ships == null || ships.Count != Fleet.Count)
        {
            error = "fleet must contain all 5 ships.";
            return false;
        }

        var seen = new HashSet<(int, int)>();
        foreach (var ship in ships)
        {
            if (!Fleet.TryGetValue(ship.Name, out var expectedLen))
            {
                error = $"unknown ship '{ship.Name}'.";
                return false;
            }
            if (ship.Cells == null || ship.Cells.Count != expectedLen)
            {
                error = $"ship '{ship.Name}' must occupy {expectedLen} cells.";
                return false;
            }
            foreach (var c in ship.Cells)
            {
                if (c.X < 0 || c.X >= BoardSize || c.Y < 0 || c.Y >= BoardSize)
                {
                    error = $"'{ship.Name}' is out of bounds.";
                    return false;
                }
                if (!seen.Add((c.X, c.Y)))
                {
                    error = "ships overlap.";
                    return false;
                }
            }

            var cells = ship.Cells.OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
            bool sameRow = cells.All(c => c.Y == cells[0].Y);
            bool sameCol = cells.All(c => c.X == cells[0].X);
            if (!sameRow && !sameCol)
            {
                error = $"'{ship.Name}' must be a straight line.";
                return false;
            }
            for (int i = 1; i < cells.Count; i++)
            {
                int d = Math.Abs(cells[i].X - cells[i - 1].X) + Math.Abs(cells[i].Y - cells[i - 1].Y);
                if (d != 1)
                {
                    error = $"'{ship.Name}' must be contiguous.";
                    return false;
                }
            }
        }
        return true;
    }

    private static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string code;
        do
        {
            code = new string(Enumerable.Range(0, 5)
                .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        } while (Rooms.ContainsKey(code));
        return code;
    }

    private static string Sanitize(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Player";
        return name.Length > 20 ? name[..20] : name;
    }

    // ----- State models -----

    public class GameRoom
    {
        public string Code { get; }
        public string? HostConnectionId { get; set; }
        public string HostToken { get; set; } = "";
        public bool ShootAgainOnHit { get; set; }
        public List<Player> Players { get; } = new();
        public bool Started { get; set; }
        public string? CurrentTurnId { get; set; }

        public GameRoom(string code) => Code = code;

        public Player? OpponentOf(Player me) =>
            Players.FirstOrDefault(p => p.ReconnectToken != me.ReconnectToken);
    }

    public class Player
    {
        public string ConnectionId { get; set; }
        public string Name { get; set; } = "Player";
        public string ReconnectToken { get; set; } = "";
        public bool Connected { get; set; } = true;
        public DateTime? DisconnectedAt { get; set; }
        public List<ShipPlacement>? Ships { get; set; }
        public HashSet<(int, int)> ShipCells { get; set; } = new();
        public HashSet<string> ShotsAtOpponent { get; set; } = new();
        public HashSet<string> HitsOnOpponent { get; set; } = new();

        public Player(string connectionId) => ConnectionId = connectionId;

        /// <summary>Clears fleet and shots but keeps the seat identity (for a rematch).</summary>
        public void ResetGame()
        {
            Ships = null;
            ShipCells = new();
            ShotsAtOpponent = new();
            HitsOnOpponent = new();
        }
    }

    public class ShipPlacement
    {
        public string Name { get; set; } = "";
        public List<Cell> Cells { get; set; } = new();
    }

    public class Cell
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class ShotDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Hit { get; set; }
    }

    public class ReconnectState
    {
        public string PlayerId { get; set; } = "";
        public string ConnectId { get; set; } = "";
        public string FleetSpec { get; set; } = "";
        public bool ShootAgainOnHit { get; set; }
        public bool Started { get; set; }
        public string? CurrentTurnId { get; set; }
        public List<ShipPlacement> Ships { get; set; } = new();
        public List<ShotDto> MyShots { get; set; } = new();
    }
}
