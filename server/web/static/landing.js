// OneAir — landing "Codex Numérique" — interactions JS
// Top bar nav, login joueur, dashboard inline, communauté + marquee, scroll progress.

const $  = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

const fmt = {
  num: (n) => Number(n || 0).toLocaleString('fr-FR'),
  pct: (r) => Math.round((r || 0) * 100) + ' %',
};

function breedBgUrl(id) {
  const valid = [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,20];
  const x = valid.includes(+id) ? id : 8;
  return `/classes/bg_${x}0.jpg`;
}

async function jsonFetch(url, opts = {}) {
  const res = await fetch(url, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    ...opts,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(text || `HTTP ${res.status}`);
  }
  return res.json();
}

function escapeHTML(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  })[c]);
}

// ====================== Top-bar nav (active link) ======================
function initRailNav() {
  const links = $$('[data-rail-link]');
  if (!links.length) return;
  const sections = links
    .map((a) => document.querySelector(a.getAttribute('href')))
    .filter(Boolean);

  const setActive = (id) => {
    links.forEach((a) => a.classList.toggle('active', a.getAttribute('href') === '#' + id));
  };

  const obs = new IntersectionObserver((entries) => {
    const visible = entries
      .filter((e) => e.isIntersecting)
      .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
    if (visible) setActive(visible.target.id);
  }, { rootMargin: '-30% 0px -50% 0px', threshold: [0, 0.25, 0.5, 0.75, 1] });

  sections.forEach((s) => obs.observe(s));
  setActive(sections[0]?.id || '');
}

// ====================== Reading progress bar ======================
function initReadingProgress() {
  const fill = $('.reading-progress-fill');
  if (!fill) return;
  const update = () => {
    const max = document.documentElement.scrollHeight - window.innerHeight;
    const pct = max > 0 ? (window.scrollY / max) * 100 : 0;
    fill.style.width = pct + '%';
  };
  window.addEventListener('scroll', update, { passive: true });
  update();
}

// ====================== Reveal au scroll ======================
function initReveal() {
  const targets = $$('[data-reveal]');
  if (!targets.length) return;
  const obs = new IntersectionObserver((entries) => {
    entries.forEach((e) => {
      if (e.isIntersecting) {
        e.target.classList.add('in');
        obs.unobserve(e.target);
      }
    });
  }, { threshold: 0.12, rootMargin: '0px 0px -50px 0px' });
  targets.forEach((el) => obs.observe(el));
}

// ====================== STATUS ======================
async function refreshStatus() {
  try {
    const s = await jsonFetch('/api/public/status');
    const setN = (sel, v) => { const el = $(sel); if (el) el.textContent = fmt.num(v); };
    setN('[data-online]', s.online);
    setN('[data-accounts]', s.accounts);
    setN('[data-chars]', s.characters);
    const dots = $$('.ed-status .dot');
    if (dots.length === 2) {
      dots[0].classList.toggle('up', s.authUp);
      dots[0].classList.toggle('down', !s.authUp);
      dots[1].classList.toggle('up', s.worldUp);
      dots[1].classList.toggle('down', !s.worldUp);
    }
  } catch (_) { /* ignore */ }
}

// ====================== AUTH JOUEUR ======================
async function refreshMe() {
  let me;
  try { me = await jsonFetch('/api/public/me'); } catch (_) { return; }

  const loginView   = $('#login-view');
  const playerView  = $('#player-view');
  const adminBtn    = $('#player-admin-btn');
  const compteTitle = $('#compte-title');

  if (!me.loggedIn) {
    loginView.hidden = false;
    playerView.hidden = true;
    if (adminBtn) adminBtn.hidden = true;
    if (compteTitle) compteTitle.innerHTML = '<i>Présente</i> tes lettres de créance.';
    return;
  }

  loginView.hidden = true;
  playerView.hidden = false;
  if (compteTitle) compteTitle.innerHTML = `Tableau de <i>${escapeHTML(me.nickname || me.username)}</i>.`;

  const greeting = $('#player-greeting');
  const sub      = $('#player-sub');
  const role     = $('#player-role-label');
  if (greeting) greeting.textContent = me.nickname || me.username;
  if (sub) {
    const n = me.characters?.length || 0;
    sub.textContent = `@${me.username} · ${n} héros enregistré${n > 1 ? 's' : ''}`;
  }
  if (role) role.textContent = me.isAdmin ? '— Maître du Codex —' : '— Aventurier —';
  if (adminBtn) adminBtn.hidden = !me.isAdmin;
  renderHeroes(me.characters || []);
}

function renderHeroes(list) {
  const wrap = $('#char-list');
  if (!list.length) {
    wrap.innerHTML = `
      <div class="hero-empty">
        <p class="kicker">Aucun héros</p>
        <p class="muted">Lance le client et crée ton premier personnage. Il apparaîtra ici dès qu'il aura choisi sa classe.</p>
      </div>`;
    return;
  }
  wrap.innerHTML = list.map(heroTileHTML).join('');
  $$('#char-list .hero-tile').forEach((el) => {
    el.addEventListener('click', (e) => {
      if (e.target.closest('.hero-collapse')) return;
      if (el.classList.contains('expanded')) return;
      collapseAll();
      expandHero(el, +el.dataset.cid);
    });
  });
}

