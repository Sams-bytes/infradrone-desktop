// sensor.js — simulated thermal (IR, white-hot) camera feed with AI detection boxes.
// Procedural textures only (no image files), so it also works fully offline.
// Clearly labelled on screen as a simulated sensor view.
(function () {
  'use strict';

  function makeNoise(size, seed, octaves, base, contrast) {
    const c = document.createElement('canvas'); c.width = c.height = size;
    const g = c.getContext('2d'), img = g.createImageData(size, size);
    let s = seed >>> 0;
    const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
    // Tileable value noise: random lattice per octave, bilinear smooth interpolation.
    const field = new Float32Array(size * size);
    let amp = 1, total = 0;
    for (let o = 0; o < octaves; o++) {
      const cells = 4 << o, lat = new Float32Array(cells * cells);
      for (let i = 0; i < lat.length; i++) lat[i] = rnd();
      const step = size / cells;
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        const gx = x / step, gy = y / step, x0 = Math.floor(gx) % cells, y0 = Math.floor(gy) % cells;
        const x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
        let fx = gx - Math.floor(gx), fy = gy - Math.floor(gy);
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        const a = lat[y0 * cells + x0], b = lat[y0 * cells + x1], cc = lat[y1 * cells + x0], d = lat[y1 * cells + x1];
        field[y * size + x] += amp * ((a * (1 - fx) + b * fx) * (1 - fy) + (cc * (1 - fx) + d * fx) * fy);
      }
      total += amp; amp *= 0.55;
    }
    for (let i = 0; i < field.length; i++) {
      let v = field[i] / total;
      v = base + (v - 0.5) * contrast;
      const q = Math.max(0, Math.min(255, v * 255));
      img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = q; img.data[i * 4 + 3] = 255;
    }
    g.putImageData(img, 0, 0);
    return c;
  }

  class IRFeed {
    constructor(canvas) {
      this.cv = canvas; this.g = canvas.getContext('2d');
      this.w = canvas.width; this.h = canvas.height;
      this.tex = {
        forest: makeNoise(256, 7, 5, 0.47, 1.25),
        terrain: makeNoise(256, 21, 4, 0.42, 0.9),
        bank: makeNoise(256, 99, 4, 0.40, 0.8)
      };
      this.off = { x: 0, y: 0 };
      this.t = 0;
    }

    // ctx: { mode: 'off'|'terrain'|'river'|'forest', speed, hdg, alt, lat, lon, clock,
    //        targets: [{ kind: 'boat'|'person', x, y, label, color, conf, box }], simLabel, rec }
    draw(dt, ctx) {
      const g = this.g, W = this.w, H = this.h;
      this.t += dt;
      g.save();
      g.fillStyle = '#05080b'; g.fillRect(0, 0, W, H);
      if (ctx.mode === 'off') {
        g.fillStyle = '#5b6b7b'; g.font = '600 14px ui-monospace, DejaVu Sans Mono, monospace'; g.textAlign = 'center';
        g.fillText(ctx.standby || 'SENSOR STANDBY', W / 2, H / 2 - 36);
        g.restore(); this.frame(ctx); return;
      }
      // Image motion: proportional to ground speed / altitude (pixels per second).
      const pxPerSec = Math.min(900, (ctx.speed || 0) / Math.max(30, ctx.alt || 100) * 260);
      this.off.y += pxPerSec * dt;
      const zoom = ctx.mode === 'forest' ? 2.2 : 1.6;
      const tex = ctx.mode === 'forest' ? this.tex.forest : ctx.mode === 'river' ? this.tex.bank : this.tex.terrain;
      this.tile(tex, zoom, this.off.x, this.off.y);

      if (ctx.mode === 'river') {
        // Cool water band (darker) running through the frame with a wavy edge.
        const cx = W * (0.46 + 0.04 * Math.sin(this.off.y / 300)), bw = W * 0.34;
        const grd = g.createLinearGradient(cx - bw, 0, cx + bw, 0);
        grd.addColorStop(0, 'rgba(10,14,18,0)'); grd.addColorStop(0.18, 'rgba(14,18,22,0.92)');
        grd.addColorStop(0.82, 'rgba(14,18,22,0.92)'); grd.addColorStop(1, 'rgba(10,14,18,0)');
        g.fillStyle = grd; g.fillRect(cx - bw, 0, bw * 2, H);
        // faint ripples
        g.strokeStyle = 'rgba(80,90,100,0.18)'; g.lineWidth = 1;
        for (let i = 0; i < 8; i++) {
          const y = ((this.off.y * 0.9 + i * 40) % (H + 40)) - 20;
          g.beginPath(); g.moveTo(cx - bw * 0.6, y); g.quadraticCurveTo(cx, y + 6, cx + bw * 0.6, y); g.stroke();
        }
      }

      for (const t of ctx.targets || []) this.target(t);

      // Sensor grain
      g.globalAlpha = 0.07;
      for (let i = 0; i < 300; i++) {
        g.fillStyle = Math.random() < 0.5 ? '#fff' : '#000';
        g.fillRect(Math.random() * W, Math.random() * H, 1.5, 1.5);
      }
      g.globalAlpha = 1;
      g.restore();
      this.frame(ctx);
    }

    tile(tex, zoom, ox, oy) {
      const g = this.g, W = this.w, H = this.h, ts = tex.width * zoom;
      const sx = -(((ox % ts) + ts) % ts), sy = (((oy % ts) + ts) % ts) - ts;
      g.imageSmoothingEnabled = true;
      for (let y = sy; y < H; y += ts) for (let x = sx; x < W; x += ts) g.drawImage(tex, x, y, ts, ts);
    }

    target(t) {
      const g = this.g, W = this.w, H = this.h, x = t.x * W, y = t.y * H;
      g.save();
      if (t.kind === 'boat') {
        // warm wake
        const wake = g.createLinearGradient(x, y, x - 4, y + 70);
        wake.addColorStop(0, 'rgba(200,200,200,0.55)'); wake.addColorStop(1, 'rgba(200,200,200,0)');
        g.fillStyle = wake; g.beginPath(); g.moveTo(x - 9, y + 8); g.lineTo(x + 9, y + 8); g.lineTo(x + 22, y + 70); g.lineTo(x - 22, y + 70); g.closePath(); g.fill();
        // hull and hot engine
        g.translate(x, y); g.rotate((t.angle || 0) * Math.PI / 180);
        g.fillStyle = 'rgba(205,205,205,0.95)';
        g.beginPath(); g.ellipse(0, 0, 8, 22, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#ffffff'; g.beginPath(); g.ellipse(0, 18, 5, 4, 0, 0, Math.PI * 2); g.fill();
        // two persons (hotter)
        g.shadowColor = '#fff'; g.shadowBlur = 8;
        g.beginPath(); g.arc(0, -8, 3.6, 0, Math.PI * 2); g.arc(0, 4, 3.6, 0, Math.PI * 2); g.fill();
        g.setTransform(1, 0, 0, 1, 0, 0);
        this.box(x - 26, y - 34, 52, 64, t);
      } else if (t.kind === 'person') {
        g.shadowColor = '#fff'; g.shadowBlur = 10; g.fillStyle = '#fff';
        g.beginPath(); g.ellipse(x, y, 5, 9, 0.5, 0, Math.PI * 2); g.fill();
        g.beginPath(); g.arc(x - 3, y - 10, 3.5, 0, Math.PI * 2); g.fill();
        g.shadowBlur = 0;
        // canopy partly covering the person (leaves drawn on top)
        g.fillStyle = 'rgba(70,70,70,0.85)';
        const seeds = [[-14, -16, 10, 7], [12, -8, 9, 12], [8, 12, 12, 8], [-12, 10, 8, 10], [16, 18, 7, 6]];
        for (const [dx, dy, rx, ry] of seeds) { g.beginPath(); g.ellipse(x + dx, y + dy, rx, ry, 0.3, 0, Math.PI * 2); g.fill(); }
        this.box(x - 24, y - 26, 48, 50, t);
      }
      g.restore();
    }

    box(x, y, w, h, t) {
      if (!t.box) return;
      const g = this.g, col = t.color || '#22d3ee', L = 10;
      g.save(); g.strokeStyle = col; g.lineWidth = 2;
      const pulse = 0.65 + 0.35 * Math.sin(this.t * 6);
      g.globalAlpha = pulse;
      g.beginPath();
      g.moveTo(x, y + L); g.lineTo(x, y); g.lineTo(x + L, y);
      g.moveTo(x + w - L, y); g.lineTo(x + w, y); g.lineTo(x + w, y + L);
      g.moveTo(x + w, y + h - L); g.lineTo(x + w, y + h); g.lineTo(x + w - L, y + h);
      g.moveTo(x + L, y + h); g.lineTo(x, y + h); g.lineTo(x, y + h - L);
      g.stroke();
      g.globalAlpha = 1;
      g.font = '700 11px ui-monospace, DejaVu Sans Mono, monospace';
      const txt = t.label + (t.conf ? '  ' + t.conf.toFixed(2) : '');
      const tw = g.measureText(txt).width + 8;
      g.fillStyle = col; g.fillRect(x, y - 16, tw, 15);
      g.fillStyle = '#05080b'; g.fillText(txt, x + 4, y - 5);
      g.restore();
    }

    frame(ctx) {
      const g = this.g, W = this.w, H = this.h;
      g.save();
      g.strokeStyle = 'rgba(226,232,240,0.55)'; g.lineWidth = 1.2;
      // crosshair
      g.beginPath(); g.moveTo(W / 2 - 18, H / 2); g.lineTo(W / 2 - 6, H / 2); g.moveTo(W / 2 + 6, H / 2); g.lineTo(W / 2 + 18, H / 2);
      g.moveTo(W / 2, H / 2 - 18); g.lineTo(W / 2, H / 2 - 6); g.moveTo(W / 2, H / 2 + 6); g.lineTo(W / 2, H / 2 + 18); g.stroke();
      // corner brackets
      const m = 10, L = 18;
      g.beginPath();
      g.moveTo(m, m + L); g.lineTo(m, m); g.lineTo(m + L, m);
      g.moveTo(W - m - L, m); g.lineTo(W - m, m); g.lineTo(W - m, m + L);
      g.moveTo(W - m, H - m - L); g.lineTo(W - m, H - m); g.lineTo(W - m - L, H - m);
      g.moveTo(m + L, H - m); g.lineTo(m, H - m); g.lineTo(m, H - m - L);
      g.stroke();
      g.font = '600 11px ui-monospace, DejaVu Sans Mono, monospace'; g.fillStyle = 'rgba(226,232,240,0.9)';
      g.textAlign = 'left';
      g.fillText(ctx.mode === 'off' ? 'EO/IR' : 'IR · WHITE-HOT · ' + (ctx.mode === 'forest' ? 'NFOV' : 'WFOV'), 16, 26);
      if (ctx.rec) { g.fillStyle = '#ef4444'; g.beginPath(); g.arc(18, H - 22, 4, 0, Math.PI * 2); g.fill(); g.fillStyle = 'rgba(226,232,240,0.9)'; g.fillText('REC', 26, H - 18); }
      if (ctx.lat !== undefined) g.fillText(`${Math.abs(ctx.lat).toFixed(4)}°${ctx.lat >= 0 ? 'N' : 'S'}  ${Math.abs(ctx.lon).toFixed(4)}°${ctx.lon >= 0 ? 'E' : 'W'}`, ctx.rec ? 60 : 16, H - 18);
      g.textAlign = 'right';
      if (ctx.clock) g.fillText(ctx.clock, W - 16, 26);
      if (ctx.alt !== undefined) g.fillText(`ALT ${Math.round(ctx.alt)} m · HDG ${String(Math.round(ctx.hdg || 0)).padStart(3, '0')}°`, W - 16, H - 18);
      // simulated label
      g.textAlign = 'center'; g.font = '700 10px ui-monospace, DejaVu Sans Mono, monospace';
      const lbl = ctx.simLabel || 'SIMULATED SENSOR VIEW', lw = g.measureText(lbl).width + 12;
      g.fillStyle = 'rgba(239,68,68,0.85)'; g.fillRect(W / 2 - lw / 2, H - 34, lw, 14);
      g.fillStyle = '#fff'; g.fillText(lbl, W / 2, H - 23.5);
      g.restore();
    }
  }

  window.IRFeed = IRFeed;
})();
