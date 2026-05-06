// OneAir admin UI — vanilla JS, no deps.

const $ = (s, p = document) => p.querySelector(s);
const $$ = (s, p = document) => Array.from(p.querySelectorAll(s));

// Catalogue d'effets — IDs vérifiés contre Giny + en jeu (Dofus 2.68).
// Inclut les effets "spéciaux" (Attitude, Apparence) qu'on trouve sur les
// montures/familiers cosmétiques.
const EFFECTS = [
  // Caractéristiques de base
  {id: 125, name: "Vitalité"},
  {id: 118, name: "Force"},
  {id: 119, name: "Agilité"},
  {id: 123, name: "Chance"},
  {id: 126, name: "Intelligence"},
  {id: 124, name: "Sagesse"},
  // PA / PM / Portée
  {id: 111, name: "PA"},
  {id: 78,  name: "PM"},
  {id: 117, name: "Portée"},
  // Combat / dommages
  {id: 112, name: "Dommages"},
  {id: 115, name: "Critique"},
  {id: 138, name: "Puissance"},                  // ← corrigé : était "+% Dommages"
  {id: 165, name: "+% Dommages"},                // ← le vrai % Dommages
  {id: 174, name: "Initiative"},
  {id: 176, name: "Prospection"},
  {id: 178, name: "Soins"},
  {id: 220, name: "Renvoi de dommages"},
  {id: 418, name: "+% Dommages Critiques"},
  {id: 420, name: "Réduction Dommages Critiques"},
  // % Caractéristiques (Idoles / cosmétiques)
  {id: 2834, name: "% Force"},
  {id: 2836, name: "% Agilité"},
  {id: 2838, name: "% Intelligence"},
  {id: 2840, name: "% Chance"},
  {id: 2842, name: "% Sagesse"},
  {id: 2844, name: "% Vitalité"},
  {id: 1078, name: "% Vitalité (alt)"},
  // Résistances %
  {id: 210, name: "% Résistance Terre"},
  {id: 211, name: "% Résistance Eau"},
  {id: 212, name: "% Résistance Air"},
  {id: 213, name: "% Résistance Feu"},
  {id: 214, name: "% Résistance Neutre"},
  // Résistances flat
  {id: 240, name: "Résistance Terre"},
  {id: 241, name: "Résistance Eau"},
  {id: 242, name: "Résistance Air"},
  {id: 243, name: "Résistance Feu"},
  {id: 244, name: "Résistance Neutre"},
  {id: 1076, name: "+ Résistances toutes"},
  // Résistances PvP %
  {id: 250, name: "% Résistance PvP Terre"},
  {id: 251, name: "% Résistance PvP Eau"},
  {id: 252, name: "% Résistance PvP Air"},
  {id: 253, name: "% Résistance PvP Feu"},
  {id: 254, name: "% Résistance PvP Neutre"},
  // Esquive / Tacle
  {id: 160, name: "Esquive PA"},
  {id: 161, name: "Esquive PM"},
  {id: 2850, name: "% Tacle"},
  {id: 168, name: "Retrait PA"},
  {id: 169, name: "Retrait PM"},
  // Invocations / Pods
  {id: 182, name: "Invocations max"},
  {id: 158, name: "Pods (Poids)"},
  // Vol de vie élémentaire
  {id: 91, name: "Vol de vie Eau"},
  {id: 92, name: "Vol de vie Terre"},
  {id: 93, name: "Vol de vie Air"},
  {id: 94, name: "Vol de vie Feu"},
  {id: 95, name: "Vol de vie Neutre"},
  // Dommages élémentaires
  {id: 422, name: "+Dommages Terre"},
  {id: 424, name: "+Dommages Feu"},
  {id: 426, name: "+Dommages Eau"},
  {id: 428, name: "+Dommages Air"},
  {id: 430, name: "+Dommages Neutre"},
  // Effets spéciaux (familiers / montures cosmétiques)
  {id: 10,   name: "Attitude (familier)"},      // value = ID d'attitude
  {id: 1176, name: "Apparence (cosmétique)"},   // value = ID d'apparence
  {id: 980,  name: "État"},
  {id: 612,  name: "Boost familier"},
];

// Icône item : essaye le cache local (extrait des d2p Dofus), fallback Ankama CDN auto via le handler /items.
const ITEM_ICON = (gid) => `/items/${gid}.png`;
const SPELL_ICON = (id) => `/spells/${id}.png`;

// Catégories d'inventaire — exactement les 6 du jeu, avec sous-catégories.
// TypeIds vérifiés contre la DB Giny (giny_world.items) et l'ItemTypeEnum
// de Dofus 2.68. Couvre tous les types couramment rencontrés.
const ITEM_CATEGORIES = [
  { key: "all", label: "Tous", icon: "∞" },
  {
    key: "equipement", label: "Équipement", icon: "⚔",
    subcats: [
      { key: "hat",       label: "Coiffes",       types: [16] },
      { key: "cloak",     label: "Capes",         types: [17] },
      { key: "amulet",    label: "Amulettes",     types: [1] },
      { key: "ring",      label: "Anneaux",       types: [9] },
      { key: "belt",      label: "Ceintures",     types: [10] },
      { key: "boots",     label: "Bottes",        types: [11] },
      { key: "shield",    label: "Boucliers",     types: [82] },
      { key: "dofus",     label: "Dofus",         types: [23] },
      { key: "trophy",    label: "Capsules / Trophées", types: [74] },
      { key: "bag",       label: "Havres-sacs",   types: [218] },
      { key: "weapons",   label: "Armes",         types: [2, 3, 4, 5, 6, 7, 8, 114] },
      { key: "tools",     label: "Outils de récolte", types: [19, 20, 21, 22] },
      { key: "net",       label: "Filets de capture", types: [99] },
      { key: "pets",      label: "Familiers",     types: [18] },
      { key: "mounts",    label: "Montures",      types: [121, 143] },
      { key: "harness",   label: "Harnachements", types: [190, 255, 256] },
    ],
  },
  {
    key: "utilisable", label: "Objets utilisables", icon: "✦",
    subcats: [
      { key: "potions",       label: "Potions",                types: [12] },
      { key: "potions-fight", label: "Potions de combat",      types: [14] },
      { key: "scrolls-xp",    label: "Parchemins (objets)",    types: [13] },
      { key: "scrolls-spell", label: "Parchemins de sort",     types: [75] },
      { key: "scrolls-car",   label: "Parchemins de caract.",  types: [76] },
      { key: "scrolls-seek",  label: "Parchemins chercheur",   types: [87] },
      { key: "treats",        label: "Friandises",             types: [42] },
      { key: "boost-food",    label: "Bénédictions / Boosts",  types: [28, 29] },
      { key: "smithmagic",    label: "Potions forgemagie",     types: [26] },
      { key: "tp",            label: "Potions de TP",          types: [43] },
      { key: "mount-pot",     label: "Potions de monture",     types: [206] },
      { key: "atti-pot",      label: "Potions d'attitude",     types: [214] },
      { key: "runes-trans",   label: "Runes transcendantes",   types: [211] },
      { key: "runes-astral",  label: "Runes astrales",         types: [233] },
      { key: "perceptor",     label: "Potions de Percepteur",  types: [165] },
      { key: "rp-buffs",      label: "Buffs RP",               types: [30, 31] },
      { key: "kamas-bag",     label: "Bourses de kamas",       types: [146, 216] },
      { key: "gifts",         label: "Cadeaux",                types: [89] },
    ],
  },
  {
    key: "quete", label: "Objets de quête", icon: "❓",
    subcats: [
      { key: "mission",     label: "Items de mission",  types: [80, 168] },
      { key: "documents",   label: "Documents / Livres", types: [25] },
      { key: "soul-stones", label: "Pierres d'âme",     types: [85] },
      { key: "soul-full",   label: "Pleines pierres d'âme", types: [88] },
      { key: "keys",        label: "Clefs",             types: [84] },
      { key: "maps",        label: "Cartes",            types: [174] },
      { key: "frags-map",   label: "Fragments de carte", types: [175, 176] },
      { key: "quest-misc",  label: "Divers (quête)",    types: [24, 215, 213, 254, 217] },
      { key: "certs",       label: "Certificats / Titres", types: [97, 200, 241] },
      { key: "perceptor",   label: "Items de Percepteur", types: [278] },
    ],
  },
  {
    key: "cosmetique", label: "Cosmétiques", icon: "✿",
    subcats: [
      { key: "ceremo-hat",     label: "Coiffes cérémo.",     types: [246] },
      { key: "ceremo-cloak",   label: "Capes / Ailes cérémo.", types: [247] },
      { key: "ceremo-amulet",  label: "Amulettes cérémo.",   types: [252] },
      { key: "ceremo-shield",  label: "Boucliers cérémo.",   types: [248] },
      { key: "ceremo-bag",     label: "Sacs cérémo.",        types: [249] },
      { key: "ceremo-pet",     label: "Familiers cérémo.",   types: [250] },
      { key: "ceremo-weapon",  label: "Armes cérémo.",       types: [251] },
      { key: "anim",           label: "Cosmétiques anim",    types: [144] },
      { key: "mutation",       label: "Mutations",           types: [27] },
      { key: "stuffed",        label: "Peluches",            types: [61] },
      { key: "ornaments",      label: "Ornements",           types: [222] },
      { key: "idols",          label: "Idoles",              types: [273, 274, 275, 276, 277, 289] },
      { key: "relics",         label: "Reliques",            types: [284] },
      { key: "sigils",         label: "Porte-signes",        types: [287] },
      { key: "engravings",     label: "Gravures",            types: [258] },
      { key: "wings",          label: "Ailes (cérémo.)",     types: [199] },
    ],
  },
  {
    key: "ressource", label: "Ressources", icon: "✣",
    subcats: [
      { key: "wood",       label: "Bois",                types: [38] },
      { key: "ore",        label: "Minerai",             types: [39] },
      { key: "alloy",      label: "Alliages",            types: [40] },
      { key: "stone",      label: "Pierres précieuses",  types: [50] },
      { key: "stones",     label: "Pierres / Métaria",   types: [51, 66] },
      { key: "flowers",    label: "Fleurs",              types: [35] },
      { key: "plants",     label: "Plantes",             types: [36] },
      { key: "seeds",      label: "Graines",             types: [58] },
      { key: "bark",       label: "Écorces / Sèves",     types: [96, 185] },
      { key: "roots",      label: "Racines",             types: [98] },
      { key: "cereals",    label: "Céréales",            types: [34] },
      { key: "bread",      label: "Pains",               types: [33] },
      { key: "fruits",     label: "Fruits",              types: [46] },
      { key: "veggies",    label: "Légumes",             types: [68] },
      { key: "fish",       label: "Poissons",            types: [41] },
      { key: "edible-fish", label: "Poissons (cuisine)", types: [49] },
      { key: "meat",       label: "Viandes crues",       types: [63] },
      { key: "edible-meat", label: "Viandes cuisinées",  types: [69] },
      { key: "bones",      label: "Os / Carapaces",      types: [47, 149] },
      { key: "feathers",   label: "Plumes",              types: [53] },
      { key: "hair",       label: "Cheveux / Barbe",     types: [54] },
      { key: "fabric",     label: "Étoffes",             types: [55] },
      { key: "leather",    label: "Cuirs",               types: [56] },
      { key: "wool",       label: "Laines",              types: [57] },
      { key: "skin",       label: "Écailles / Peaux",    types: [59] },
      { key: "tail",       label: "Queues / Pompons",    types: [65] },
      { key: "drinks",     label: "Boissons",            types: [37, 79] },
      { key: "ash",        label: "Cendres / Poudres",   types: [48] },
      { key: "oil",        label: "Huiles",              types: [60] },
      { key: "dye",        label: "Teintures",           types: [70] },
      { key: "alchemy",    label: "Équipement alchimie", types: [71] },
      { key: "smith-rune", label: "Runes forgemagie",    types: [78] },
      { key: "bags-res",   label: "Sacs de ressources",  types: [100] },
      { key: "hormones",   label: "Hormones",            types: [262] },
      { key: "obsolete",   label: "Obsolètes",           types: [245] },
      { key: "temporis",   label: "Temporis",            types: [241, 266] },
      { key: "dream",      label: "Rêves",               types: [219] },
      { key: "combat",     label: "Combat",              types: [229] },
      { key: "misc",       label: "Divers",              types: [15] },
    ],
  },
];

