/* Admin console logic for Battleship over SignalR.
 * Requires an authenticated admin (cookie session via /api/auth/login).
 * A SuperAdmin additionally sees the "Admins" panel to manage admin accounts.
 * Runs MANY concurrent games from one connection; each game is a dashboard card.
 */
(() => {
  // ---- elements ----
  const startPanel = document.getElementById('startPanel');
  const gamesEl = document.getElementById('games');
  const emptyHint = document.getElementById('emptyHint');
  const createBtn = document.getElementById('createBtn');
  const hostNameInput = document.getElementById('hostName');
  const shootAgainInput = document.getElementById('shootAgain');
  const toastEl = document.getElementById('toast');

  const loginOverlay = document.getElementById('loginOverlay');
  const loginForm = document.getElementById('loginForm');
  const loginUser = document.getElementById('loginUser');
  const loginPass = document.getElementById('loginPass');
  const loginBtn = document.getElementById('loginBtn');
  const loginError = document.getElementById('loginError');

  const whoami = document.getElementById('whoami');
  const whoamiName = document.getElementById('whoamiName');
  const logoutBtn = document.getElementById('logoutBtn');

  const adminsPanel = document.getElementById('adminsPanel');
  const adminsList = document.getElementById('adminsList');
  const addAdminToggle = document.getElementById('addAdminToggle');
  const addAdminForm = document.getElementById('addAdminForm');
  const newAdminUser = document.getElementById('newAdminUser');
  const newAdminPass = document.getElementById('newAdminPass');
  const newAdminRole = document.getElementById('newAdminRole');
  const addAdminError = document.getElementById('addAdminError');

  let connection = null;
  let publicBaseUrl = '';
  let connected = false;

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

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  async function loadConfig() {
    try {
      const res = await fetch('/api/config');
      const cfg = await res.json();
      publicBaseUrl = (cfg.publicBaseUrl || '').replace(/\/$/, '');
    } catch { /* ignore */ }
  }

  // ===== Auth bootstrap =====

  function showLogin(msg) {
    loginOverlay.classList.remove('hidden');
    if (msg) loginError.textContent = msg;
    loginUser.focus();
  }

  function hideLogin() {
    loginOverlay.classList.add('hidden');
    loginError.textContent = '';
  }

  loginForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    loginBtn.disabled = true;
    loginError.textContent = '';
    try {
      await window.Auth.login(loginUser.value.trim(), loginPass.value);
      hideLogin();
      await afterLogin();
    } catch (err) {
      loginError.textContent = err.message || 'Sign in failed.';
    } finally {
      loginBtn.disabled = false;
    }
  });

  logoutBtn.addEventListener('click', async () => {
    await window.Auth.logout();
    location.reload();
  });

  async function bootstrap() {
    const me = await window.Auth.me();
    if (me) {
      hideLogin();
      await afterLogin();
    } else {
      showLogin();
    }
  }

  async function afterLogin() {
    const user = window.Auth.user;
    whoamiName.textContent = `${user.username} (${user.role})`;
    whoami.classList.remove('hidden');
    startPanel.classList.remove('hidden');

    // SuperAdmin-only admin management.
    if (window.Auth.isSuperAdmin) {
      adminsPanel.classList.remove('hidden');
      loadAdmins();
    } else {
      adminsPanel.classList.add('hidden');
    }

    await loadConfig();
    await connect();
  }

  // ===== Admins panel (SuperAdmin only) =====

  addAdminToggle.addEventListener('click', () => {
    addAdminForm.classList.toggle('hidden');
    addAdminError.textContent = '';
  });

  addAdminForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    addAdminError.textContent = '';
    try {
      const res = await fetch('/api/admins', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'same-origin',
        body: JSON.stringify({
          username: newAdminUser.value.trim(),
          password: newAdminPass.value,
          role: newAdminRole.value,
        }),
      });
      if (!res.ok) {
        let m = 'Could not create admin.';
        try { const j = await res.json(); if (j && j.error) m = j.error; } catch {}
        throw new Error(m);
      }
      newAdminUser.value = ''; newAdminPass.value = '';
      addAdminForm.classList.add('hidden');
      await loadAdmins();
    } catch (err) {
      addAdminError.textContent = err.message;
    }
  });

  async function loadAdmins() {
    try {
      const res = await fetch('/api/admins', { credentials: 'same-origin' });
      if (!res.ok) return;
      const list = await res.json();
      renderAdmins(list);
    } catch { /* ignore */ }
  }

  function renderAdmins(list) {
    adminsList.innerHTML = '';
    list.forEach(a => {
      const row = document.createElement('div');
      row.className = 'admin-row';
      const badge = a.isConfig ? '<span class="pill config">config</span>' : '<span class="pill db">db</span>';
      row.innerHTML = `
        <span class="admin-name">${escapeHtml(a.username)}</span>
        <span class="pill ${a.role === 'SuperAdmin' ? 'super' : 'admin'}">${escapeHtml(a.role)}</span>
        ${badge}
        ${a.isActive ? '' : '<span class="pill off">disabled</span>'}
        <span class="admin-actions"></span>
      `;
      const actions = row.querySelector('.admin-actions');
      if (!a.isConfig) {
        const roleBtn = document.createElement('button');
        roleBtn.className = 'secondary small';
        roleBtn.textContent = a.role === 'SuperAdmin' ? 'Make Admin' : 'Make SuperAdmin';
        roleBtn.addEventListener('click', () => updateAdmin(a.id, { role: a.role === 'SuperAdmin' ? 'Admin' : 'SuperAdmin' }));
        const actBtn = document.createElement('button');
        actBtn.className = 'secondary small';
        actBtn.textContent = a.isActive ? 'Disable' : 'Enable';
        actBtn.addEventListener('click', () => updateAdmin(a.id, { isActive: !a.isActive }));
        const delBtn = document.createElement('button');
        delBtn.className = 'danger small';
        delBtn.textContent = 'Delete';
        delBtn.addEventListener('click', () => deleteAdmin(a.id, a.username));
        actions.append(roleBtn, actBtn, delBtn);
      } else {
        actions.innerHTML = '<span class="muted small">managed in variables.env</span>';
      }
      adminsList.appendChild(row);
    });
  }

  async function updateAdmin(id, patch) {
    try {
      const res = await fetch(`/api/admins/${id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'same-origin',
        body: JSON.stringify(patch),
      });
      if (!res.ok) { let m = 'Update failed.'; try { const j = await res.json(); if (j&&j.error) m=j.error; } catch {} throw new Error(m); }
      await loadAdmins();
    } catch (err) { toast(err.message); }
  }

  async function deleteAdmin(id, name) {
    if (!confirm(`Delete admin "${name}"? This cannot be undone.`)) return;
    try {
      const res = await fetch(`/api/admins/${id}`, { method: 'DELETE', credentials: 'same-origin' });
      if (!res.ok) { let m = 'Delete failed.'; try { const j = await res.json(); if (j&&j.error) m=j.error; } catch {} throw new Error(m); }
      await loadAdmins();
    } catch (err) { toast(err.message); }
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
    refs.newGameBtn.disabled = (g.players || []).length < 2;
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

  // ===== Connection =====

  async function connect() {
    if (connected) return;
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/battleship')          // cookie auth is sent automatically
      .withAutomaticReconnect()
      .build();

    connection.on('GameCreated', (code, hostId, spec, shootAgainOnHit) => {
      games.set(code, { code, shootAgainOnHit, started: false, players: [] });
      updateCard(games.get(code));
      cardLog(code, 'Game created — waiting for players.');
    });

    connection.on('GameUpdated', (code, started, players) => {
      const g = games.get(code) || { code };
      g.started = started;
      g.players = players;
      games.set(code, g);
      updateCard(g);
    });

    connection.on('ShotResult', (by, x, y, hit, sunk) => {
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

    connection.on('GameRemoved', (code) => {
      if (cards.has(code)) cardLog(code, 'Game removed.');
      removeCard(code);
    });

    connection.on('GamesList', (list) => {
      list.forEach(g => {
        games.set(g.code, { code: g.code, shootAgainOnHit: g.shootAgainOnHit, started: g.started, players: g.players });
        updateCard(games.get(g.code));
      });
    });

    connection.on('Error', (msg) => toast(msg));

    connection.onreconnecting(() => { connected = false; toast('Reconnecting…'); });
    connection.onreconnected(async () => {
      connected = true;
      toast('Reconnected.');
      await resumeHost();
    });

    await connection.start();
    connected = true;
    await resumeHost();
  }

  // Re-claim ownership of our rooms (owner derives from the logged-in session).
  async function resumeHost() {
    try {
      await connection.invoke('ResumeHost');
    } catch (err) {
      console.warn('ResumeHost failed, trying ListGames:', err);
      try { await connection.invoke('ListGames'); } catch { /* ignore */ }
    }
  }

  async function invokeSafe(method, ...args) {
    try {
      if (!connected) { await connect(); }
      await connection.invoke(method, ...args);
    } catch (err) {
      console.error(err);
      toast(`Action failed: ${method}`);
    }
  }

  // ===== Create button =====

  createBtn.addEventListener('click', async () => {
    createBtn.disabled = true;
    try {
      if (!connected) { await connect(); }
      await connection.invoke('CreateGame', hostNameInput.value || 'Host', shootAgainInput.checked);
    } catch (err) {
      console.error('Could not create game:', err);
      toast('Could not create game: ' + (err && err.message ? err.message : err));
    } finally {
      createBtn.disabled = false;
    }
  });

  // ===== Start =====
  bootstrap();
})();
