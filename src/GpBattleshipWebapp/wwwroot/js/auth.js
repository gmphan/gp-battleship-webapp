/* Auth helpers for the admin console.
 * Talks to /api/auth/* (cookie-based session). Exposes window.Auth with the
 * current user and a promise that resolves once we know the auth state.
 */
(() => {
  window.Auth = {
    user: null,          // { username, role, source } | null
    get isSuperAdmin() { return this.user && this.user.role === 'SuperAdmin'; },
    get isAuthenticated() { return !!this.user; },
  };

  async function fetchMe() {
    try {
      const res = await fetch('/api/auth/me', { credentials: 'same-origin' });
      if (!res.ok) return null;
      const data = await res.json();
      return data && data.authenticated ? data : null;
    } catch {
      return null;
    }
  }

  window.Auth.login = async (username, password) => {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ username, password }),
    });
    if (!res.ok) {
      let msg = 'Sign in failed.';
      try { const j = await res.json(); if (j && j.error) msg = j.error; } catch {}
      throw new Error(msg);
    }
    window.Auth.user = await fetchMe();
    return window.Auth.user;
  };

  window.Auth.logout = async () => {
    try { await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' }); } catch {}
    window.Auth.user = null;
  };

  // Fetch current user AND record it on window.Auth, so callers can check state.
  window.Auth.me = async () => {
    const u = await fetchMe();
    window.Auth.user = u;
    return u;
  };
})();