function heroTileHTML(c) {
  const bg = breedBgUrl(c.breedId);
  return `
    <div class="hero-tile" role="button" tabindex="0" data-cid="${c.id}">
      <div class="hero-tile-bg" style="background-image:url('${bg}')"></div>
      <button class="hero-collapse" aria-label="Replier" type="button">×</button>
      <div class="hero-tile-content">
        <div class="hero-tile-head">
          <div>
            <h3 class="hero-tile-name">${escapeHTML(c.name) || 'Sans nom'}</h3>
            <p class="hero-tile-class">${escapeHTML(c.breedName)}${c.online ? ' · <span class="hero-tile-online"><span class="dot up" style="width:6px;height:6px"></span>En ligne</span>' : ''}</p>
          </div>
          <div class="hero-tile-level">
            Niveau<strong>${c.level || '?'}</strong>
          </div>
        </div>
        <div class="hero-tile-stats">
          <div><span class="tile-k">Kamas</span><span class="tile-v cyan">${fmt.num(c.kamas)}</span></div>
          <div><span class="tile-k">Zone</span><span class="tile-v" style="font-size:14px;font-style:normal">${escapeHTML(c.zone || '—')}</span></div>
        </div>
        <div class="hero-expand" data-detail></div>
      </div>
    </div>`;
}

function collapseAll() {
  $$('#char-list .hero-tile.expanded').forEach((el) => el.classList.remove('expanded'));
}

async function expandHero(el, cid) {
  const detail = $('[data-detail]', el);
  if (!detail) return;
  detail.innerHTML = `<p class="muted" style="grid-column:1/-1">Chargement de la fiche…</p>`;
  el.classList.add('expanded');

  const closeBtn = $('.hero-collapse', el);
  if (closeBtn) closeBtn.addEventListener('click', (ev) => {
    ev.stopPropagation();
    el.classList.remove('expanded');
  });

  el.scrollIntoView({ behavior: 'smooth', block: 'start' });

  try {
    const c = await jsonFetch('/api/public/character?id=' + encodeURIComponent(cid));
    const ratio = Math.max(0, Math.min(1, c.xpRatio || 0));
    detail.innerHTML = `
      <div class="hero-expand-xp">
        <div>
          <p class="kicker">Progression</p>
          <div class="hero-xp-bar"><div class="hero-xp-fill" style="width:${ratio * 100}%"></div></div>
          <div class="hero-xp-text">
            <span>${fmt.num(c.xpInLevel)} / ${fmt.num(c.xpForNext)}</span>
            <span>${fmt.pct(ratio)}</span>
          </div>
        </div>
        <div>
          <p class="kicker">Position</p>
          <p class="hero-xp-position">${escapeHTML(c.position)}</p>
        </div>
      </div>
      <div class="hero-expand-grid">
        <div class="hero-stat-tile"><div class="hero-stat-k">Kamas</div><div class="hero-stat-v">${fmt.num(c.kamas)}</div></div>
        <div class="hero-stat-tile"><div class="hero-stat-k">Items</div><div class="hero-stat-v">${fmt.num(c.itemCount)}</div></div>
        <div class="hero-stat-tile"><div class="hero-stat-k">XP totale</div><div class="hero-stat-v">${fmt.num(c.experience)}</div></div>
        <div class="hero-stat-tile"><div class="hero-stat-k">Statut</div><div class="hero-stat-v" style="font-size:15px;${c.online ? 'color:var(--leaf)' : 'color:var(--paper-2)'}">${c.online ? 'En ligne' : 'Hors ligne'}</div></div>
      </div>
    `;
  } catch (_) {
    detail.innerHTML = `<p class="form-error" style="grid-column:1/-1">Impossible de charger ce personnage.</p>`;
  }
}

// ====================== EVENT WIRING ======================
function wire() {
  const form = $('#player-login');
  if (form) {
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(form);
      const err = $('#login-error');
      err.hidden = true;
      try {
        await jsonFetch('/api/public/login', {
          method: 'POST',
          body: JSON.stringify({
            username: fd.get('username'),
            password: fd.get('password'),
          }),
        });
        form.reset();
        await refreshMe();
        $('#compte').scrollIntoView({ behavior: 'smooth' });
      } catch (e2) {
        err.textContent = String(e2.message || 'Connexion impossible.');
        err.hidden = false;
      }
    });
  }

  const logout = $('#player-logout');
  if (logout) {
    logout.addEventListener('click', async () => {
      try { await jsonFetch('/api/public/logout', { method: 'POST' }); } catch (_) {}
      await refreshMe();
      $('#compte').scrollIntoView({ behavior: 'smooth' });
    });
  }
}

// ====================== COMMUNAUTÉ ======================

const FEED_ICONS = {
  level_up:    { glyph: '✦', cls: 'feed-icon-level' },
  dungeon_win: { glyph: '⚔', cls: 'feed-icon-dungeon' },
};

