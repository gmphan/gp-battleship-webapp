/* Admin console logic for Battleship over SignalR.
 * Runs MANY concurrent games from one connection. Each game is a dashboard
 * card with its own QR, room code, live player list, status, and controls
 * (Abort / New Game).
 */
(() => {
  const startPanel = document.getElementById('startPanel');
  const gamesEl = document.getElementById('games');
  const emptyHint = document.getElementById('emptyHint');
  const createBtn = document.getElementById('createBtn');
  const hostNameInput = document.getElementById('hostName');
  const shootAgainInput = document.getElementById('shootAgain');
  const toastEl = document.getElementById('toast');

  let connection = null;
  let publicBaseUrl = '';

  // code -> { code, shootAgainOnHit, started, players: [] }
  const games = new Map();
  // code -> { card, ...element refs, feed lines }
  const cards = new Map();

  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    clearTimeout(toast._t);
    toast._t = setTimeout(() => toastEl.classList.remove('show'), 3200);
  }

  async function loadConfig() {
    try {
      const res = await fetch('/api/config');
      const cfg = await res.json();
      publicBaseUrl = (cfg.publicBaseUrl || '').replace(/\/$/, '');
    } catch { /* ignore */ }
  }

  // ===== Card rendering =====

  function ensureCard(code) {
    if (cards.has(code)) return cards.get(code);

    const card = document.createElement('div');
    card.className = 'game-card';
    card.dataset.code = code;
    card.innerHTML = `
      <div class="game-card-head">
        <span class="room-code sm" data-role="code"></span>
        <span class="status-pill" data-role="status">Lobby</span>
      </div>
      <div class="qr-wrap sm">
        <img data-role="qr" alt="Join QR" />
        <div class="game-meta">
          <div class="muted">Join link</div>
          <div class="join-link" data-role="link"></div>
        </div>
      </div>
      <div class="players" data-role="players"></div>
      <div class="log mini" data-role="feed"></div>
      <div class="row card-actions">
        <button class="secondary" data-role="newgame">🔄 New Game</button>
        <button class="danger" data-role="abort">⛔ Abort</button>
      </div>
    `;

    const refs = {
      el: card,
      code: card.querySelector('[data-role="code"]'),
      status: card.querySelector('[data-role="status"]'),
      qr: card.querySelector('[data-role="qr"]'),
      link: card.querySelector('[data-role="link"]'),
      players: card.querySelector('[data-role="players"]'),
      feed: card.querySelector('[data-role="feed"]'),
      newGameBtn: card.querySelector('[data-role="newgame"]'),
      abortBtn: card.querySelector('[data-role="abort"]'),
      feedLines: [],
    };

    refs.code.textContent = code;
    const base = publicBaseUrl || window.location.origin;
    const link = `${base}/play.html?room=${encodeURIComponent(code)}`;
    refs.link.textContent = link;
    refs.qr.src = `/api/qr/${encodeURIComponent(code)}`;

    refs.newGameBtn.addEventListener('click', () => invokeSafe('NewGame', code));
    refs.abortBtn.addEventListener('click', () => {
      if (confirm(`Abort game ${code}? Players will be disconnected.`)) {
        invokeSafe('AbortGame', code);
      }
    });

    cards.set(code, refs);
    gamesEl.appendChild(card);
    refreshEmptyHint();
    return refs;
  }

  function updateCard(g) {
    const refs = ensureCard(g.code);
    const started = g.started;
    refs.status.textContent = started ? 'In progress' : 'Lobby';
    refs.status.className = 'status-pill ' + (started ? 'live' : 'idle');
    refs.newGameBtn.disabled = g.players.length < 2;
    renderCardPlayers(refs, g.players || []);
  }

  function renderCardPlayers(refs, players) {
    refs.players.innerHTML = '';
    players.forEach((p, i) => {
      const chip = document.createElement('span');
      const ready = p.ready ? ' ready' : '';
      const off = p.connected === false ? ' offline' : '';
      chip.className = 'player-chip' + ready + off;
      chip.dataset.id = p.id;
      chip.innerHTML = `<span class="dot ${p.ready ? 'ready' : ''}"></span>${escapeHtml(p.name)} (P${i + 1})` +
        (p.connected === false ? ' — offline' : (p.ready ? ' — ready' : ''));
      refs.players.appendChild(chip);
    });
    for (let i = players.length; i < 2; i++) {
      const chip = document.createElement('span');
      chip.className = 'player-chip';
      chip.innerHTML = `<span class="dot"></span>Empty seat`;
      refs.players.appendChild(chip);
    }
  }

  function cardLog(code, msg) {
    const refs = cards.get(code);
    if (!refs) return;
    refs.feedLines.push(msg);
    if (refs.feedLines.length > 50) refs.feedLines.shift();
    refs.feed.innerHTML = refs.feedLines.map(l => `<div>${escapeHtml(l)}</div>`).join('');
    refs.feed.scrollTop = refs.feed.scrollHeight;
  }

  function removeCard(code) {
    const refs = cards.get(code);
    if (refs) { refs.el.remove(); cards.delete(code); }
    games.delete(code);
    refreshEmptyHint();
  }

  function refreshEmptyHint() {
    emptyHint.style.display = cards.size === 0 ? 'block' : 'none';
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  // ===== Connection =====

  async function connect() {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/battleship')
      .withAutomaticReconnect()
      .build();

    connection.on('GameCreated', (code, hostId, spec, shootAgainOnHit) => {
      games.set(code, { code, shootAgainOnHit, started: false, players: [] });
      updateCard(games.get(code));
      cardLog(code, 'Game created — waiting for players.');
    });

    // Host-directed per-game updates.
    connection.on('GameUpdated', (code, started, players) => {
      const g = games.get(code) || { code };
      g.started = started;
      g.players = players;
      games.set(code, g);
      updateCard(g);
    });

    // Shot feed across whichever games are running.
    connection.on('ShotResult', (by, x, y, hit, sunk) => {
      // Find the game whose player list contains this shooter.
      for (const [code, g] of games) {
        if ((g.players || []).some(p => p.id === by)) {
          const cell = String.fromCharCode(65 + x) + (y + 1);
          cardLog(code, `${hit ? '💥 HIT' : '💦 miss'} at ${cell}${sunk ? ' — sank ' + sunk : ''}`);
          break;
        }
      }
    });

    connection.on('GameStarted', () => { /* status comes via GameUpdated */ });

    connection.on('GameOver', (winner) => {
      for (const [code, g] of games) {
        const p = (g.players || []).find(p => p.id === winner);
        if (p) {
          cardLog(code, `🏆 ${p.name} wins!`);
          const refs = cards.get(code);
          if (refs) { refs.status.textContent = 'Finished'; refs.status.className = 'status-pill done'; }
          break;
        }
      }
    });

    // AbortGame emits GameRemoved (with code) back to the host; the group-facing
    // GameAborted is mainly for players.
    connection.on('GameRemoved', (code) => {
      if (cards.has(code)) cardLog(code, 'Game removed.');
      removeCard(code);
    });

    connection.on('GamesList', (list) => {
      // Restore dashboard after a reconnect/reload.
      list.forEach(g => {
        games.set(g.code, { code: g.code, shootAgainOnHit: g.shootAgainOnHit, started: g.started, players: g.players });
        updateCard(games.get(g.code));
      });
    });

    connection.on('Error', (msg) => toast(msg));

    connection.onreconnecting(() => toast('Reconnecting…'));
    connection.onreconnected(async () => {
      toast('Reconnected.');
      await connection.invoke('ListGames'); // re-sync dashboard
    });

    await connection.start();
  }

  async function invokeSafe(method, ...args) {
    try {
      if (!connection) { await loadConfig(); await connect(); }
      await connection.invoke(method, ...args);
    } catch (err) {
      console.error(err);
      toast(`Action failed: ${method}`);
    }
  }

  // ===== Create button =====

  let started = false;
  async function ensureConnected() {
    if (started) return;
    await loadConfig();
    await connect();
    started = true;
  }

  createBtn.addEventListener('click', async () => {
    createBtn.disabled = true;
    try {
      await ensureConnected();
      await connection.invoke('CreateGame', hostNameInput.value || 'Host', shootAgainInput.checked);
    } catch (err) {
      console.error(err);
      toast('Could not create game.');
    } finally {
      createBtn.disabled = false;
    }
  });

  // Re-sync after reload if the same connection somehow persists (rare); safe no-op.
  window.addEventListener('beforeunload', () => { /* sockets close; host rooms are cleaned server-side */ });
})();
