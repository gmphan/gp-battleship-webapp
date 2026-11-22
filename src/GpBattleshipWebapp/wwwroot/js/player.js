/* Player page logic for Battleship over SignalR.
 * Adds: optional "hit = shoot again", reconnect support (stable token in
 * localStorage), sound effects and battle animations.
 */
(() => {
  const BOARD = 10;
  const SOUND_ON = () => window.SFX && !window.SFX.isMuted();

  // ----- DOM -----
  const joinPanel = document.getElementById('joinPanel');
  const placePanel = document.getElementById('placePanel');
  const battlePanel = document.getElementById('battlePanel');
  const nameInput = document.getElementById('nameInput');
  const joinBtn = document.getElementById('joinBtn');
  const shipListEl = document.getElementById('shipList');
  const placeGrid = document.getElementById('placeGrid');
  const readyBtn = document.getElementById('readyBtn');
  const enemyGrid = document.getElementById('enemyGrid');
  const ownGrid = document.getElementById('ownGrid');
  const turnBanner = document.getElementById('turnBanner');
  const feed = document.getElementById('feed');
  const toastEl = document.getElementById('toast');
  const muteBtn = document.getElementById('muteBtn');

  // ----- State -----
  const params = new URLSearchParams(location.search);
  const room = (params.get('room') || '').toUpperCase();
  let connection = null;
  let myId = null;
  let fleetSpec = {};
  let shipOrder = [];
  let selectedShip = null;
  let orientation = 'H';
  let own = {};                 // "x,y" -> shipName
  const enemyShots = {};        // "x,y" -> 'hit' | 'miss'
  let myTurn = false;
  let gameOver = false;
  let shootAgainOnHit = false;

  // Reconnect/pause countdown (opponent dropped — count down the grace window).
  let pauseTimer = null;       // setInterval handle
  let pauseDeadline = 0;       // epoch ms when the grace window expires
  let pausedName = '';         // who we're waiting on

  const tokenKey = `bs_token_${room}`;

  // ----- Pause countdown -----
  function clearPauseCountdown() {
    if (pauseTimer) { clearInterval(pauseTimer); pauseTimer = null; }
    pausedName = '';
  }

  function startPauseCountdown(name, graceSeconds) {
    clearPauseCountdown();
    pausedName = name || 'Opponent';
    pauseDeadline = Date.now() + Math.max(0, graceSeconds) * 1000;
    renderPauseBanner();
    pauseTimer = setInterval(() => {
      if (Date.now() >= pauseDeadline) {
        clearPauseCountdown();
        turnBanner.textContent = `${pausedName} didn't return — ending…`;
        turnBanner.className = 'banner wait';
        return;
      }
      renderPauseBanner();
    }, 1000);
  }

  function renderPauseBanner() {
    const left = Math.max(0, pauseDeadline - Date.now());
    const s = Math.ceil(left / 1000);
    const mm = Math.floor(s / 60);
    const ss = String(s % 60).padStart(2, '0');
    const pretty = mm > 0 ? `${mm}:${ss}` : `${s}s`;
    turnBanner.textContent = `⏸ ${pausedName} disconnected — ${pretty} to reconnect…`;
    turnBanner.className = 'banner wait';
  }

  // ----- Helpers -----
  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    clearTimeout(toast._t);
    toast._t = setTimeout(() => toastEl.classList.remove('show'), 3200);
  }
  function log(msg) {
    const d = document.createElement('div');
    d.textContent = msg;
    feed.appendChild(d);
    feed.scrollTop = feed.scrollHeight;
  }
  const key = (x, y) => `${x},${y}`;
  const cellName = (x, y) => String.fromCharCode(65 + x) + (y + 1);
  const shipSlug = (name) => 'ship-' + name.toLowerCase().replace(/[^a-z0-9]/g, '');

  // Host ended the game: show an end state and offer to rejoin fresh.
  function handleGameEnd(reason) {
    gameOver = true;
    myTurn = false;
    clearPauseCountdown();
    toast(reason);
    turnBanner.textContent = reason;
    turnBanner.className = 'banner wait';
    log(reason);
    if (enemyGrid) enemyGrid.classList.add('disabled');
    // The seat is gone; clear the saved token so a reload doesn't try to reconnect.
    localStorage.removeItem(tokenKey);
  }

  // Floating combat text near a grid cell.
  function floatText(grid, x, y, text, color) {
    const c = grid.querySelector(`[data-x="${x}"][data-y="${y}"]`);
    if (!c) return;
    const rect = c.getBoundingClientRect();
    const el = document.createElement('div');
    el.className = 'float-text';
    el.textContent = text;
    el.style.color = color;
    el.style.left = `${rect.left + rect.width / 2 - 20}px`;
    el.style.top = `${rect.top}px`;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 950);
  }

  // Depends on DOM readiness for correct rect; call after paint.
  const afterPaint = (fn) => requestAnimationFrame(() => requestAnimationFrame(fn));

  // Wrap a 10x10 .grid with A–J row labels and 1–10 column labels, like a real
  // Battleship board. The strips use the same 32px cells + 3px gap + 6px padding
  // as the grid so every label lines up with its row/column. Idempotent.
  function addBoardLabels(gridEl) {
    if (!gridEl || gridEl.dataset.labeled === '1') return;
    gridEl.dataset.labeled = '1';

    const frame = document.createElement('div');
    frame.className = 'board-frame';

    const corner = document.createElement('div');
    corner.className = 'board-corner';

    const cols = document.createElement('div');
    cols.className = 'board-cols';
    for (let x = 0; x < BOARD; x++) {
      const s = document.createElement('span');
      s.textContent = String(x + 1);
      cols.appendChild(s);
    }

    const rows = document.createElement('div');
    rows.className = 'board-rows';
    for (let y = 0; y < BOARD; y++) {
      const s = document.createElement('span');
      s.textContent = String.fromCharCode(65 + y);
      rows.appendChild(s);
    }

    // Replace gridEl with the frame, then place the grid into the bottom-right slot.
    gridEl.parentNode.insertBefore(frame, gridEl);
    frame.appendChild(corner);
    frame.appendChild(cols);
    frame.appendChild(rows);
    frame.appendChild(gridEl);
  }

  // Keep-alive tuning so idle games don't silently time out. The server's default
  // keep-alive is 15s; we allow a generous client timeout so a phone that briefly
  // sleeps/backgrounds isn't dropped before it can reconnect.
  const KEEP_ALIVE_MS = 15 * 1000;
  const SERVER_TIMEOUT_MS = 60 * 1000;

  // ===== Connection =====
  async function connect() {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/battleship', {
        keepAliveIntervalInMilliseconds: KEEP_ALIVE_MS,
        serverTimeoutInMilliseconds: SERVER_TIMEOUT_MS,
      })
      .withAutomaticReconnect({
        // Retry forever with a capped backoff, so we never give up while the
        // server holds our seat (grace window is 30 min).
        nextRetryDelayInMilliseconds: (ctx) =>
          Math.min(1000 * Math.pow(2, Math.min(ctx.previousRetryCount, 5)), 30000),
      })
      .build();

    connection.on('GameJoined', (code, id, spec, token, saoh) => {
      myId = id;
      shootAgainOnHit = !!saoh;
      fleetSpec = parseFleet(spec);
      localStorage.setItem(tokenKey, token);
      joinPanel.classList.add('hidden');
      startPlacement();
    });

    connection.on('LobbyUpdated', () => { /* light for players */ });

    connection.on('GameStarted', (firstTurnId) => {
      placePanel.classList.add('hidden');
      battlePanel.classList.remove('hidden');
      buildBattleGrids();
      myTurn = firstTurnId === myId;
      updateTurnBanner();
      if (myTurn) { log('Game started — fire at the enemy!'); SFX.play('turn'); }
      else log('Game started — opponent fires first.');
    });

    connection.on('ShotResult', (by, x, y, hit, sunk, over) => {
      if (by === myId) {
        enemyShots[key(x, y)] = hit ? 'hit' : 'miss';
        animateShot(enemyGrid, x, y, hit);
        if (hit) {
          SFX.play(sunk ? 'sunk' : 'hit');
          floatText(enemyGrid, x, y, sunk ? 'SUNK!' : 'HIT!', '#ef4444');
        } else {
          SFX.play('miss');
          floatText(enemyGrid, x, y, 'MISS', '#cbd5e1');
        }
        log(`${hit ? '💥 HIT' : '💦 miss'} at ${cellName(x, y)}${sunk ? ' — you sunk their ' + sunk : ''}`);
      } else {
        animateShot(ownGrid, x, y, hit, /*incoming*/ true);
        if (hit) {
          SFX.play(sunk ? 'sunk' : 'hit');
          shake(ownGrid);
          floatText(ownGrid, x, y, sunk ? 'SUNK!' : 'HIT!', '#ef4444');
        } else {
          SFX.play('miss');
          floatText(ownGrid, x, y, 'MISS', '#cbd5e1');
        }
        log(`${hit ? '🔥 They hit your ship' : '🛡️ they missed'} at ${cellName(x, y)}`);
      }
    });

    connection.on('TurnChanged', (turnId, keepTurn) => {
      clearPauseCountdown();  // play resumed
      myTurn = turnId === myId;
      updateTurnBanner();
      if (myTurn && !gameOver) SFX.play('turn');
      if (keepTurn) log('A hit! The same player shoots again.');
    });

    connection.on('GameOver', (winner) => {
      gameOver = true;
      myTurn = false;
      clearPauseCountdown();
      enemyGrid.classList.add('disabled');
      const win = winner === myId;
      turnBanner.textContent = win ? '🏆 You sank the enemy fleet — you win!' : '💀 Your fleet was destroyed.';
      turnBanner.className = 'banner ' + (win ? 'win' : 'wait');
      SFX.play(win ? 'win' : 'lose');
      log(win ? 'Victory!' : 'Defeat.');
    });

    connection.on('PauseState', (paused) => {
      if (paused) {
        // If we don't yet know the name/countdown, show a generic pause; the
        // OpponentDisconnected event (sent alongside) will upgrade to a countdown.
        if (!pauseTimer) {
          turnBanner.textContent = '⏸ Paused — opponent reconnecting…';
          turnBanner.className = 'banner wait';
        }
      } else {
        clearPauseCountdown();
        updateTurnBanner();
      }
    });

    connection.on('OpponentDisconnected', (name, secs) => {
      log(`${name} disconnected — waiting up to ${secs}s for them to return.`);
      startPauseCountdown(name, secs);
    });
    connection.on('OpponentLeft', () => {
      clearPauseCountdown();
      turnBanner.textContent = 'Opponent left the game.';
      turnBanner.className = 'banner wait';
      log('Opponent left.');
    });
    connection.on('GameClosed', () => handleGameEnd('The host closed the game.'));
    connection.on('GameAborted', (reason) => handleGameEnd(reason || 'The host aborted the game.'));

    // Host started a rematch in the same room — reset the client and re-place.
    connection.on('NewGameStarted', (saoh) => {
      shootAgainOnHit = !!saoh;
      gameOver = false;
      myTurn = false;
      clearPauseCountdown();
      own = {};
      Object.keys(enemyShots).forEach(k => delete enemyShots[k]);
      battlePanel.classList.add('hidden');
      feed.innerHTML = '';
      log('New game started — place your fleet again.');
      startPlacement();
      readyBtn.disabled = true;
      readyBtn.textContent = 'Ready';
    });

    connection.on('Error', (msg) => toast(msg));

    connection.on('ReconnectFailed', (msg) => {
      // Seat gone — fall back to fresh join UI.
      localStorage.removeItem(tokenKey);
      joinPanel.classList.remove('hidden');
      toast(msg || 'Could not reconnect.');
    });

    connection.on('Reconnected', (state) => restoreState(state));

    // Auto-reconnect lifecycle: after the socket drops, try to re-claim our seat.
    connection.onreconnecting(() => {
      turnBanner.textContent = '🔌 Connection lost — reconnecting…';
      turnBanner.className = 'banner wait';
    });
    connection.onreconnected(async () => {
      const token = localStorage.getItem(tokenKey);
      if (token) await tryReconnect(token);
    });

    // If automatic reconnect has fully given up (or the socket closed), keep trying
    // to re-establish so an idle/backgrounded player can always come back within the
    // grace window instead of being dropped and timing out.
    connection.onclose(async () => {
      const token = localStorage.getItem(tokenKey);
      if (!token || gameOver) return;
      let delay = 1000;
      while (connection.state === signalR.HubConnectionState.Disconnected && !gameOver) {
        try {
          await new Promise(r => setTimeout(r, delay));
          await connection.start();
          await tryReconnect(token);
          break;
        } catch (e) {
          delay = Math.min(delay * 2, 30000);
        }
      }
    });

    await connection.start();
  }

  async function tryReconnect(token) {
    try {
      await connection.invoke('Reconnect', room, token);
    } catch (e) {
      console.error(e);
    }
  }

  function restoreState(state) {
    myId = state.playerId;
    shootAgainOnHit = !!state.shootAgainOnHit;
    fleetSpec = parseFleet(state.fleetSpec);

    // Rebuild own fleet map from ships.
    own = {}; // eslint-disable-line no-const-assign
    (state.ships || []).forEach(s =>
      s.cells.forEach(c => { own[`${c.x},${c.y}`] = s.name; }));

    // If game already running, jump straight to battle.
    if (state.started) {
      joinPanel.classList.add('hidden');
      placePanel.classList.add('hidden');
      battlePanel.classList.remove('hidden');
      buildBattleGrids();

      // Re-apply my previous shots on the enemy board.
      (state.myShots || []).forEach(s => {
        enemyShots[`${s.x},${s.y}`] = s.hit ? 'hit' : 'miss';
        paintStatic(enemyGrid, s.x, s.y, s.hit ? 'hit' : 'miss', s.hit ? '💥' : '•');
      });

      myTurn = state.currentTurnId === myId;
      updateTurnBanner();
      log('Reconnected — game resumed.');
    } else {
      // Still in placement: show placement again (server keeps our ships if any).
      joinPanel.classList.add('hidden');
      startPlacement();
      if ((state.ships || []).length) {
        // We had already placed; let them re-ready.
        shipOrder.forEach(s => { s.placed = !!own && Object.values(own).includes(s.name); });
        repaintOwnPlacement();
        readyBtn.disabled = shipOrder.some(s => !s.placed);
        if (!readyBtn.disabled) readyBtn.textContent = 'Ready ✓';
      }
      log('Reconnected — place your fleet.');
    }
  }

  function parseFleet(spec) {
    const out = {};
    (spec || '').split(',').forEach(pair => {
      const [n, len] = pair.split(':');
      if (n) out[n] = parseInt(len, 10);
    });
    return out;
  }

  // ===== Placement =====
  function startPlacement() {
    placePanel.classList.remove('hidden');
    shipOrder = Object.entries(fleetSpec).map(([name, length]) => ({ name, length, placed: false }));
    selectedShip = shipOrder[0]?.name || null;
    renderShipList();
    buildPlaceGrid();
  }

  function renderShipList() {
    shipListEl.innerHTML = '';
    shipOrder.forEach(s => {
      const el = document.createElement('span');
      el.className = 'ship-item ' + shipSlug(s.name) +
        (s.placed ? ' placed' : '') + (selectedShip === s.name ? ' vertical' : '');
      const swatch = document.createElement('span');
      swatch.className = 'ship-swatch';
      const label = `${s.name} (${s.length})` + (selectedShip === s.name ? (orientation === 'H' ? ' ↔' : ' ↕') : '');
      el.appendChild(swatch);
      el.appendChild(document.createTextNode(label));
      el.addEventListener('click', () => {
        if (s.placed) { removeShip(s.name); return; }
        if (selectedShip === s.name) orientation = orientation === 'H' ? 'V' : 'H';
        else selectedShip = s.name;
        renderShipList();
      });
      shipListEl.appendChild(el);
    });
  }

  function buildPlaceGrid() {
    placeGrid.innerHTML = '';
    for (let y = 0; y < BOARD; y++) {
      for (let x = 0; x < BOARD; x++) {
        const c = document.createElement('div');
        c.className = 'cell';
        c.dataset.x = x; c.dataset.y = y;
        c.addEventListener('click', () => onPlaceClick(x, y));
        placeGrid.appendChild(c);
      }
    }
    repaintOwnPlacement();
  }

  function shipCellsFor(name, x, y, horiz, length) {
    const cells = [];
    for (let i = 0; i < length; i++) {
      const cx = horiz ? x + i : x;
      const cy = horiz ? y : y + i;
      if (cx >= BOARD || cy >= BOARD) return null;
      cells.push([cx, cy]);
    }
    return cells;
  }
  const cellsFree = (cells) => cells.every(([cx, cy]) => !own[key(cx, cy)]);

  function onPlaceClick(x, y) {
    const ship = shipOrder.find(s => s.name === selectedShip && !s.placed);
    if (!ship) return;
    const cells = shipCellsFor(ship.name, x, y, orientation === 'H', ship.length);
    if (!cells) { toast('Ship goes off the board.'); return; }
    if (!cellsFree(cells)) { toast('Ships cannot overlap.'); return; }

    cells.forEach(([cx, cy]) => { own[key(cx, cy)] = ship.name; });
    ship.placed = true;
    SFX.play('place');

    const next = shipOrder.find(s => !s.placed);
    selectedShip = next ? next.name : null;
    renderShipList();
    repaintOwnPlacement();
    readyBtn.disabled = shipOrder.some(s => !s.placed);
  }

  function removeShip(name) {
    Object.keys(own).forEach(k => { if (own[k] === name) delete own[k]; });
    const ship = shipOrder.find(s => s.name === name);
    if (ship) ship.placed = false;
    selectedShip = name;
    renderShipList();
    repaintOwnPlacement();
    readyBtn.disabled = true;
  }

  function repaintOwnPlacement() {
    [...placeGrid.children].forEach(c => {
      const x = +c.dataset.x, y = +c.dataset.y;
      c.className = 'cell';
      c.textContent = '';
    });
    layoutShips(placeGrid);
  }

  // ----- Graphical ship hulls -----
  // Group the `own` map into ship runs, then decorate each occupied cell so that
  // adjacent cells of the same ship visually fuse into one hull (n/s/e/w + bow/mid/stern).
  function layoutShips(gridEl) {
    // Reset any previous hull decoration (keep shot markers like ship-hit/hit/miss).
    const PRESERVE = new Set(['cell', 'ship-hit', 'hit', 'miss', 'drop-target']);
    [...gridEl.children].forEach(c => {
      c.classList.remove('ship', 'orient-h', 'orient-v', 'ship-bow', 'ship-mid', 'ship-stern',
        'conn-n', 'conn-s', 'conn-e', 'conn-w');
      // Remove per-ship-type color classes (ship-carrier, etc.) but keep shot-state classes.
      [...c.classList].forEach(cl => {
        if (cl.startsWith('ship-') && !PRESERVE.has(cl)) c.classList.remove(cl);
      });
      c.removeAttribute('data-ship');
    });

    // Re-apply hull classes for each occupied cell.
    Object.entries(own).forEach(([k, name]) => {
      const [x, y] = k.split(',').map(Number);
      const cell = gridEl.querySelector(`[data-x="${x}"][data-y="${y}"]`);
      if (!cell) return;
      cell.classList.add('ship', shipSlug(name));
      cell.dataset.ship = name;

      const hasLeft = !!own[key(x - 1, y)];
      const hasRight = !!own[key(x + 1, y)];
      const hasUp = !!own[key(x, y - 1)];
      const hasDown = !!own[key(x, y + 1)];

      const horiz = hasLeft || hasRight;
      cell.classList.add(horiz ? 'orient-h' : 'orient-v');
      if (hasLeft) cell.classList.add('conn-w');
      if (hasRight) cell.classList.add('conn-e');
      if (hasUp) cell.classList.add('conn-n');
      if (hasDown) cell.classList.add('conn-s');

      // Bow = one end, stern = the other; anything in between is mid.
      const degree = (hasLeft ? 1 : 0) + (hasRight ? 1 : 0) + (hasUp ? 1 : 0) + (hasDown ? 1 : 0);
      if (degree === 1) cell.classList.add('ship-bow');
      else if (degree === 2) cell.classList.add('ship-mid');
      else cell.classList.add('ship-mid');
    });
  }

  readyBtn.addEventListener('click', async () => {
    const ships = shipOrder.map(s => {
      const cells = Object.entries(own)
        .filter(([, name]) => name === s.name)
        .map(([k]) => { const [x, y] = k.split(',').map(Number); return { x, y }; });
      return { name: s.name, cells };
    });
    try {
      await connection.invoke('PlaceShips', ships);
      readyBtn.disabled = true;
      readyBtn.textContent = 'Ready ✓';
    } catch (err) {
      console.error(err);
      toast('Could not submit ships.');
    }
  });

  // ===== Battle =====
  function buildBattleGrids() {
    buildGrid(enemyGrid, onFire);
    buildGrid(ownGrid, null);
    layoutShips(ownGrid);
  }

  function buildGrid(grid, onClick) {
    grid.innerHTML = '';
    for (let y = 0; y < BOARD; y++) {
      for (let x = 0; x < BOARD; x++) {
        const c = document.createElement('div');
        c.className = 'cell';
        c.dataset.x = x; c.dataset.y = y;
        if (onClick) c.addEventListener('click', () => onClick(x, y));
        grid.appendChild(c);
      }
    }
  }

  function paintStatic(grid, x, y, cls, symbol) {
    const c = grid.querySelector(`[data-x="${x}"][data-y="${y}"]`);
    if (c) { c.classList.add(cls); c.innerHTML = `<span class="marker">${symbol}</span>`; }
  }

  // Animated shot marker + splash/explosion.
  function animateShot(grid, x, y, hit, incoming) {
    const c = grid.querySelector(`[data-x="${x}"][data-y="${y}"]`);
    if (!c) return;
    c.classList.add(hit ? (incoming ? 'ship-hit' : 'hit') : 'miss');

    // splash/impact overlay
    const impact = document.createElement('span');
    impact.className = 'impact';
    c.appendChild(impact);
    setTimeout(() => impact.remove(), 550);

    const mark = document.createElement('span');
    mark.className = 'marker';
    mark.textContent = hit ? '💥' : '•';
    // clear previous content then add marker
    const prev = c.querySelector('.marker');
    if (prev) prev.remove();
    c.appendChild(mark);
  }

  function shake(grid) {
    grid.classList.remove('shake');
    void grid.offsetWidth; // reflow to restart animation
    grid.classList.add('shake');
  }

  async function onFire(x, y) {
    if (gameOver) return;
    if (!myTurn) { toast("It's not your turn."); return; }
    if (enemyShots[key(x, y)]) { toast('Already fired there.'); return; }
    SFX.play('fire');
    try {
      await connection.invoke('Fire', x, y);
    } catch (err) {
      console.error(err);
      toast('Fire failed.');
    }
  }

  function updateTurnBanner() {
    if (gameOver) return;
    enemyGrid.classList.toggle('disabled', !myTurn);
    if (myTurn) {
      turnBanner.textContent = shootAgainOnHit
        ? '🎯 Your turn — a hit lets you shoot again!'
        : '🎯 Your turn — fire at the enemy!';
      turnBanner.className = 'banner turn';
    } else {
      turnBanner.textContent = '⏳ Waiting for opponent…';
      turnBanner.className = 'banner wait';
    }
  }

  // ===== Sound toggle =====
  muteBtn.textContent = SFX.isMuted() ? '🔇' : '🔊';
  muteBtn.addEventListener('click', () => {
    const m = SFX.toggle();
    muteBtn.textContent = m ? '🔇' : '🔊';
  });

  // ===== Boot =====
  joinBtn.addEventListener('click', async () => {
    if (!room) { toast('No room code in the URL.'); return; }
    SFX.unmute(); // user gesture unlocks audio
    joinBtn.disabled = true;
    try {
      await connect();
      // If we have a prior token for this room, try to resume; else join fresh.
      const existing = localStorage.getItem(tokenKey);
      if (existing) {
        await tryReconnect(existing);
      } else {
        await connection.invoke('JoinGame', room, nameInput.value || 'Player');
      }
    } catch (err) {
      console.error(err);
      toast('Could not join game.');
      joinBtn.disabled = false;
    }
  });

  // Wrap each board with A–J / 1–10 labels (like the real game).
  addBoardLabels(placeGrid);
  addBoardLabels(enemyGrid);
  addBoardLabels(ownGrid);

  if (!room) {
    joinPanel.innerHTML = '<h2>Missing room code</h2><p class="muted">Scan the QR code from the host screen.</p>';
  }
})();