function timeAgo(d) {
  const sec = Math.max(0, Math.floor((Date.now() - d.getTime()) / 1000));
  if (sec < 60)        return 'à l’instant';
  if (sec < 3600)      return `il y a ${Math.floor(sec / 60)} min`;
  if (sec < 86400)     return `il y a ${Math.floor(sec / 3600)} h`;
  if (sec < 86400 * 7) return `il y a ${Math.floor(sec / 86400)} j`;
  return d.toLocaleDateString('fr-FR', { day: 'numeric', month: 'short' });
}

async function refreshCommunity() {
  try {
    const r = await fetch('/api/public/community?limit=30&leaderboardN=10', {
      credentials: 'include',
    });
    if (!r.ok) throw new Error('http ' + r.status);
    const data = await r.json();
    renderFeed(data.events || []);
    renderLeaderboard(data.leaderboard || []);
    renderCommunityStats(data.stats || {});
    renderMarquee(data.events || []);
  } catch (_) {
    const feed = $('#community-feed');
    if (feed) feed.innerHTML = '<li class="com-feed-loading">Le flux est temporairement indisponible.</li>';
  }
}

function renderFeed(events) {
  const ol = $('#community-feed');
  if (!ol) return;
  if (events.length === 0) {
    ol.innerHTML = '<li class="com-feed-loading">Le serveur démarre — les premiers exploits apparaîtront ici dès qu’un héros aura fait parler de lui.</li>';
    const sub = $('#com-feed-sub');
    if (sub) sub.textContent = '';
    return;
  }
  ol.innerHTML = '';
  for (const e of events) {
    const li = document.createElement('li');
    const meta = FEED_ICONS[e.kind] || { glyph: '·', cls: '' };
    const at = new Date(e.atUtc.endsWith('Z') ? e.atUtc : (e.atUtc + 'Z'));
    li.innerHTML = `
      <span class="feed-icon ${meta.cls}">${meta.glyph}</span>
      <span class="feed-text">
        <span class="feed-title"></span>
        ${e.detail ? '<span class="feed-detail"></span>' : ''}
      </span>
      <span class="feed-time">${timeAgo(at)}</span>
    `;
    li.querySelector('.feed-title').textContent = e.title;
    if (e.detail) li.querySelector('.feed-detail').textContent = e.detail;
    ol.appendChild(li);
  }
  const sub = $('#com-feed-sub');
  if (sub) sub.textContent = events.length + (events.length > 1 ? ' faits récents' : ' fait récent');
}

function renderLeaderboard(rows) {
  const ol = $('#community-leaderboard');
  if (!ol) return;
  if (rows.length === 0) {
    ol.innerHTML = '<li class="com-feed-loading">Aucun héros classé pour le moment.</li>';
    return;
  }
  ol.innerHTML = '';
  for (const r of rows) {
    const li = document.createElement('li');
    li.innerHTML = `
      <span class="lb-rank">${toRoman(r.rank)}</span>
      <span class="lb-name">
        <img src="/breeds/${r.breedId}.png" alt="" loading="lazy" onerror="this.style.opacity=0">
        <span></span>
      </span>
      <span class="lb-level">N. ${r.level}</span>
    `;
    li.querySelector('.lb-name span').textContent = r.name;
    ol.appendChild(li);
  }
}

function toRoman(n) {
  const map = ['', 'I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X'];
  if (n <= 10 && n >= 0) return map[n];
  return String(n);
}

function renderCommunityStats(stats) {
  const el = $('#community-foot-stat');
  if (!el) return;
  const bits = [];
  const byKind = stats.byKind || {};
  if (byKind.dungeon_win) bits.push(`${byKind.dungeon_win} donjons terminés`);
  if (byKind.level_up)    bits.push(`${byKind.level_up} paliers franchis`);
  if (stats.topDungeon && stats.topDungeon.detail) {
    bits.push(`donjon vedette : ${stats.topDungeon.detail}`);
  }
  if (bits.length === 0) { el.hidden = true; return; }
  el.hidden = false;
  el.textContent = bits.join('  ·  ');
}

function renderMarquee(events) {
  const track = $('#com-marquee-track');
  if (!track) return;
  let items;
  if (events.length === 0) {
    items = ['Le serveur attend ses premiers exploits.', 'OneAir 2.68 · Codex en construction', 'Édition Voyageur'];
  } else {
    items = events.slice(0, 12).map(e => e.title);
  }
  // Doubler la liste pour boucler proprement (translate -50%)
  const html = items.concat(items)
    .map(t => `<span class="com-marquee-item">${escapeHTML(t)}</span>`)
    .join('');
  track.innerHTML = html;
}

// ====================== Boot ======================
document.addEventListener('DOMContentLoaded', () => {
  wire();
  initRailNav();
  initReadingProgress();
  initReveal();
  refreshStatus();
  refreshMe();
  refreshCommunity();
  setInterval(refreshStatus, 20000);
  setInterval(refreshCommunity, 60000);
});