// Tous les TypeIds d'une catégorie principale (union de ses subcats).
function categoryTypes(cat) {
  if (!cat.subcats) return null;
  const out = [];
  cat.subcats.forEach(s => out.push(...s.types));
  return out;
}

// Catégorie d'un item à partir de son TypeId + flag Usable.
// Ordre : Équipement → Quête → Cosmétique (par TypeId), puis Usable → Utilisable, sinon Ressource.
function categorizeItem(typeId, usable) {
  for (const c of ITEM_CATEGORIES) {
    if (c.key === "all" || c.key === "utilisable" || c.key === "ressource") continue;
    const types = categoryTypes(c);
    if (types && types.includes(typeId)) return c;
  }
  if (usable) return ITEM_CATEGORIES.find(c => c.key === "utilisable");
  return ITEM_CATEGORIES.find(c => c.key === "ressource");
}

// Union des TypeIds qui ne sont PAS dans Utilisable/Ressource (catch-all).
function fixedCategoryTypes() {
  const set = new Set();
  ITEM_CATEGORIES.forEach(c => {
    if (c.key === "utilisable" || c.key === "ressource" || c.key === "all") return;
    const types = categoryTypes(c);
    if (types) types.forEach(t => set.add(t));
  });
  return Array.from(set);
}

function toast(msg, kind = "") {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "show " + kind;
  setTimeout(() => t.classList.remove("show"), 2400);
}

async function api(path, opts = {}) {
  const r = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...opts,
  });
  if (r.status === 401) { location.href = "/login"; throw new Error("unauthorized"); }
  if (!r.ok) {
    const t = await r.text();
    throw new Error(t || r.statusText);
  }
  return r.headers.get("content-type")?.includes("json") ? r.json() : r.text();
}

// --- routing tabs -----------------------------------------------------------

function showTab(name) {
  $$(".panel").forEach(p => p.classList.toggle("active", p.id === name));
  $$(".nav-item").forEach(a => a.classList.toggle("active", a.dataset.tab === name));
  if (name === "dbgate") {
    const iframe = $("#dbgate-iframe");
    if (iframe.src === "about:blank" || !iframe.src.includes("/dbgate")) {
      iframe.src = "/dbgate/";
    }
  }
  // Stop le countdown live quand on quitte l'onglet events
  if (name !== "events" && _eventsTickTimer) {
    clearInterval(_eventsTickTimer);
    _eventsTickTimer = null;
  }
  if (loaders[name]) loaders[name]();
}

window.addEventListener("hashchange", () => {
  const t = location.hash.slice(1) || "dashboard";
  showTab(t);
});

document.addEventListener("DOMContentLoaded", () => {
  // Tous les .modal sont relocalisés au niveau body — sinon ils sont cachés
  // par display:none de leur panel parent quand on est sur un autre onglet.
  document.querySelectorAll(".modal").forEach(m => document.body.appendChild(m));

  $$(".nav-item").forEach(a => a.addEventListener("click", e => {
    const t = a.dataset.tab;
    showTab(t);
  }));
  showTab(location.hash.slice(1) || "dashboard");
  setInterval(() => loaders.dashboard(), 5000);
  // Quand on revient sur le dashboard, force un re-fetch joueurs
  window.addEventListener("hashchange", () => {
    if ((location.hash.slice(1) || "dashboard") === "dashboard") loadOnlinePlayers();
  });
});

// --- EVENTS countdown ------------------------------------------------------

let _eventsTickTimer = null;

function formatMul(m) {
  if (m == null) return "1";
  if (Number.isInteger(m)) return String(m);
  return m.toFixed(2).replace(/\.?0+$/, "");
}

function formatRemaining(ms) {
  if (ms <= 0) return "expiré";
  const s = Math.floor(ms / 1000);
  if (s < 60) return s + "s";
  if (s < 3600) {
    const m = Math.floor(s / 60), r = s % 60;
    return `${m}min ${r}s`;
  }
  if (s < 86400) {
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
    return `${h}h ${m}min`;
  }
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600);
  return `${d}j ${h}h`;
}

function updateEventCountdowns() {
  const cards = document.querySelectorAll(".event-card .ev-expires[data-expires]");
  cards.forEach(el => {
    const iso = el.dataset.expires;
    if (!iso) return;
    const exp = new Date(iso).getTime();
    const remaining = exp - Date.now();
    if (remaining <= 0) {
      el.textContent = "Expiré (sera nettoyé au prochain tick)";
      el.classList.remove("live");
    } else {
      el.textContent = "Expire dans " + formatRemaining(remaining);
      el.classList.add("live");
    }
  });
}

// --- ONLINE PLAYERS --------------------------------------------------------

const BREED_NAMES = {
  1: "Feca", 2: "Osamodas", 3: "Enutrof", 4: "Sram",
  5: "Xelor", 6: "Ecaflip", 7: "Eniripsa", 8: "Iop",
  9: "Cra", 10: "Sadida", 11: "Sacrieur", 12: "Pandawa",
  13: "Roublard", 14: "Zobal", 15: "Steamer", 16: "Eliotrope",
  17: "Huppermage", 18: "Ouginak", 19: "Forgelance",
};

function isFemale(sex) {
  return sex === "1" || sex === 1 || sex === "True" || sex === "true" || sex === true;
}

async function loadOnlinePlayers() {
  const root = $("#online-list");
  if (!root) return;
  let players = [];
  try { players = await api("/api/players/online"); } catch (e) {}

  $("#online-count").textContent = players.length === 0
    ? "(personne)"
    : `(${players.length})`;

  if (!players.length) {
    root.innerHTML = `<div class="empty-state" style="grid-column:1/-1; padding:20px;">
      <p style="color:var(--muted)">Aucun joueur en ligne pour l'instant.</p>
    </div>`;
    return;
  }

  root.innerHTML = "";
  players.forEach(p => {
    const sexCode = isFemale(p.sex) ? 1 : 0;
    const breedName = BREED_NAMES[p.breedId] || ("Classe " + p.breedId);
    const card = document.createElement("div");
    card.className = "player-card";
    card.style.setProperty("--symbol-url", `url(/classes/symbol_${p.breedId}.png)`);
    card.innerHTML = `
      <div class="pl-pulse" title="En ligne"></div>
      <div class="pl-avatar" style="background-image: url(/classes/bg_${p.breedId}${sexCode}.jpg);">
        <div class="pl-symbol"></div>
      </div>
      <div class="pl-info">
        <div class="pl-name">${escapeHTML(p.name)}</div>
        <div class="pl-meta">
          <span class="pl-level">Niv ${p.level}</span>
          <span>${escapeHTML(breedName)}${isFemale(p.sex) ? " ♀" : " ♂"}</span>
          <span class="muted small">@ ${escapeHTML(p.username || "?")}</span>
        </div>
        <div class="pl-zone" title="Map ${p.mapId} [${p.mapX}, ${p.mapY}]">
          ${escapeHTML(p.zone || "Zone inconnue")} <span class="muted small">[${p.mapX}, ${p.mapY}]</span>
        </div>
      </div>`;
    card.onclick = () => openCharModal(p.characterId, p.name, true);
    root.appendChild(card);
  });
}

// --- loaders ----------------------------------------------------------------

