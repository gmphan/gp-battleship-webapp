/* Tiny Web Audio sound engine — no asset files needed.
 * Exposes window.SFX with play(name) and a mute toggle.
 * The AudioContext is created lazily on first user gesture to satisfy autoplay policies.
 */
(() => {
  let ctx = null;
  let muted = localStorage.getItem('bs_muted') === '1';

  function ensureCtx() {
    if (!ctx) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (AC) ctx = new AC();
    }
    if (ctx && ctx.state === 'suspended') ctx.resume();
    return ctx;
  }

  function tone({ freq = 440, type = 'sine', dur = 0.15, gain = 0.2, sweepTo = null, delay = 0 }) {
    const c = ensureCtx();
    if (!c || muted) return;
    const t0 = c.currentTime + delay;
    const osc = c.createOscillator();
    const g = c.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, t0);
    if (sweepTo != null) osc.frequency.exponentialRampToValueAtTime(sweepTo, t0 + dur);
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(gain, t0 + 0.01);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    osc.connect(g).connect(c.destination);
    osc.start(t0);
    osc.stop(t0 + dur + 0.02);
  }

  function noise({ dur = 0.3, gain = 0.25, delay = 0, filterFreq = 1000 }) {
    const c = ensureCtx();
    if (!c || muted) return;
    const t0 = c.currentTime + delay;
    const bufferSize = Math.floor(c.sampleRate * dur);
    const buffer = c.createBuffer(1, bufferSize, c.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < bufferSize; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / bufferSize);
    const src = c.createBufferSource();
    src.buffer = buffer;
    const filter = c.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = filterFreq;
    const g = c.createGain();
    g.gain.setValueAtTime(gain, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    src.connect(filter).connect(g).connect(c.destination);
    src.start(t0);
  }

  const sounds = {
    // firing a shot: quick descending "pew"
    fire() { tone({ freq: 880, sweepTo: 220, type: 'square', dur: 0.12, gain: 0.12 }); },
    // miss: watery plop
    miss() { noise({ dur: 0.35, gain: 0.18, filterFreq: 700 }); tone({ freq: 300, sweepTo: 120, type: 'sine', dur: 0.3, gain: 0.1 }); },
    // hit: explosion burst
    hit() {
      noise({ dur: 0.4, gain: 0.4, filterFreq: 1400 });
      tone({ freq: 160, sweepTo: 40, type: 'sawtooth', dur: 0.35, gain: 0.25 });
    },
    // sunk: bigger, layered explosion
    sunk() {
      noise({ dur: 0.7, gain: 0.5, filterFreq: 900 });
      tone({ freq: 200, sweepTo: 30, type: 'sawtooth', dur: 0.6, gain: 0.3 });
      tone({ freq: 120, sweepTo: 25, type: 'square', dur: 0.7, gain: 0.2, delay: 0.08 });
    },
    // your turn: gentle chime
    turn() { tone({ freq: 660, type: 'sine', dur: 0.12, gain: 0.12 }); tone({ freq: 990, type: 'sine', dur: 0.16, gain: 0.12, delay: 0.12 }); },
    // victory: rising arpeggio
    win() {
      [523, 659, 784, 1046].forEach((f, i) =>
        tone({ freq: f, type: 'triangle', dur: 0.25, gain: 0.18, delay: i * 0.13 }));
    },
    // defeat: descending
    lose() {
      [523, 415, 330, 220].forEach((f, i) =>
        tone({ freq: f, type: 'sine', dur: 0.3, gain: 0.16, delay: i * 0.16 }));
    },
    // placed a ship
    place() { tone({ freq: 440, type: 'triangle', dur: 0.08, gain: 0.1 }); }
  };

  window.SFX = {
    play(name) { const fn = sounds[name]; if (fn) { try { fn(); } catch (e) { /* ignore */ } } },
    isMuted: () => muted,
    toggle() {
      muted = !muted;
      localStorage.setItem('bs_muted', muted ? '1' : '0');
      if (!muted) ensureCtx();
      return muted;
    },
    unmute() { if (muted) { muted = false; localStorage.setItem('bs_muted', '0'); } ensureCtx(); }
  };
})();