const loaders = {
  dashboard: async () => {
    try {
      const s = await api("/api/status");
      $("#status-auth").innerHTML = s.servers[0].up
        ? '<span class="up">UP</span>' : '<span class="down">DOWN</span>';
      $("#status-world").innerHTML = s.servers[1].up
        ? '<span class="up">UP</span>' : '<span class="down">DOWN</span>';
      $("#status-online").textContent = s.playersOnline ?? 0;
      $("#status-accounts").textContent = s.accountsCount ?? 0;
      $("#status-characters").textContent = s.charactersCount ?? 0;
      $("#status-uptime").textContent = s.uptime;
    } catch (e) { /* ignore */ }
    if ($(".panel.active")?.id === "dashboard") loadOnlinePlayers();
  },
  accounts: async () => {
    const accounts = await api("/api/accounts");
    const roleNames = { 1: "Joueur", 2: "Modo", 3: "GMP", 4: "GM", 5: "Admin" };
    const tbody = $("#accounts-table tbody");
    tbody.innerHTML = "";
    (accounts || []).forEach(a => {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td>${a.id}</td>
        <td>${escapeHTML(a.username)}</td>
        <td><code>${escapeHTML(a.password || "")}</code></td>
        <td>
          <select data-id="${a.id}" class="role-select">
            ${[1,2,3,4,5].map(r => `<option value="${r}" ${r===a.role?"selected":""}>${roleNames[r]}</option>`).join("")}
          </select>
        </td>
        <td>${a.banned ? "<span class='down'>OUI</span>" : "non"}</td>
        <td>${a.characterSlots}</td>
        <td class="actions">
          <button data-act="password" data-id="${a.id}" class="ghost">Mdp</button>
          ${a.banned
            ? `<button data-act="unban" data-id="${a.id}" class="ghost">Débannir</button>`
            : `<button data-act="ban"   data-id="${a.id}" class="danger">Bannir</button>`}
          <button data-act="delete" data-id="${a.id}" class="danger">Suppr</button>
        </td>`;
      tbody.appendChild(tr);
    });

    tbody.querySelectorAll(".role-select").forEach(s => s.onchange = async e => {
      await api("/api/accounts", { method: "POST",
        body: JSON.stringify({ action: "role", id: +e.target.dataset.id, role: +e.target.value })});
      toast("Rôle mis à jour", "ok");
    });
    tbody.querySelectorAll("button[data-act]").forEach(b => b.onclick = () => accountAction(b.dataset.act, +b.dataset.id));
  },
  characters: async () => {
    const chars = await api("/api/characters");
    const tbody = $("#characters-table tbody");
    const filter = $("#char-filter").value.toLowerCase();
    tbody.innerHTML = "";
    (chars || []).filter(c => !filter ||
      c.name.toLowerCase().includes(filter) || c.username.toLowerCase().includes(filter))
      .forEach(c => {
        const tr = document.createElement("tr");
        tr.innerHTML = `
          <td>${c.id}</td>
          <td><b>${escapeHTML(c.name)}</b></td>
          <td>${escapeHTML(c.username)} <span class="muted">(${c.accountId})</span></td>
          <td>${c.sex === "1" || c.sex === "true" || c.sex === 1 ? "F" : "M"}</td>
          <td>${c.experience}</td>
          <td>${c.kamas}</td>
          <td>${c.mapId}/${c.cellId}</td>
          <td>${c.online ? "<span class='up'>online</span>" : "<span class='dim'>offline</span>"}</td>
          <td class="actions">
            <button data-act="actions" data-id="${c.id}" data-name="${escapeHTML(c.name)}" data-online="${c.online?1:0}">Actions</button>
            <button data-act="inv" data-id="${c.id}" data-name="${escapeHTML(c.name)}" class="ghost">Inv</button>
            <button data-act="kick" data-id="${c.id}" class="danger" ${c.online?"":"disabled"}>Kick</button>
            <button data-act="delchar" data-id="${c.id}" data-name="${escapeHTML(c.name)}" class="danger">Suppr</button>
          </td>`;
        tbody.appendChild(tr);
      });

    tbody.querySelectorAll("button[data-act='inv']").forEach(b => b.onclick = () => {
      $("#inv-cid").value = b.dataset.id;
      $("#inv-char-name").textContent = "— " + b.dataset.name + " (#" + b.dataset.id + ")";
      location.hash = "inventory";
      loaders.inventoryFor(+b.dataset.id);
    });
    tbody.querySelectorAll("button[data-act='actions']").forEach(b => b.onclick = () => {
      const id = +b.dataset.id;
      const name = b.dataset.name;
      const online = b.dataset.online === "1";
      openCharModal(id, name, online);
    });
    tbody.querySelectorAll("button[data-act='kick']").forEach(b => b.onclick = async () => {
      if (!confirm("Kicker ce personnage ?")) return;
      await api("/api/kick", { method: "POST",
        body: JSON.stringify({ characterId: +b.dataset.id, reason: "Kick admin" })});
      toast("Action de kick mise en file", "ok");
    });
    tbody.querySelectorAll("button[data-act='delchar']").forEach(b => b.onclick = async () => {
      const name = b.dataset.name;
      const id = +b.dataset.id;
      const c1 = confirm(`⚠ Supprimer DÉFINITIVEMENT le personnage « ${name} » (#${id}) ?\n\nSes items et son record seront perdus.`);
      if (!c1) return;
      const c2 = prompt(`Pour confirmer, retape exactement le nom du personnage : « ${name} »`);
      if (c2 !== name) return toast("Nom incorrect, suppression annulée", "err");
      await postAction("delete_character", String(id));
      toast(`Suppression de « ${name} » mise en file`, "ok");
      setTimeout(() => loaders.characters(), 2000);
    });
  },
  events: async () => {
    const grid = $("#events-grid");
    if (!grid) return;
    let active = [];
    try { active = await api("/api/events"); } catch (e) {}
    const byType = {};
    (active || []).forEach(e => byType[e.type] = e);

    const types = [
      { key: "xp",    label: "Expérience", emoji: "⭐", desc: "Multiplie l'XP gagnée par les joueurs (combats, quêtes, métiers)." },
      { key: "kamas", label: "Kamas",      emoji: "💰", desc: "Multiplie les kamas gagnés (drops monstres, quêtes)." },
      { key: "drop",  label: "Drop rate",  emoji: "🎁", desc: "Multiplie les chances de drop (selon implémentation Giny)." },
    ];

    grid.innerHTML = "";
    types.forEach(t => {
      const ev = byType[t.key];
      const isActive = ev && ev.multiplier !== 1;
      const mul = ev ? ev.multiplier : 1;
      const card = document.createElement("div");
      card.className = "event-card" + (isActive ? " active" : "");
      card.dataset.type = t.key;
      card.innerHTML = `
        <div class="ev-head">
          <span class="ev-name">${t.emoji} ${t.label}</span>
          <span class="ev-status ${isActive ? "on" : "off"}">${isActive ? "ACTIF" : "Inactif"}</span>
        </div>
        <div class="ev-multiplier">${formatMul(mul)}</div>
        <div class="ev-expires" data-expires="${ev?.expiresAt || ""}">${
          isActive
            ? (ev.expiresAt ? "expire dans —" : "Permanent — jusqu'à arrêt manuel")
            : "Pas d'événement actif"
        }</div>
        <p class="muted small" style="margin: 0 0 12px;">${t.desc}</p>
        <div class="ev-form">
          <div>
            <label>Multiplicateur</label>
            <input type="number" step="0.5" min="0.1" max="100" id="ev-${t.key}-mul" value="${isActive ? mul : 2}">
          </div>
          <div>
            <label>Durée</label>
            <select id="ev-${t.key}-dur">
              <option value="0">Permanent</option>
              <option value="900">15 min</option>
              <option value="1800">30 min</option>
              <option value="3600" selected>1 heure</option>
              <option value="7200">2 heures</option>
              <option value="14400">4 heures</option>
              <option value="43200">12 heures</option>
              <option value="86400">24 heures</option>
              <option value="259200">3 jours</option>
              <option value="604800">7 jours</option>
            </select>
          </div>
        </div>
        <div class="ev-actions">
          <button data-act="start">${isActive ? "Mettre à jour" : "Activer"}</button>
          ${isActive ? '<button data-act="stop" class="danger">Arrêter</button>' : ""}
        </div>`;
      card.querySelector("button[data-act='start']").onclick = async () => {
        const mul = +$(`#ev-${t.key}-mul`).value;
        const dur = +$(`#ev-${t.key}-dur`).value;
        if (!mul || mul <= 0) return toast("Multiplicateur invalide", "err");
        const durTxt = dur === 0 ? "permanent" : (dur >= 3600 ? `${dur/3600}h` : `${dur/60}min`);
        if (!confirm(`Activer ${t.label} × ${mul} (${durTxt}) ? Tous les joueurs en ligne seront notifiés.`)) return;
        await postAction("event_set", `${t.key}|${mul}|${dur}`);
        toast(`Événement ${t.label} × ${mul} activé`, "ok");
        setTimeout(() => loaders.events(), 800);
      };
      const stopBtn = card.querySelector("button[data-act='stop']");
      if (stopBtn) stopBtn.onclick = async () => {
        if (!confirm(`Arrêter l'événement ${t.label} ?`)) return;
        await postAction("event_clear", t.key);
        toast(`Événement ${t.label} arrêté`, "ok");
        setTimeout(() => loaders.events(), 800);
      };
      grid.appendChild(card);
    });

    // Démarre/relance le countdown live
    if (_eventsTickTimer) clearInterval(_eventsTickTimer);
    _eventsTickTimer = setInterval(updateEventCountdowns, 1000);
    updateEventCountdowns();
  },

  articles: async () => {
    const list = await api("/api/articles");
    const tbody = $("#articles-table tbody");
    tbody.innerHTML = "";
    (list || []).forEach(a => {
      const tr = document.createElement("tr");
      tr.dataset.id = a.id;
      tr.innerHTML = `
        <td><a href="/article/${a.slug}" target="_blank" rel="noopener">${escapeHTML(a.title)}</a></td>
        <td><code>${escapeHTML(a.slug)}</code></td>
        <td>${escapeHTML(a.tag || "")}</td>
        <td>${a.published ? '<span class="up">PUB</span>' : '<span class="down">BROUILLON</span>'}</td>
        <td class="muted small">${(a.createdAt || "").slice(0,10)}</td>
        <td class="row gap" style="justify-content:flex-end">
          <button class="ghost art-edit" data-id="${a.id}">Éditer</button>
          <button class="danger art-del" data-id="${a.id}">Supprimer</button>
        </td>`;
      tbody.appendChild(tr);
    });
    tbody.querySelectorAll(".art-edit").forEach(b => b.onclick = () => editArticle(+b.dataset.id, list));
    tbody.querySelectorAll(".art-del").forEach(b => b.onclick = async () => {
      if (!confirm("Supprimer cet article ?")) return;
      await api("/api/articles", { method: "POST", body: JSON.stringify({ action: "delete", id: +b.dataset.id }) });
      toast("Article supprimé", "ok");
      loaders.articles();
    });
  },

  actions: async () => {
    const list = await api("/api/actions");
    const tbody = $("#actions-table tbody");
    tbody.innerHTML = "";
    (list || []).forEach(a => {
      const tr = document.createElement("tr");
      const status = a.processedAt
        ? `<span class="up">traité</span>`
        : `<span class="dim">en attente</span>`;
      const result = (a.result || "").startsWith("err")
        ? `<span class="down">${escapeHTML(a.result)}</span>`
        : escapeHTML(a.result);
      tr.innerHTML = `
        <td>${a.id}</td>
        <td><code>${escapeHTML(a.type)}</code></td>
        <td><code>${escapeHTML(a.payload)}</code></td>
        <td><span class="muted">${new Date(a.createdAt).toLocaleString()}</span></td>
        <td>${status}<br><span class="muted">${a.processedAt ? new Date(a.processedAt).toLocaleString() : ""}</span></td>
        <td>${result}</td>`;
      tbody.appendChild(tr);
    });
  },
  unhandled: async () => loadUnhandled(),
  inventory: async () => { /* attendre clic Charger */ },
  inventoryFor: async (cid) => {
    $("#inv-empty").style.display = "none";
    $("#inv-board").classList.remove("hidden");
    const parsed = await api("/api/inventory/parsed?characterId=" + cid);
    const meta = $("#inv-meta");

    let items = parsed.items;
    let useParsed = Array.isArray(items) && items.length;
    if (!useParsed) {
      items = await api("/api/inventory?characterId=" + cid);
      meta.innerHTML = parsed.needsDump
        ? "<span class='dim'>Effets non décodés — clique « Décoder en jeu » si le joueur est online.</span>"
        : "<span class='dim'>Lecture SQL (effets non décodés).</span>";
    } else {
      const ts = parsed.updatedAt ? new Date(parsed.updatedAt).toLocaleString() : "?";
      meta.innerHTML = `<span class='up'>Données décodées</span> <span class='muted'>(maj ${ts})</span>`;
    }

    items = items || [];
    items.forEach(it => {
      const t = it.type || it.typeId || 0;
      const usable = (it.usable === 1 || it.usable === true);
      it._cat = categorizeItem(t, usable).key;
      it._level = it.level || 0;
      it._pos = it.position ?? it.pos ?? 63;
      it._eq = (it.eq === 1) || (it._pos !== 63 && it._pos !== 0xff);
      it._qty = it.qty ?? it.quantity ?? 1;
    });

    // Récupère les kamas du personnage
    let kamas = 0;
    try {
      const chars = await api("/api/characters");
      const me = (chars || []).find(c => c.id == cid);
      if (me) kamas = me.kamas;
    } catch (e) {}

    _invState = {
      items, cid, useParsed, kamas,
      cat: (_invState && _invState.cat) || "all",
    };
    renderPaperdoll();
    renderInvCatTabs();
    renderBagGrid();
    $("#bag-kamas").textContent = formatNumber(kamas);
  },
  backups: async () => {
    const list = await api("/api/backups");
    const tbody = $("#backups-table tbody");
    tbody.innerHTML = "";
    (list || []).forEach(b => {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td><code>${escapeHTML(b.name)}</code></td>
        <td>${humanSize(b.size)}</td>
        <td>${new Date(b.time).toLocaleString()}</td>
        <td class="actions">
          <button data-act="restore" data-name="${escapeHTML(b.name)}" class="danger">Restaurer</button>
          <button data-act="delete"  data-name="${escapeHTML(b.name)}" class="ghost">Suppr</button>
        </td>`;
      tbody.appendChild(tr);
    });
    tbody.querySelectorAll("button[data-act]").forEach(b => b.onclick = () => backupAction(b.dataset.act, b.dataset.name));
  },
  sql: async () => { /* attendre clic Exécuter */ },
};

// --- helpers actions --------------------------------------------------------

async function accountAction(act, id) {
  if (act === "delete") {
    // Récupère le username depuis le DOM pour confirmation forte
    const row = document.querySelector(`button[data-act="delete"][data-id="${id}"]`)?.closest("tr");
    const username = row?.children[1]?.textContent?.trim() || "";
    const c1 = confirm(`⚠ SUPPRIMER le compte « ${username} » (#${id}) ?\n\nTous ses personnages resteront en DB mais inaccessibles.`);
    if (!c1) return;
    const c2 = prompt(`Pour confirmer, retape "${username}" :`);
    if (c2 !== username) return toast("Username incorrect, annulé", "err");
  }
  if (act === "ban" && !confirm("Bannir ce compte ?")) return;
  if (act === "password") {
    const p = prompt("Nouveau mot de passe :");
    if (!p) return;
    await api("/api/accounts", { method: "POST",
      body: JSON.stringify({ action: "password", id, password: p })});
    toast("Mot de passe mis à jour", "ok");
    return;
  }
  await api("/api/accounts", { method: "POST", body: JSON.stringify({ action: act, id })});
  toast("OK", "ok");
  loaders.accounts();
}

async function backupAction(act, name) {
  if (act === "restore" && !confirm(`Restaurer "${name}" ? Les bases auth+world vont être ÉCRASÉES.`)) return;
  if (act === "delete" && !confirm(`Supprimer "${name}" ?`)) return;
  try {
    await api("/api/backups", { method: "POST", body: JSON.stringify({ action: act, name })});
    toast(act + " OK", "ok");
    loaders.backups();
  } catch (e) { toast("Échec : " + e.message, "err"); }
}

// --- form handlers ----------------------------------------------------------

document.addEventListener("DOMContentLoaded", () => {
  $("#create-account").onsubmit = async e => {
    e.preventDefault();
    const f = e.target;
    try {
      await api("/api/accounts", { method: "POST", body: JSON.stringify({
        action: "create",
        username: f.username.value,
        password: f.password.value,
        role: +f.role.value,
      })});
      toast("Compte créé / mis à jour", "ok");
      f.reset();
      loaders.accounts();
    } catch (err) { toast("Échec : " + err.message, "err"); }
  };

  $("#broadcast-form").onsubmit = async e => {
    e.preventDefault();
    const msg = e.target.message.value.trim();
    if (!msg) return;
    await api("/api/broadcast", { method: "POST", body: JSON.stringify({ message: msg })});
    toast("Broadcast en file (livré au prochain tick world)", "ok");
    e.target.reset();
  };

  $("#char-filter").oninput = () => loaders.characters();

  $("#inv-load").onclick = () => {
    const cid = +$("#inv-cid").value;
    if (cid) loaders.inventoryFor(cid);
  };
  $("#inv-dump").onclick = async () => {
    const cid = +$("#inv-cid").value;
    if (!cid) return toast("CharacterId requis", "err");
    await postAction("dump_inventory", String(cid));
    toast("Décodage demandé… reload dans 2s", "ok");
    setTimeout(() => loaders.inventoryFor(cid), 2000);
  };
  $("#inv-picker").onclick = () => {
    if (!+$("#inv-cid").value) return toast("Charge un personnage d'abord", "err");
    openItemPicker();
  };
  $("#inv-search").oninput = () => _invState && renderBagGrid();
  $("#inv-sort").onchange = () => _invState && renderBagGrid();
  if ($("#inv-only-eq")) $("#inv-only-eq").onchange = () => _invState && renderBagGrid();
  $("#item-edit").addEventListener("click", e => { if (e.target.id === "item-edit") closeModal("itemedit"); });

  // Picker controls
  let pickerSearchTimer = null;
  $("#picker-search").oninput = e => {
    clearTimeout(pickerSearchTimer);
    pickerSearchTimer = setTimeout(() => {
      _pickerState.q = e.target.value;
      loadPicker();
    }, 250);
  };

  // Infinite scroll : charge plus quand on est près du bas
  const grid = $("#picker-grid");
  if (grid) {
    grid.addEventListener("scroll", () => {
      const remaining = grid.scrollHeight - grid.scrollTop - grid.clientHeight;
      if (remaining < 200 && !_pickerState.loading && !_pickerState.exhausted) {
        pickerLoadMore();
      }
    });
  }

  // Spells modal
  $("#cm-spells-open").onclick = openSpellsModal;
  $("#cm-pick-item").onclick = openItemPicker;
  $("#spells-dump").onclick = async () => {
    if (!_charModalCid) return;
    await postAction("dump_spells", String(_charModalCid));
    toast("Décodage demandé… 1.5s", "ok");
    setTimeout(loadKnownSpells, 1500);
  };
  $("#spells-search").oninput = e => {
    _spellState.q = e.target.value; _spellState.offset = 0;
    loadSpellCatalog();
  };
  $("#spells-prev").onclick = () => {
    if (_spellState.offset === 0) return;
    _spellState.offset = Math.max(0, _spellState.offset - _spellState.limit);
    loadSpellCatalog();
  };
  $("#spells-next").onclick = () => {
    _spellState.offset += _spellState.limit;
    loadSpellCatalog();
  };

  // Generic close buttons
  document.querySelectorAll("[data-close]").forEach(b => b.onclick = () => closeModal(b.dataset.close));
  $("#item-picker").addEventListener("click", e => { if (e.target.id === "item-picker") closeModal("picker"); });
  $("#spells-modal").addEventListener("click", e => { if (e.target.id === "spells-modal") closeModal("spells"); });

  $("#backup-trigger").onclick = async () => {
    toast("Backup en cours…");
    try {
      await api("/api/backups", { method: "POST", body: JSON.stringify({ action: "trigger" })});
      toast("Backup déclenché", "ok");
      loaders.backups();
    } catch (e) { toast("Échec : " + e.message, "err"); }
  };

});

// --- INVENTORY : paperdoll + bag --------------------------------------------

let _invState = null;

// Slots paperdoll Dofus 2.x (CharacterInventoryPositionEnum)
const PAPERDOLL_SLOTS = [
  { pos: 7,  label: "Casque",   icon: "🎩", col: 1, row: 1 },
  { pos: 0,  label: "Amulette", icon: "◈",  col: 3, row: 1 },
  { pos: 2,  label: "Anneau G", icon: "○",  col: 1, row: 2 },
  { pos: 4,  label: "Anneau D", icon: "○",  col: 3, row: 2 },
  { pos: 1,  label: "Arme",     icon: "⚔",  col: 1, row: 3 },
  { pos: 3,  label: "Bouclier", icon: "⛨",  col: 3, row: 3 },
  { pos: 5,  label: "Ceinture", icon: "▭",  col: 1, row: 4 },
  { pos: 8,  label: "Cape",     icon: "▽",  col: 3, row: 4 },
  { pos: 6,  label: "Bottes",   icon: "👢", col: 1, row: 5 },
  { pos: 9,  label: "Familier", icon: "♞",  col: 3, row: 5 },
];
const DOFUS_SLOTS = [
  { pos: 10, label: "Dofus 1" },
  { pos: 11, label: "Dofus 2" },
  { pos: 12, label: "Dofus 3" },
  { pos: 13, label: "Dofus 4" },
  { pos: 14, label: "Dofus 5" },
  { pos: 15, label: "Dofus 6" },
];

function formatNumber(n) {
  if (!n) return "0";
  return Number(n).toLocaleString("fr-FR");
}

function renderPaperdoll() {
  const root = $("#paperdoll");
  const dofusRow = $("#paperdoll-dofus");
  if (!_invState) { root.innerHTML = ""; dofusRow.innerHTML = ""; return; }

  const byPos = {};
  _invState.items.forEach(it => { byPos[it._pos] = it; });

  // Avatar central
  root.innerHTML = `<div class="paperdoll-avatar"><span class="avatar-placeholder">⚔</span></div>`;

  PAPERDOLL_SLOTS.forEach(s => {
    const it = byPos[s.pos];
    const slot = document.createElement("div");
    slot.className = "slot" + (it ? "" : " empty");
    slot.style.gridColumn = s.col;
    slot.style.gridRow = s.row;
    slot.title = s.label + (it ? " — " + (it.name || "?") : " (vide)");
    if (it) {
      slot.innerHTML = `<img src="${ITEM_ICON(it.gid)}" loading="lazy"
        onerror="this.style.display='none'">${it._qty > 1 ? `<span class="slot-qty">${it._qty}</span>` : ""}`;
      slot.onclick = () => openItemEdit(it);
    } else {
      slot.innerHTML = `<span class="slot-icon-fallback">${s.icon}</span>`;
    }
    root.appendChild(slot);
  });

  // Dofus row
  dofusRow.innerHTML = "";
  DOFUS_SLOTS.forEach(s => {
    const it = byPos[s.pos];
    const slot = document.createElement("div");
    slot.className = "slot" + (it ? "" : " empty");
    slot.title = s.label + (it ? " — " + (it.name || "?") : " (vide)");
    if (it) {
      slot.innerHTML = `<img src="${ITEM_ICON(it.gid)}" loading="lazy" onerror="this.style.display='none'">`;
      slot.onclick = () => openItemEdit(it);
    } else {
      slot.innerHTML = `<span class="slot-icon-fallback">◯</span>`;
    }
    dofusRow.appendChild(slot);
  });
}

function renderInvCatTabs() {
  const wrap = $("#inv-cat-tabs");
  if (!_invState) { wrap.innerHTML = ""; return; }
  // Compte par catégorie (uniquement les items en sac)
  const bagItems = _invState.items.filter(it => !it._eq);
  const counts = { all: bagItems.length };
  bagItems.forEach(it => { counts[it._cat] = (counts[it._cat] || 0) + 1; });

  wrap.innerHTML = "";
  ITEM_CATEGORIES.forEach(cat => {
    const n = counts[cat.key] || 0;
    if (cat.key !== "all" && n === 0) return;
    const btn = document.createElement("button");
    btn.className = (cat.key === _invState.cat) ? "active" : "";
    btn.dataset.cat = cat.key;
    btn.innerHTML = `
      ${cat.icon ? `<span class="cat-icon">${cat.icon}</span>` : ""}
      <span class="cat-badge">${n}</span>`;
    btn.title = cat.label;
    btn.onclick = () => {
      _invState.cat = cat.key;
      renderInvCatTabs();
      renderBagGrid();
    };
    wrap.appendChild(btn);
  });
}

function renderBagGrid() {
  const root = $("#bag-grid");
  if (!_invState) { root.innerHTML = ""; return; }
  const { items, cat } = _invState;
  const search = ($("#inv-search").value || "").trim().toLowerCase();
  const sort = $("#inv-sort").value || "name";

  let list = items.filter(it => !it._eq); // sac uniquement (équipés sont dans le paperdoll)
  if (cat !== "all") list = list.filter(it => it._cat === cat);
  if (search)        list = list.filter(it => (it.name || "").toLowerCase().includes(search));

  const sortFns = {
    name: (a, b) => (a.name || "").localeCompare(b.name || ""),
    level: (a, b) => (b._level || 0) - (a._level || 0),
    "level-asc": (a, b) => (a._level || 0) - (b._level || 0),
    qty: (a, b) => b._qty - a._qty,
    type: (a, b) => (a.type || 0) - (b.type || 0),
  };
  list.sort(sortFns[sort] || sortFns.name);

  $("#bag-count").textContent = `${list.length} objet${list.length > 1 ? "s" : ""}`;

  root.innerHTML = "";
  if (list.length === 0) {
    root.innerHTML = `<div class="muted small" style="grid-column:1/-1; text-align:center; padding:30px;">Aucun objet dans cette catégorie.</div>`;
    return;
  }
  list.forEach(it => {
    const slot = document.createElement("div");
    slot.className = "slot in-bag";
    slot.title = `${it.name || "?"} · niv ${it._level} · ${it._qty}× · UID ${it.uid}`;
    slot.innerHTML = `<img src="${ITEM_ICON(it.gid)}" loading="lazy" onerror="this.style.display='none'">${it._qty > 1 ? `<span class="slot-qty">${it._qty}</span>` : ""}`;
    slot.onclick = () => openItemEdit(it);
    root.appendChild(slot);
  });
}

// Modale d'édition d'un item (réutilise renderItemCard)
function openItemEdit(it) {
  $("#item-edit-title").innerHTML = `<span style="color:var(--gold)">${escapeHTML(it.name || "?")}</span> <span class="muted small">UID ${it.uid} · GID ${it.gid}</span>`;
  const body = $("#item-edit-body");
  body.innerHTML = "";
  const card = renderItemCard(_invState.cid, it, _invState.useParsed);
  card.style.borderColor = "transparent";
  card.style.background = "transparent";
  card.style.padding = "0";
  body.appendChild(card);
  $("#item-edit").classList.remove("hidden");
}

// --- item card rendering ----------------------------------------------------

function effectName(id) {
  const f = EFFECTS.find(e => e.id === id);
  return f ? f.name : `Effet #${id}`;
}

function effectOptions(selectedId) {
  const known = EFFECTS.some(e => e.id === selectedId);
  let html = "";
  // Si l'ID n'est pas dans le catalogue, on ajoute une option dédiée pour
  // ne pas faire mentir le dropdown (sinon il fallback sur la 1ère option).
  if (!known && selectedId !== undefined && selectedId !== null) {
    html += `<option value="${selectedId}" selected>[${selectedId}] Effet #${selectedId} (inconnu)</option>`;
  }
  html += EFFECTS.map(e =>
    `<option value="${e.id}" ${e.id === selectedId ? "selected" : ""}>[${e.id}] ${e.name}</option>`
  ).join("");
  return html;
}

function renderItemCard(cid, it, parsed) {
  const card = document.createElement("div");
  const equipped = it.eq === 1 || it.position !== 63;
  card.className = "item-card" + (equipped ? " equipped" : "");

  const headHtml = `
    <div class="item-head">
      <img class="item-icon" src="${ITEM_ICON(it.gid)}" loading="lazy"
           onerror="this.style.opacity=0.2;this.src='data:image/svg+xml;utf8,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 24 24%22><rect width=%2224%22 height=%2224%22 fill=%22%23222%22/></svg>'">
      <div>
        <div class="item-name">${escapeHTML(it.name || "?")}</div>
        <div class="item-meta">UID ${it.uid} · GID ${it.gid} · pos ${it.position ?? it.pos ?? 63}${equipped?" · <span class='up'>équipé</span>":""}</div>
      </div>
    </div>`;

  // effects (only if parsed dump available)
  let effectsHtml = "";
  if (parsed && Array.isArray(it.effects)) {
    effectsHtml = `<div class="item-effects">`;
    if (it.effects.length === 0) {
      effectsHtml += `<div class="muted">Aucun effet</div>`;
    } else {
      it.effects.forEach((eff, idx) => {
        effectsHtml += `
          <div class="effect-row">
            <select class="eff-id" data-uid="${it.uid}" data-idx="${idx}">${effectOptions(eff.id)}</select>
            <input type="number" class="eff-val" value="${eff.value}" data-uid="${it.uid}" data-idx="${idx}">
            <button class="x-btn" data-act="eff-del" data-uid="${it.uid}" data-idx="${idx}">×</button>
          </div>`;
      });
    }
    effectsHtml += `
      <div class="add-effect">
        <select class="eff-add-id" data-uid="${it.uid}">${effectOptions(EFFECTS[0].id)}</select>
        <input type="number" class="eff-add-val" data-uid="${it.uid}" placeholder="Valeur" value="0">
        <button data-act="eff-add" data-uid="${it.uid}">+ Ajouter</button>
      </div>`;
    effectsHtml += `</div>`;
  } else {
    effectsHtml = `<div class="muted">Effets : décodage requis (joueur online)</div>`;
  }

  // actions
  const actionsHtml = `
    <div class="item-actions">
      <span class="muted">Qté :</span>
      <input type="number" class="it-qty" data-uid="${it.uid}" value="${it.qty ?? it.quantity ?? 1}">
      <button data-act="qty" data-uid="${it.uid}">Save</button>
      <span class="muted">Pos :</span>
      <input type="number" class="it-pos" data-uid="${it.uid}" value="${it.position ?? it.pos ?? 63}">
      <button data-act="pos" data-uid="${it.uid}" class="ghost">Move</button>
      <button data-act="del" data-uid="${it.uid}" class="danger">Suppr</button>
    </div>`;

  card.innerHTML = headHtml + effectsHtml + actionsHtml;

  // wire actions
  card.querySelectorAll("button[data-act='eff-del']").forEach(b => b.onclick = async () => {
    if (!confirm("Supprimer cet effet ?")) return;
    await postAction("item_eff_del", `${cid}|${b.dataset.uid}|${b.dataset.idx}`);
    setTimeout(() => loaders.inventoryFor(cid), 1500);
  });
  card.querySelectorAll("button[data-act='eff-add']").forEach(b => b.onclick = async () => {
    const id = +card.querySelector(".eff-add-id").value;
    const val = +card.querySelector(".eff-add-val").value;
    await postAction("item_eff_add", `${cid}|${b.dataset.uid}|${id}|${val}`);
    setTimeout(() => loaders.inventoryFor(cid), 1500);
  });
  card.querySelectorAll("input.eff-val").forEach(inp => {
    inp.onchange = async () => {
      const uid = +inp.dataset.uid, idx = +inp.dataset.idx, v = +inp.value;
      await postAction("item_eff_set", `${cid}|${uid}|${idx}|${v}`);
      setTimeout(() => loaders.inventoryFor(cid), 1500);
    };
  });
  card.querySelectorAll("select.eff-id").forEach(sel => {
    sel.onchange = async () => {
      // Change d'effet = delete + re-add (preserve la valeur)
      const uid = +sel.dataset.uid, idx = +sel.dataset.idx;
      const newId = +sel.value;
      const valInput = card.querySelector(`input.eff-val[data-uid='${uid}'][data-idx='${idx}']`);
      const v = valInput ? +valInput.value : 0;
      await postAction("item_eff_del", `${cid}|${uid}|${idx}`);
      await postAction("item_eff_add", `${cid}|${uid}|${newId}|${v}`);
      setTimeout(() => loaders.inventoryFor(cid), 2000);
    };
  });

  card.querySelector("button[data-act='qty']").onclick = async () => {
    const uid = card.querySelector("input.it-qty").dataset.uid;
    const v = +card.querySelector("input.it-qty").value;
    await postAction("item_set_qty", `${cid}|${uid}|${v}`);
    setTimeout(() => loaders.inventoryFor(cid), 1500);
  };
  card.querySelector("button[data-act='pos']").onclick = async () => {
    const uid = card.querySelector("input.it-pos").dataset.uid;
    const v = +card.querySelector("input.it-pos").value;
    await postAction("item_set_pos", `${cid}|${uid}|${v}`);
    setTimeout(() => loaders.inventoryFor(cid), 1500);
  };
  card.querySelector("button[data-act='del']").onclick = async () => {
    if (!confirm("Supprimer cet item ?")) return;
    const uid = +card.querySelector("input.it-qty").dataset.uid;
    await postAction("item_delete", `${cid}|${uid}`);
    setTimeout(() => loaders.inventoryFor(cid), 1500);
  };

  return card;
}

// --- ITEM PICKER -----------------------------------------------------------

let _pickerState = {
  offset: 0, limit: 60, q: "",
  cat: "all", subcat: null,
  types: null, excludeTypes: null, usable: null,
  loading: false, exhausted: false,
};

function pickerQueryParams() {
  const params = new URLSearchParams({
    offset: String(_pickerState.offset),
    limit: String(_pickerState.limit),
  });
  if (_pickerState.q) params.set("q", _pickerState.q);
  if (_pickerState.types) params.set("type", _pickerState.types.join(","));
  if (_pickerState.excludeTypes) params.set("excludeType", _pickerState.excludeTypes.join(","));
  if (_pickerState.usable !== null) params.set("usable", _pickerState.usable ? "1" : "0");
  return params;
}

function pickerCardHtml(it) {
  const card = document.createElement("div");
  card.className = "picker-card";
  card.dataset.gid = it.id;
  card.innerHTML = `
    <img src="${ITEM_ICON(it.id)}" loading="lazy" onerror="this.style.opacity=.2">
    <div class="pc-name">${escapeHTML(it.name)}</div>
    <div class="pc-meta">#${it.id} · niv ${it.level}</div>
    <div class="pc-give">+1</div>`;
  card.onclick = () => pickerGive(it, 1);
  card.oncontextmenu = e => { e.preventDefault(); pickerGive(it, "?"); };
  return card;
}

// Reset complet (changement filtre / catégorie / search)
async function loadPicker() {
  _pickerState.offset = 0;
  _pickerState.exhausted = false;
  _pickerState.loading = false;
  const grid = $("#picker-grid");
  grid.innerHTML = "";
  $("#picker-end").textContent = "";
  await pickerLoadMore();
}

async function pickerLoadMore() {
  if (_pickerState.loading || _pickerState.exhausted) return;
  _pickerState.loading = true;
  $("#picker-end").textContent = "Chargement…";
  try {
    const items = await api("/api/items/catalog?" + pickerQueryParams().toString());
    const grid = $("#picker-grid");
    if (_pickerState.offset === 0 && (!items || items.length === 0)) {
      grid.innerHTML = `<div class="empty-state" style="grid-column:1/-1;"><h3>Aucun résultat</h3></div>`;
      _pickerState.exhausted = true;
      $("#picker-end").textContent = "";
      return;
    }
    (items || []).forEach(it => grid.appendChild(pickerCardHtml(it)));
    if (!items || items.length < _pickerState.limit) {
      _pickerState.exhausted = true;
      $("#picker-end").textContent = grid.children.length > 0 ? "✦ Fin de la liste ✦" : "";
    } else {
      _pickerState.offset += _pickerState.limit;
      $("#picker-end").textContent = "";
    }
  } catch (e) {
    $("#picker-end").innerHTML = `<span class="down">Erreur: ${escapeHTML(e.message)}</span>`;
  } finally {
    _pickerState.loading = false;
  }
}

let _pickerBulkMode = false;

async function pickerGive(item, qtyHint) {
  let qty = qtyHint;
  if (qty === "?" || qty === undefined || qty === null) {
    qty = prompt(`Quantité de « ${item.name} » à donner :`, "1");
    if (!qty) return;
  }
  const card = $("#picker-grid").querySelector(`[data-gid="${item.id}"]`);
  if (card) {
    card.classList.add("giving");
    setTimeout(() => card.classList.remove("giving"), 800);
  }
  try {
    if (_pickerBulkMode) {
      if (!confirm(`Donner « ${item.name} » × ${qty} à TOUS les joueurs en ligne ?`)) return;
      await postAction("bulk_give_item", `${item.id}|${qty}`);
      toast(`Bulk : « ${item.name} » × ${qty} envoyé`, "ok");
      return;
    }
    const cid = +$("#inv-cid").value || _charModalCid;
    if (!cid) return toast("CharacterId requis (charger un inventaire d'abord)", "err");
    await postAction("give_item", `${cid}|${item.id}|${qty}`);
    toast(`« ${item.name} » × ${qty} donné`, "ok");
    // Reload silencieux de l'inventaire si on est sur la page inventaire
    setTimeout(() => { if (loaders.inventoryFor) loaders.inventoryFor(cid); }, 1500);
  } catch (e) {
    toast("Erreur: " + e.message, "err");
  }
}

const MODAL_IDS = { picker: "#item-picker", spells: "#spells-modal", char: "#char-modal", itemedit: "#item-edit" };
function closeModal(name) {
  const sel = MODAL_IDS[name];
  if (sel) $(sel).classList.add("hidden");
  if (name === "picker") _pickerBulkMode = false;
}

function openItemPicker() {
  _pickerState = {
    offset: 0, limit: 60, q: "",
    cat: "all", subcat: null,
    types: null, excludeTypes: null, usable: null,
    loading: false, exhausted: false,
  };
  buildCatChips();
  buildSubCatChips();
  $("#picker-search").value = "";
  // Titre avec indicateur bulk
  const head = $("#item-picker .modal-head h2");
  if (head) head.innerHTML = _pickerBulkMode
    ? `Catalogue d'objets — <span style="color:var(--green)">BULK (tous online)</span>`
    : `Catalogue d'objets`;
  loadPicker();
  $("#item-picker").classList.remove("hidden");
}

function applyPickerCatFilter() {
  // Reset
  _pickerState.types = null;
  _pickerState.excludeTypes = null;
  _pickerState.usable = null;
  _pickerState.offset = 0;

  const cat = ITEM_CATEGORIES.find(c => c.key === _pickerState.cat);
  if (!cat || cat.key === "all") return;

  // Sous-catégorie sélectionnée → ses types l'emportent
  if (_pickerState.subcat) {
    const sub = (cat.subcats || []).find(s => s.key === _pickerState.subcat);
    if (sub) {
      _pickerState.types = sub.types;
      // Pour utilisable/ressource, ajoute aussi le filtre Usable pour rester cohérent
      if (cat.key === "utilisable") _pickerState.usable = true;
      if (cat.key === "ressource")  _pickerState.usable = false;
      return;
    }
  }

  // Pas de sous-cat → filtre par toute la catégorie principale
  if (cat.key === "utilisable") {
    _pickerState.usable = true;
    _pickerState.excludeTypes = fixedCategoryTypes();
  } else if (cat.key === "ressource") {
    _pickerState.usable = false;
    _pickerState.excludeTypes = fixedCategoryTypes();
  } else {
    const types = categoryTypes(cat);
    if (types) _pickerState.types = types;
  }
}

function buildCatChips() {
  const wrap = $("#picker-cats");
  wrap.innerHTML = "";
  ITEM_CATEGORIES.forEach(cat => {
    const chip = document.createElement("span");
    chip.className = "cat-chip" + (cat.key === _pickerState.cat ? " active" : "");
    chip.textContent = (cat.icon ? cat.icon + " " : "") + cat.label;
    chip.onclick = () => {
      _pickerState.cat = cat.key;
      _pickerState.subcat = null;
      buildCatChips();
      buildSubCatChips();
      applyPickerCatFilter();
      loadPicker();
    };
    wrap.appendChild(chip);
  });
}

function buildSubCatChips() {
  const wrap = $("#picker-subcats");
  wrap.innerHTML = "";
  const cat = ITEM_CATEGORIES.find(c => c.key === _pickerState.cat);
  if (!cat || !cat.subcats || !cat.subcats.length) {
    wrap.classList.add("empty");
    return;
  }
  wrap.classList.remove("empty");

  // "Tout" pour cette catégorie
  const allChip = document.createElement("span");
  allChip.className = "cat-chip" + (!_pickerState.subcat ? " active" : "");
  allChip.textContent = "Tout";
  allChip.onclick = () => {
    _pickerState.subcat = null;
    buildSubCatChips();
    applyPickerCatFilter();
    loadPicker();
  };
  wrap.appendChild(allChip);

  cat.subcats.forEach(sub => {
    const chip = document.createElement("span");
    chip.className = "cat-chip" + (_pickerState.subcat === sub.key ? " active" : "");
    chip.textContent = sub.label;
    chip.onclick = () => {
      _pickerState.subcat = sub.key;
      buildSubCatChips();
      applyPickerCatFilter();
      loadPicker();
    };
    wrap.appendChild(chip);
  });
}

// --- SPELLS MODAL ----------------------------------------------------------

let _spellState = { offset: 0, limit: 30, q: "", knownSpells: [] };

async function openSpellsModal() {
  if (!_charModalCid) return toast("Sélectionne un personnage d'abord", "err");
  $("#spells-title-name").textContent = `(personnage #${_charModalCid})`;
  $("#spells-modal").classList.remove("hidden");
  await loadKnownSpells();
  await loadSpellCatalog();
}

async function loadKnownSpells() {
  if (!_charModalCid) return;
  const r = await api("/api/spells/parsed?characterId=" + _charModalCid);
  _spellState.knownSpells = r.spells || [];
  renderKnownSpells();
}

function renderKnownSpells() {
  const root = $("#spells-known");
  root.innerHTML = "";
  if (!_spellState.knownSpells.length) {
    root.innerHTML = `<div class='muted small' style='grid-column:1/-1;'>Aucun sort connu (ou non décodé — clique « Charger »).</div>`;
    return;
  }
  _spellState.knownSpells.forEach(s => {
    const card = document.createElement("div");
    card.className = "spell-card known";
    card.innerHTML = `
      <span class="sp-grade">${s.grade ?? 1}</span>
      <span class="sp-x">×</span>
      <img src="${SPELL_ICON(s.id)}" loading="lazy" onerror="this.style.opacity=.2">
      <div class="sp-name">#${s.id}</div>`;
    card.querySelector(".sp-x").onclick = async (e) => {
      e.stopPropagation();
      if (!confirm(`Oublier le sort #${s.id} ?`)) return;
      await postAction("forget_spell", `${_charModalCid}|${s.id}`);
      setTimeout(loadKnownSpells, 1500);
    };
    root.appendChild(card);
  });
}

async function loadSpellCatalog() {
  const params = new URLSearchParams({
    offset: String(_spellState.offset),
    limit: String(_spellState.limit),
  });
  if (_spellState.q) params.set("q", _spellState.q);
  const list = await api("/api/spells/catalog?" + params.toString());
  const root = $("#spells-catalog");
  root.innerHTML = "";
  (list || []).forEach(s => {
    const card = document.createElement("div");
    card.className = "spell-card";
    card.innerHTML = `
      <img src="${SPELL_ICON(s.id)}" loading="lazy" onerror="this.style.opacity=.2">
      <div class="sp-name">${escapeHTML(s.name) || ("#"+s.id)}</div>`;
    card.title = `#${s.id} · ${s.category}`;
    card.onclick = async () => {
      await postAction("learn_spell", `${_charModalCid}|${s.id}`);
      toast(`Sort « ${s.name} » appris`, "ok");
      setTimeout(loadKnownSpells, 1500);
    };
    root.appendChild(card);
  });
  $("#spells-page").textContent = `page ${Math.floor(_spellState.offset / _spellState.limit) + 1}`;
}

// --- char actions modal -----------------------------------------------------

let _charModalCid = null;

function openCharModal(cid, name, online) {
  _charModalCid = cid;
  $("#char-modal-title span").textContent = `${name} (#${cid})`;
  const note = $("#char-modal-online");
  if (note) note.innerHTML = online
    ? '<span class="up">● en ligne</span>'
    : '<span class="dim">● hors ligne — les actions live (heal/kick/teleport/...) seront ignorées par le poller, seuls les <b>give_*</b> fonctionnent à la prochaine connexion.</span>';
  $("#char-modal").classList.remove("hidden");
}

function closeCharModal() {
  $("#char-modal").classList.add("hidden");
  _charModalCid = null;
  ["#cm-pm","#cm-mapid","#cm-cellid","#cm-kamas","#cm-level","#cm-xp","#cm-gid"].forEach(s => $(s).value = "");
  $("#cm-qty").value = "1";
}

async function postAction(type, payload) {
  await api("/api/action", { method: "POST",
    body: JSON.stringify({ type, payload })});
  toast(`Action « ${type} » filée`, "ok");
}

document.addEventListener("DOMContentLoaded", () => {
  const modal = $("#char-modal");
  $("#char-modal-close").onclick = closeCharModal;
  modal.addEventListener("click", e => { if (e.target === modal) closeCharModal(); });

  modal.querySelectorAll("button[data-act]").forEach(b => b.onclick = async () => {
    if (!_charModalCid) return;
    const cid = _charModalCid;
    const a = b.dataset.act;
    let payload = "";
    switch (a) {
      case "heal":
      case "kick":
      case "reload_inventory":
        payload = String(cid);
        if (a === "kick") payload = `${cid}|admin`;
        break;
      case "send_pm": {
        const m = $("#cm-pm").value.trim();
        if (!m) return toast("Message vide", "err");
        payload = `${cid}|${m}`;
        break;
      }
      case "teleport": {
        const m = $("#cm-mapid").value;
        if (!m) return toast("MapId requis", "err");
        const c = $("#cm-cellid").value;
        payload = c ? `${cid}|${m}|${c}` : `${cid}|${m}`;
        break;
      }
      case "set_kamas":
      case "give_kamas": {
        const k = $("#cm-kamas").value;
        if (!k) return toast("Kamas requis", "err");
        payload = `${cid}|${k}`;
        break;
      }
      case "set_level": {
        const l = $("#cm-level").value;
        if (!l) return toast("Niveau requis", "err");
        payload = `${cid}|${l}`;
        break;
      }
      case "give_xp": {
        const x = $("#cm-xp").value;
        if (!x) return toast("XP requis", "err");
        payload = `${cid}|${x}`;
        break;
      }
      case "give_item": {
        const g = $("#cm-gid").value;
        const q = $("#cm-qty").value || "1";
        if (!g) return toast("GId requis", "err");
        payload = `${cid}|${g}|${q}`;
        break;
      }
      case "reset_spells": {
        if (!confirm("Vraiment reset tous les sorts ?")) return;
        payload = String(cid);
        break;
      }
      case "reset_character": {
        const c1 = confirm("⚠ Reset le personnage : niveau 1, kamas 0, sorts vidés. Continuer ?");
        if (!c1) return;
        payload = String(cid);
        break;
      }
      case "delete_character": {
        const name = $("#char-modal-title span").textContent.split(" (#")[0];
        const c1 = confirm(`⚠ SUPPRIMER DÉFINITIVEMENT le personnage « ${name} » ?`);
        if (!c1) return;
        const c2 = prompt(`Tape "${name}" pour confirmer :`);
        if (c2 !== name) return toast("Nom incorrect, annulé", "err");
        payload = String(cid);
        break;
      }
      case "set_breed": {
        const b = $("#cm-breed").value;
        if (!b) return toast("Choisir une classe", "err");
        payload = `${cid}|${b}`;
        break;
      }
      case "set_sex": {
        const s = $("#cm-sex").value;
        if (s === "") return toast("Choisir un sexe", "err");
        payload = `${cid}|${s}`;
        break;
      }
      case "set_look": {
        const l = $("#cm-look").value.trim();
        if (!l) return toast("Look requis", "err");
        payload = `${cid}|${l}`;
        break;
      }
    }
    await postAction(a, payload);
  });

  // Global server actions
  $("#act-save-now").onclick = () => postAction("save_now", "");
  $("#act-reload-items").onclick = () => postAction("reload_items", "");
  $("#act-shutdown").onclick = async () => {
    const s = prompt("Shutdown — délai en secondes (5-3600) :", "60");
    if (!s) return;
    const m = prompt("Message :", "Maintenance imminente.");
    await postAction("shutdown", `${s}|${m || ""}`);
  };

  // Bulk actions (dashboard)
  $("#bulk-kamas-btn").onclick = async () => {
    const v = +$("#bulk-kamas").value;
    if (!v) return toast("Montant requis", "err");
    if (!confirm(`Donner ${v.toLocaleString()} kamas à tous les joueurs en ligne ?`)) return;
    await postAction("bulk_give_kamas", String(v));
    toast("Bulk kamas distribué", "ok");
  };
  $("#bulk-xp-btn").onclick = async () => {
    const v = +$("#bulk-xp").value;
    if (!v) return toast("XP requise", "err");
    if (!confirm(`Donner ${v.toLocaleString()} XP à tous les joueurs en ligne ?`)) return;
    await postAction("bulk_give_xp", String(v));
    toast("Bulk XP distribuée", "ok");
  };
  $("#bulk-heal-btn").onclick = async () => {
    if (!confirm("Heal général à tous les joueurs en ligne ?")) return;
    await postAction("bulk_heal", "");
    toast("Heal général envoyé", "ok");
  };
  $("#bulk-item-btn").onclick = async () => {
    const g = +$("#bulk-gid").value;
    const q = +$("#bulk-qty").value || 1;
    if (!g) return toast("GId requis", "err");
    if (!confirm(`Donner item GId ${g} × ${q} à tous les joueurs en ligne ?`)) return;
    await postAction("bulk_give_item", `${g}|${q}`);
    toast("Bulk item distribué", "ok");
  };
  $("#bulk-item-pick").onclick = () => {
    // Mode picker pour bulk : on stocke un flag temporaire
    _pickerBulkMode = true;
    openItemPicker();
  };
});

// --- articles --------------------------------------------------------------

function editArticle(id, list) {
  const a = (list || []).find(x => x.id === id);
  if (!a) return;
  const f = document.forms["article-form"] || $("#article-form");
  f.elements["id"].value          = a.id;
  f.elements["slug"].value        = a.slug || "";
  f.elements["title"].value       = a.title || "";
  f.elements["excerpt"].value     = a.excerpt || "";
  f.elements["coverImage"].value  = a.coverImage || "";
  f.elements["tag"].value         = a.tag || "";
  f.elements["content"].value     = a.content || "";
  f.elements["published"].checked = !!a.published;
  f.scrollIntoView({ behavior: "smooth", block: "center" });
}

document.addEventListener("DOMContentLoaded", () => {
  const f = $("#article-form");
  if (!f) return;
  f.addEventListener("submit", async (e) => {
    e.preventDefault();
    const fd = new FormData(f);
    const id = +fd.get("id") || 0;
    const body = {
      action: id ? "update" : "create",
      id,
      slug: fd.get("slug")?.trim() || "",
      title: fd.get("title")?.trim() || "",
      excerpt: fd.get("excerpt") || "",
      coverImage: fd.get("coverImage") || "",
      tag: fd.get("tag") || "",
      content: fd.get("content") || "",
      published: fd.get("published") === "on",
    };
    try {
      await api("/api/articles", { method: "POST", body: JSON.stringify(body) });
      toast(id ? "Article mis à jour" : "Article publié", "ok");
      f.reset();
      f.elements["id"].value = "";
      loaders.articles();
    } catch (e2) {
      toast(String(e2.message || e2), "err");
    }
  });
  $("#article-form-reset")?.addEventListener("click", () => {
    setTimeout(() => { f.elements["id"].value = ""; }, 0);
  });
});

// --- utils ------------------------------------------------------------------

function escapeHTML(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({
    "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;"
  })[c]);
}

function humanSize(n) {
  if (!n) return "0";
  const u = ["B","K","M","G"];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(i ? 1 : 0) + u[i];
}

// --- Unhandled (debug actions joueur non gérées) ---------------------------

function unhandledQueryString() {
  const q = new URLSearchParams();
  const cat = $("#unhandled-cat-filter")?.value;
  const cid = $("#unhandled-char-filter")?.value?.trim();
  const lim = $("#unhandled-limit")?.value;
  if (cat) q.set("category", cat);
  if (cid) q.set("characterId", cid);
  if (lim) q.set("limit", lim);
  return q.toString();
}

async function loadUnhandled() {
  try {
    const data = await api("/api/unhandled?" + unhandledQueryString());
    const events = data.events || [];
    const byCat = data.byCategory || {};

    // Update category filter options (only if structure changed)
    const sel = $("#unhandled-cat-filter");
    if (sel) {
      const current = sel.value;
      const cats = Object.keys(byCat).sort();
      const wanted = ["", ...cats].map(c => c).join("|");
      const have = Array.from(sel.options).map(o => o.value).join("|");
      if (wanted !== have) {
        sel.innerHTML = `<option value="">— toutes —</option>` +
          cats.map(c => `<option value="${escapeHTML(c)}">${escapeHTML(c)} (${byCat[c]})</option>`).join("");
        sel.value = current;
      }
    }

    $("#unhandled-count").textContent =
      `${events.length} affichés / ${data.total || 0} total`;

    // Badge global dans la sidebar
    const badge = $("#unhandled-badge");
    if (badge) {
      const total = data.total || 0;
      if (total > 0) {
        badge.textContent = total > 999 ? "999+" : String(total);
        badge.hidden = false;
      } else {
        badge.hidden = true;
      }
    }

    const tbody = $("#unhandled-table tbody");
    tbody.innerHTML = "";
    events.forEach(e => {
      const tr = document.createElement("tr");
      const charCell = e.characterId
        ? `${escapeHTML(e.characterName || "")} <span class="muted">#${e.characterId}</span>`
        : `<span class="muted">—</span>`;
      tr.innerHTML = `
        <td><span class="muted">${new Date(e.atUtc).toLocaleString()}</span></td>
        <td><code>${escapeHTML(e.category)}</code></td>
        <td>${charCell}</td>
        <td><code style="white-space:pre-wrap">${escapeHTML(e.detail)}</code></td>
        <td>${e.payload
          ? `<details><summary><span class="muted">voir (${e.payload.length} c.)</span></summary><pre style="white-space:pre-wrap">${escapeHTML(e.payload)}</pre></details>`
          : `<span class="muted">—</span>`}</td>
        <td><button class="ghost" style="padding:4px 8px" data-unh-del="${e.id}">✕</button></td>`;
      tbody.appendChild(tr);
    });

    tbody.querySelectorAll("[data-unh-del]").forEach(b => {
      b.addEventListener("click", async () => {
        await api("/api/unhandled?id=" + b.dataset.unhDel, { method: "DELETE" });
        loadUnhandled();
      });
    });
  } catch (e) {
    toast("Erreur unhandled : " + (e.message || e), "err");
  }
}

async function copyUnhandledForClaude() {
  try {
    const r = await fetch("/api/unhandled?format=md&" + unhandledQueryString(), {
      credentials: "include",
    });
    if (!r.ok) throw new Error("HTTP " + r.status);
    const md = await r.text();
    await navigator.clipboard.writeText(md);
    toast("📋 " + md.length + " caractères copiés. Colle-le à Claude.", "ok");
  } catch (e) {
    toast("Copie échouée : " + (e.message || e), "err");
  }
}

async function clearUnhandled() {
  const filter = unhandledQueryString();
  const all = !filter || (!$("#unhandled-cat-filter").value && !$("#unhandled-char-filter").value.trim());
  const msg = all
    ? "Effacer TOUS les events non gérés ?"
    : "Effacer les events filtrés ?";
  if (!confirm(msg)) return;
  const q = all ? "all=1" : filter;
  const r = await api("/api/unhandled?" + q, { method: "DELETE" });
  toast(`${r.deleted} entrées effacées`, "ok");
  loadUnhandled();
}

document.addEventListener("DOMContentLoaded", () => {
  $("#unhandled-refresh")?.addEventListener("click", loadUnhandled);
  $("#unhandled-copy")?.addEventListener("click", copyUnhandledForClaude);
  $("#unhandled-clear")?.addEventListener("click", clearUnhandled);
  $("#unhandled-cat-filter")?.addEventListener("change", loadUnhandled);
  $("#unhandled-char-filter")?.addEventListener("change", loadUnhandled);
  $("#unhandled-limit")?.addEventListener("change", loadUnhandled);

  // Refresh badge silencieusement toutes les 15s même hors onglet
  setInterval(async () => {
    try {
      const r = await fetch("/api/unhandled?limit=1", { credentials: "include" });
      if (!r.ok) return;
      const d = await r.json();
      const badge = $("#unhandled-badge");
      if (!badge) return;
      const total = d.total || 0;
      if (total > 0) {
        badge.textContent = total > 999 ? "999+" : String(total);
        badge.hidden = false;
      } else {
        badge.hidden = true;
      }
    } catch (_) { /* ignore */ }
  }, 15000);
});
