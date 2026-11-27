'use strict';

/* ==========================================================================
   Steam Achievement Manager Modern - user interface

   A single-page app in plain JavaScript (no framework, no build step). How it is put together:

   * ONE global `state` object holds everything the UI knows (library, filters, open game, running jobs ...).
   * "render" functions turn state into HTML strings and assign them to a container's innerHTML. Most re-render
     the whole container; the few places that must not (a polled dialog, an input that has focus) update parts in place.
   * Events are delegated: one click/input/change listener on `document` looks for `data-action="..."` on the
     clicked element and dispatches to `handleAction` (see "Events" at the bottom).
   * All data comes from the local host over a small JSON API (`api()`); long jobs (progress check, auto-unlock,
     loading friends' progress) are started with a POST and then POLLED with a cursor until they report "not running".
   * Preferences (theme, filters, saved views ...) are saved by the host in prefs.json, not in the browser, because
     the local port - and with it the browser's storage origin - changes on every start.
   * Everything shown to the user is English.

   Layout of this file: helpers -> preferences -> state -> API -> filtering/sorting -> quick views -> modals ->
   top bar and accounts -> startup and routing -> library (sidebar, tiles, cards, friends' progress, progress check,
   auto-unlock, friends comparison) -> game detail -> events -> boot.
   ========================================================================== */

/* ---------- Helpers ---------- */
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const nf = new Intl.NumberFormat('en-US');
const fmtDate = (unix) => new Date(unix * 1000).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
const pctText = (p) => (p == null ? '–' : p >= 1 ? '100%' : `${Math.min(99, Math.round(p * 100))}%`);
const hash = (s) => { let h = 0; for (const c of String(s)) h = (h * 31 + c.charCodeAt(0)) | 0; return Math.abs(h); };
const plural = (n, one, many) => `${nf.format(n)} ${n === 1 ? one : many}`;

/* ---------- Preferences (stored on disk by the app, because the local port changes on every start) ---------- */
const PREFS = {};
let prefsTimer = null;
const LS = {
  get(k, d) { return PREFS[k] === undefined ? d : PREFS[k]; },
  set(k, v) { PREFS[k] = v; clearTimeout(prefsTimer); prefsTimer = setTimeout(savePrefs, 400); },
};
/**
 * Saves the preferences to the host (debounced by `LS.set`). Failures are ignored - preferences are a
 * convenience.
 */
function savePrefs() { api('/api/prefs', { method: 'POST', body: PREFS }).catch(() => {}); }
/** Loads the preferences saved by the host; on failure the defaults are used. */
async function loadPrefs() { try { Object.assign(PREFS, (await api('/api/prefs')) || {}); } catch { /* start with defaults */ } }

/* ---------- State ---------- */
const defaultTypes = () => ({ normal: true, demo: false, mod: false, junk: false });
const defaultFilters = () => ({
  q: '', status: 'all', hideComplete: false, hideNone: false, ingame: 'all', news: 'all',
  pctMin: 0, pctMax: 100, minAch: 0, types: defaultTypes(), installed: 'all', played: 'all',
});
const DEFAULT_COLLAPSED = { progress: true, minach: true, types: true, install: true, played: true, news: true, friends: true };

const state = {
  token: '',
  status: null,
  ready: false,
  library: null,
  games: [],
  byId: new Map(),
  filters: defaultFilters(),
  idFilter: null,          // array of app IDs a saved "selected games" view restricts the list to
  idSet: null,
  sort: { key: 'name', dir: 'asc' },
  view: 'grid',
  size: 260,
  theme: 'dark',
  views: [],               // user-saved quick views
  collapsed: { ...DEFAULT_COLLAPSED },
  select: { on: false, ids: new Set() },
  route: { name: 'library' },
  game: null,
  scan: { running: false, total: 0, done: 0, current: 0, failed: 0, error: null, polling: false },
  switching: null,
  friends: { settings: { enabled: false, auto: false, selected: [] }, list: null, byId: new Map(), loading: false },
  friendProg: { games: {}, scan: { running: false, total: 0, done: 0, current: 0, failed: 0, skipped: 0, error: null, polling: false } },   // selected friends' progress per game (counts only)
  popover: null,
  auto: { running: false, finished: false, mode: 'unlock', total: 0, done: 0, changed: 0, problems: 0, current: 0, currentName: '', results: [], error: null, polling: false, canUndo: false, showDetails: false, cancelling: false },
};

/**
 * Copies the loaded preferences into `state` (filters, sort, view, card size, theme, saved views, collapsed
 * sections). The search text is never restored.
 */
function applyPrefs() {
  const f = PREFS.filters || {};
  state.filters = { ...defaultFilters(), ...f, q: '', types: { ...defaultTypes(), ...(f.types || {}) } };
  setIdFilter(Array.isArray(PREFS.idFilter) ? PREFS.idFilter : null);
  state.sort = PREFS.sort || { key: 'name', dir: 'asc' };
  state.view = PREFS.view || 'grid';
  state.size = PREFS.size || 260;
  state.theme = PREFS.theme || 'dark';
  state.views = Array.isArray(PREFS.views) ? PREFS.views : [];
  state.collapsed = { ...DEFAULT_COLLAPSED, ...(PREFS.collapsed || {}) };
}
/**
 * Restricts the library to a fixed list of app ids (a saved "selected games" view). `null` or an empty list
 * removes the restriction.
 */
function setIdFilter(ids) {
  state.idFilter = ids && ids.length ? [...ids] : null;
  state.idSet = state.idFilter ? new Set(state.idFilter) : null;
}
/** Saves the current filters (without the search text) and the id restriction. */
function persistFilters() {
  const { q, ...rest } = state.filters;
  LS.set('filters', rest);
  LS.set('idFilter', state.idFilter);
}

/* ---------- API ---------- */
async function api(path, { method = 'GET', body } = {}) {
  const res = await fetch(path, {
    method,
    headers: { 'X-SAM-Token': state.token, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    cache: 'no-store',
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const err = new Error(data?.error || `Error ${res.status}`);
    err.code = data?.code || 'http';
    err.status = res.status;
    throw err;
  }
  return data;
}
const imgUrl = (id, kind = 'header') => `/img/${id}?kind=${kind}&t=${encodeURIComponent(state.token)}`;
const avatarUrl = (steamId) => `/avatar/${steamId}?t=${encodeURIComponent(state.token)}`;
const achIconUrl = (appId, h) => (h ? `https://cdn.steamstatic.com/steamcommunity/public/images/apps/${appId}/${h}` : '');

/* ---------- Theme ---------- */
const themeMedia = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;
/** Applies the theme to the page. "auto" follows the operating system's light/dark setting. */
function applyTheme() {
  const eff = state.theme === 'auto' ? (themeMedia?.matches ? 'light' : 'dark') : state.theme;
  document.documentElement.dataset.theme = eff;
}
themeMedia?.addEventListener?.('change', () => state.theme === 'auto' && applyTheme());
/** Theme button: dark -> light -> auto -> dark. */
function cycleTheme() {
  state.theme = { dark: 'light', light: 'auto', auto: 'dark' }[state.theme] || 'dark';
  LS.set('theme', state.theme);
  applyTheme();
  renderTopbar();
}

/* ==========================================================================
   Derived values, filtering, sorting
   ========================================================================== */
/**
 * Adds the computed fields every view relies on to a library entry: `hasAch`, `progKnown`, `pct`, `left`,
 * `complete`, `samLeft` (still unlockable with SAM = left - in-game only - blocked by DLC), `status` (unknown
 * | none | open | complete) and `typeKey`. Called whenever an entry's data changes.
 */
function derive(g) {
  const known = !!g.known;
  const total = g.total ?? 0;
  g.protTotal = g.protTotal ?? 0;
  g.protLocked = g.protLocked ?? 0;
  g.dlcLocked = g.dlcLocked ?? 0;
  g.isNew = !!g.isNew;
  g.newAch = g.newAch || 0;
  g.hasAch = known && total > 0;
  g.progKnown = g.hasAch && g.unlocked != null;
  g.pct = g.progKnown ? g.unlocked / total : null;
  g.left = g.progKnown ? total - g.unlocked : null;
  g.complete = g.progKnown && g.unlocked === total;
  g.samLeft = g.progKnown ? Math.max(0, g.left - g.protLocked - g.dlcLocked) : null;
  g.status = !known ? 'unknown' : total === 0 ? 'none' : !g.progKnown ? 'unknown' : g.complete ? 'complete' : 'open';
  g.typeKey = ['normal', 'demo', 'mod', 'junk'].includes(g.type) ? g.type : 'junk';
  g.nameLower = (g.name || '').toLowerCase();
  return g;
}

/** Merges a fresh progress result (from a scan or from opening a game) into the library entry and re-derives it. */
function applyProgress(r) {
  const g = state.byId.get(r.id);
  if (!g) return;
  Object.assign(g, { known: true, total: r.total, unlocked: r.unlocked, protTotal: r.protTotal, protLocked: r.protLocked, dlcLocked: r.dlcLocked ?? 0, ts: r.ts, src: r.src });
  derive(g);
}

/** Installs a library answer from the host: derives every game and builds the id lookup. */
function setLibrary(lib) {
  state.library = lib;
  state.games = lib.games.map(derive);
  state.byId = new Map(state.games.map((g) => [g.id, g]));
}

/**
 * Does game `g` pass the filters `f`? Used for the list, the sidebar counters and "how many would match"
 * previews.
 */
function matches(g, f) {
  if (!f.types[g.typeKey]) return false;
  if (state.idSet && !state.idSet.has(g.id)) return false;
  const q = f.q.trim().toLowerCase();
  if (q && !g.nameLower.includes(q) && !String(g.id).includes(q)) return false;
  if (f.status !== 'all' && g.status !== f.status) return false;
  if (f.hideComplete && f.status !== 'complete' && g.status === 'complete') return false;
  if (f.hideNone && f.status !== 'none' && g.status === 'none') return false;
  if (f.ingame === 'with' && !(g.protTotal > 0)) return false;
  if (f.ingame === 'open' && !(g.protLocked > 0)) return false;
  if (f.ingame === 'dlc' && !(g.dlcLocked > 0)) return false;
  if (f.ingame === 'without' && (!g.hasAch || g.protTotal > 0 || g.dlcLocked > 0)) return false;
  if (f.news === 'any' && !(g.isNew || g.newAch > 0)) return false;
  if (f.news === 'newgame' && !g.isNew) return false;
  if (f.news === 'newach' && !(g.newAch > 0)) return false;
  if (f.pctMin > 0 || f.pctMax < 100) {
    if (g.pct == null) return false;
    const p = g.pct * 100;
    if (p < f.pctMin || p > f.pctMax) return false;
  }
  if (f.minAch > 0 && !(g.hasAch && g.total >= f.minAch)) return false;
  if (f.installed === 'yes' && !g.installed) return false;
  if (f.installed === 'no' && g.installed) return false;
  if (f.played !== 'all') {
    const lp = g.lastPlayed;
    if (f.played === 'never') { if (lp) return false; }
    else {
      const days = { '30d': 30, '180d': 180, '365d': 365 }[f.played];
      if (!lp || Date.now() / 1000 - lp > days * 86400) return false;
    }
  }
  return true;
}

const SORTS = {
  name: { label: 'Name', dir: 'asc', cmp: (a, b) => a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }) },
  progress: { label: 'Progress', dir: 'desc', val: (g) => g.pct },
  left: { label: 'Open achievements', dir: 'desc', val: (g) => g.left },
  samLeft: { label: 'Still unlockable via SAM', dir: 'desc', val: (g) => g.samLeft },
  ingame: { label: 'Open in-game achievements', dir: 'desc', val: (g) => (g.progKnown ? g.protLocked : null) },
  dlc: { label: 'Blocked by missing DLC', dir: 'desc', val: (g) => (g.progKnown ? g.dlcLocked : null) },
  total: { label: 'Number of achievements', dir: 'desc', val: (g) => (g.hasAch ? g.total : null) },
  played: { label: 'Last played', dir: 'desc', val: (g) => g.lastPlayed ?? null },
  id: { label: 'App ID', dir: 'asc', val: (g) => g.id },
};

/**
 * Sorts in place by the chosen key. Games without a value for the key always go last; ties fall back to the
 * name.
 */
function sortGames(list) {
  const s = SORTS[state.sort.key] || SORTS.name;
  const dir = state.sort.dir === 'asc' ? 1 : -1;
  const byName = (a, b) => a.name.localeCompare(b.name, 'en', { sensitivity: 'base' });
  return list.sort((a, b) => {
    if (s.cmp) return s.cmp(a, b) * dir;
    const va = s.val(a), vb = s.val(b);
    if (va == null && vb == null) return byName(a, b);
    if (va == null) return 1;
    if (vb == null) return -1;
    return va === vb ? byName(a, b) : (va - vb) * dir;
  });
}

const visibleGames = () => sortGames(state.games.filter((g) => matches(g, state.filters)));

/* ---------- Human-readable filter labels (used for chips and collapsed section summaries) ---------- */
const STATUS_LABEL = { open: 'Open', complete: '100% reached', none: 'No achievements', unknown: 'Not checked yet' };
const INGAME_LABEL = { with: 'Has in-game achievements', open: 'Still open in game', dlc: 'Needs a DLC I don’t own', without: 'Fully possible via SAM' };
const NEWS_LABEL = { any: 'Anything new', newgame: 'New games', newach: 'New achievements' };
const PLAYED_LABEL = { '30d': 'Last 30 days', '180d': 'Last 6 months', '365d': 'Last year', never: 'Never played' };
const TYPE_LABEL = { normal: 'Games', demo: 'Demos', mod: 'Mods', junk: 'Other' };

/**
 * Short text describing what is set in a filter section (shown when the section is collapsed and in the
 * chips), or null if the section is at its default.
 */
function sectionInfo(id) {
  const f = state.filters;
  switch (id) {
    case 'status': return f.status === 'all' ? null : STATUS_LABEL[f.status];
    case 'sam': return f.ingame === 'all' ? null : INGAME_LABEL[f.ingame];
    case 'news': return f.news === 'all' ? null : NEWS_LABEL[f.news];
    case 'progress': return f.pctMin > 0 || f.pctMax < 100 ? `${f.pctMin}–${f.pctMax}%` : null;
    case 'minach': return f.minAch > 0 ? `at least ${f.minAch}` : null;
    case 'types': return JSON.stringify(f.types) === JSON.stringify(defaultTypes()) ? null : (Object.keys(f.types).filter((k) => f.types[k]).map((k) => TYPE_LABEL[k]).join(', ') || 'None');
    case 'install': return f.installed === 'all' ? null : (f.installed === 'yes' ? 'Installed' : 'Not installed');
    case 'played': return f.played === 'all' ? null : PLAYED_LABEL[f.played];
    case 'friends': return state.friends.settings.enabled ? 'On' : null;
    default: return null;
  }
}

/** Number of active filters, for the "N filters active" label. */
function activeFilterCount() {
  const f = state.filters;
  let n = ['status', 'sam', 'news', 'progress', 'minach', 'types', 'install', 'played'].filter((id) => sectionInfo(id)).length;
  if (f.hideComplete) n++;
  if (f.hideNone) n++;
  if (state.idFilter) n++;
  if (f.q.trim()) n++;
  return n;
}

/* ==========================================================================
   Quick views (saved filter combinations)
   ========================================================================== */
const BUILTIN_VIEWS = [
  { id: 'all', name: 'All games', filters: {}, sort: { key: 'name', dir: 'asc' }, builtin: true },
  { id: 'todo', name: 'Still to do', filters: { hideComplete: true, hideNone: true }, sort: { key: 'progress', dir: 'desc' }, builtin: true },
  { id: 'close', name: 'Almost done', filters: { status: 'open', pctMin: 75, hideNone: true }, sort: { key: 'progress', dir: 'desc' }, builtin: true },
  { id: 'easy', name: 'Easy wins', filters: { status: 'open', ingame: 'without' }, sort: { key: 'left', dir: 'asc' }, builtin: true },
  { id: 'ingame', name: 'Needs in-game play', filters: { ingame: 'open' }, sort: { key: 'ingame', dir: 'desc' }, builtin: true },
  { id: 'dlc', name: 'Blocked by DLC', filters: { ingame: 'dlc' }, sort: { key: 'dlc', dir: 'desc' }, builtin: true },
  { id: 'new', name: 'What’s new', filters: { news: 'any' }, sort: { key: 'name', dir: 'asc' }, builtin: true },
];

const normFilters = (f) => { const { q, ...rest } = f; return rest; };
const sigOf = (f, ids) => JSON.stringify([normFilters(f), ids && ids.length ? [...ids].sort((a, b) => a - b) : null]);
/** A saved view's filters, completed with defaults (older saved views may lack newer filter keys). */
function viewFilters(v) { return { ...defaultFilters(), ...(v.filters || {}), types: { ...defaultTypes(), ...(v.filters?.types || {}) } }; }
const currentSig = () => sigOf(state.filters, state.idFilter);
const allViews = () => [...BUILTIN_VIEWS, ...state.views];
const activeView = () => allViews().find((v) => sigOf(viewFilters(v), v.ids) === currentSig());

/** Activates a quick view: its filters, sorting and optional fixed game list. */
function applyView(v) {
  state.filters = { ...viewFilters(v), q: state.filters.q };
  if (v.sort) { state.sort = { ...v.sort }; LS.set('sort', state.sort); }
  setIdFilter(v.ids || null);
  if (state.select.on) state.select.ids = new Set(state.idFilter || []);
  persistFilters();
  updateLibrary();
}

/** The row of quick-view chips (built-in and saved) plus the "Save current view" button. */
function viewsHtml() {
  const act = activeView();
  const chip = (v) => `<div class="vchip${act && act.id === v.id ? ' on' : ''}${v.builtin ? '' : ' user'}">
      <button class="vname" data-action="view-apply" data-id="${esc(v.id)}" title="${v.ids ? `${v.ids.length} selected games` : 'Quick view'}">${v.ids ? icon('star') : ''}<span>${esc(v.name)}</span>${v.ids ? `<small>${v.ids.length}</small>` : ''}</button>
      ${v.builtin ? '' : `<button class="vx" data-action="view-delete" data-id="${esc(v.id)}" aria-label="Delete view ${esc(v.name)}" title="Delete view">${icon('x')}</button>`}</div>`;
  return `<span class="vlabel">${icon('filter')} Views</span>
    ${BUILTIN_VIEWS.map(chip).join('')}
    ${state.views.length ? '<span class="vsep"></span>' + state.views.map(chip).join('') : ''}
    ${act ? '' : `<span class="vmod" title="The current filters do not match any saved view">Custom</span>`}
    <button class="vchip add${act ? '' : ' hot'}" data-action="view-save">${icon('plus')} Save current view</button>`;
}

/**
 * Asks for a name and stores the current filters (or the hand-picked games) as a quick view. Reusing a name
 * replaces that view; at most 30 are kept.
 */
async function saveViewDialog() {
  const selCount = state.select.ids.size;
  const current = state.idFilter ? state.idFilter.length : 0;
  let name = '', useSel = selCount > 0;
  const r = await modal({
    title: 'Save quick view', icon: 'star',
    body: `<p>Saves the current filters and sorting, so you can come back to this list with one click.</p>
      <input class="field" id="vname" maxlength="40" placeholder="Name, e.g. “Easy wins”" autocomplete="off" spellcheck="false">
      ${selCount ? `<label class="switch" style="margin-top:14px"><input type="checkbox" id="vsel" checked><span class="tr"></span><span>Only the <b>${nf.format(selCount)}</b> games I selected</span></label>`
        : current ? `<p class="hint" style="margin-top:12px">${icon('info')} This view also keeps the ${plural(current, 'game', 'games')} of the current selection.</p>` : ''}
      <p class="hint" style="margin-top:12px">Using an existing name replaces that view.</p>`,
    actions: [{ label: 'Cancel', value: false }, { label: 'Save view', value: true, cls: 'primary' }],
    onMount: (ov) => {
      const save = $('.btn.primary', ov);
      const input = $('#vname', ov);
      save.disabled = true;
      input.addEventListener('input', () => { name = input.value.trim(); save.disabled = name.length === 0; });
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && name) save.click(); });
      $('#vsel', ov)?.addEventListener('change', (e) => (useSel = e.target.checked));
      input.focus();
    },
  });
  if (r !== true || !name) return;

  let filters = normFilters(state.filters), ids = state.idFilter ? [...state.idFilter] : null;
  if (selCount && useSel) { filters = normFilters({ ...defaultFilters(), types: { normal: true, demo: true, mod: true, junk: true } }); ids = [...state.select.ids]; }
  const existing = state.views.find((v) => v.name.toLowerCase() === name.toLowerCase());
  if (!existing && state.views.length >= 30) return toast('You can save up to 30 views.', 'warn');
  const view = { id: existing?.id || `u${Date.now().toString(36)}`, name, filters, sort: { ...state.sort }, ids };
  state.views = existing ? state.views.map((v) => (v === existing ? view : v)) : [...state.views, view];
  LS.set('views', state.views);
  applyView(view);
  toast(existing ? `View “${name}” updated.` : `View “${name}” saved.`, 'ok');
}

/** Deletes a saved view after confirmation. Only the view is removed, never any game data. */
async function deleteView(id) {
  const v = state.views.find((x) => x.id === id);
  if (!v) return;
  if (!(await confirmBox({ title: `Delete view “${v.name}”?`, icon: 'alert', body: '<p>Only the saved view is removed – your games and achievements are not affected.</p>', ok: 'Delete', danger: true }))) return;
  state.views = state.views.filter((x) => x.id !== id);
  LS.set('views', state.views);
  updateLibrary();
}

/* ---------- Selecting games (for "selected games" views) ---------- */
/** Turns "Select games" mode on or off. Entering it pre-selects the games of the active selected-games view. */
function toggleSelectMode() {
  state.select.on = !state.select.on;
  state.select.ids = state.select.on ? new Set(state.idFilter || []) : new Set();
  updateLibrary();
}
/** The bar shown while selecting games (counter and bulk buttons). */
function selbarHtml() {
  if (!state.select.on) return '';
  const n = state.select.ids.size;
  return `<div class="selbar">${icon('checkCircle')}<span><b id="selCount">${nf.format(n)}</b> selected – click games to add or remove them</span>
    <span class="grow"></span>
    <button class="btn sm" data-action="select-all">Select all shown</button>
    <button class="btn sm" data-action="select-clear">Clear</button>
    <button class="btn sm primary" data-action="auto-open" data-preset="selected" ${n ? '' : 'disabled'}>${icon('zap')} Auto-unlock selected</button>
    <button class="btn sm" data-action="view-save" ${n ? '' : 'disabled'}>${icon('star')} Save as view …</button>
    <button class="btn sm ghost" data-action="select-toggle">Done</button></div>`;
}
/** Refreshes the selection counter and the buttons that need a selection, without re-rendering the grid. */
function updateSelBar() {
  const c = $('#selCount');
  if (c) c.textContent = nf.format(state.select.ids.size);
  const save = $('.selbar [data-action="view-save"]');
  if (save) save.disabled = state.select.ids.size === 0;
}

/* ==========================================================================
   Modals, toasts, popovers
   ========================================================================== */
/**
 * Shows a dialog and resolves with the `value` of the clicked action (null if dismissed with Esc or a click
 * outside). `onMount(overlay, close)` can wire up dynamic content.
 */
function modal({ title, icon: ic = 'info', body = '', actions = [], cls = '', onMount }) {
  return new Promise((resolve) => {
    const ov = document.createElement('div');
    ov.className = 'overlay';
    ov.innerHTML = `<div class="modal ${cls}" role="dialog" aria-modal="true"><h3>${icon(ic)}<span>${esc(title)}</span></h3><div class="mbody">${body}</div><div class="macts">${actions.map((a, i) => `<button class="btn ${a.cls || ''}" data-mv="${i}">${esc(a.label)}</button>`).join('')}</div></div>`;
    const close = (v) => { ov.remove(); document.removeEventListener('keydown', onKey, true); resolve(v); };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(null); } };
    ov.addEventListener('click', (e) => {
      if (e.target === ov) return close(null);
      const b = e.target.closest('[data-mv]');
      if (b) close(actions[+b.dataset.mv].value);
    });
    document.addEventListener('keydown', onKey, true);
    $('#overlay-root').appendChild(ov);
    onMount?.(ov, close);
    ($('.btn.primary, .btn.danger', ov) || $('.btn', ov))?.focus();
  });
}
const confirmBox = async (o) => (await modal({
  actions: [{ label: o.cancel || 'Cancel', value: false }, { label: o.ok || 'OK', value: true, cls: o.danger ? 'danger' : 'primary' }],
  ...o,
})) === true;

/** Shows a short message in the corner. kind: ok | error | warn | info. */
function toast(msg, kind = 'info', ms = 4800) {
  const ic = { ok: 'checkCircle', error: 'alert', warn: 'alert', info: 'info' }[kind] || 'info';
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `${icon(ic)}<span>${esc(msg)}</span>`;
  $('#toast-root').appendChild(el);
  setTimeout(() => { el.classList.add('leave'); setTimeout(() => el.remove(), 260); }, ms);
}

/** Closes the open popover menu, if any. */
function closePopover() { state.popover?.remove(); state.popover = null; }
/**
 * Opens a popover menu next to `anchor` (below it if it fits, above otherwise). Only one popover exists at a
 * time.
 */
function openPopover(anchor, html, { width = 360 } = {}) {
  closePopover();
  const pop = document.createElement('div');
  pop.className = 'popover';
  pop.innerHTML = html;
  pop.style.width = `${width}px`;
  $('#overlay-root').appendChild(pop);
  const r = anchor.getBoundingClientRect();
  const left = Math.max(12, Math.min(window.innerWidth - width - 12, r.right - width));
  pop.style.left = `${left}px`;
  // Below the anchor if it fits, otherwise above it (anchors near the bottom of the window, e.g. in the sidebar)
  const h = pop.offsetHeight;
  let top = r.bottom + 8;
  if (top + h > window.innerHeight - 12) top = Math.max(12, Math.min(r.top - h - 8, window.innerHeight - h - 12));
  pop.style.top = `${top}px`;
  pop.dataset.anchor = anchor.dataset.action || '';
  state.popover = pop;
  return pop;
}

/* ---------- Avatars ---------- */
/** First letter of a name, escaped, for avatar placeholders. */
function initials(name) {
  const t = String(name || '?').trim();
  return esc((t[0] || '?').toUpperCase());
}
/**
 * An avatar: the picture if there is one (falling back to a coloured initial if it fails to load), otherwise a
 * coloured initial. The colour is derived from `seed`.
 */
function avatarHtml(name, seed, { lg = false, img = null } = {}) {
  const hue = hash(seed || name) % 360;
  if (img) return `<img class="avatar${lg ? ' lg' : ''}" src="${esc(img)}" alt="" onerror="this.outerHTML='<span class=&quot;avatar${lg ? ' lg' : ''}&quot; style=&quot;background:hsl(${hue} 50% 42%)&quot;>${initials(name)}</span>'">`;
  return `<span class="avatar${lg ? ' lg' : ''}" style="background:hsl(${hue} 50% 42%)">${initials(name)}</span>`;
}
const accountAvatar = (a, lg) => avatarHtml(a.personaName || a.accountName, a.steamId, { lg, img: a.hasAvatar ? avatarUrl(a.steamId) : null });

/* ==========================================================================
   Top bar
   ========================================================================== */
const activeAccount = () => state.status?.accounts?.find((a) => a.active) || null;

/**
 * The top bar: back button, search box, progress/reload/theme buttons and the account button. Which parts show
 * depends on the route.
 */
function renderTopbar() {
  const inGame = state.route.name === 'game';
  const acct = activeAccount();
  const themeIcon = { dark: 'moon', light: 'sun', auto: 'monitor' }[state.theme];
  const themeLabel = { dark: 'Dark', light: 'Light', auto: 'System' }[state.theme];

  $('#topbar').innerHTML = `
    ${inGame ? `<button class="btn icon" data-action="back" title="Back to library (Esc)" aria-label="Back">${icon('arrowLeft')}</button>` : ''}
    <div class="brand"><div class="logo">${icon('trophy')}</div><span class="txt">Achievement Manager<small>for Steam</small></span></div>
    ${state.ready && !inGame ? `<div class="search">${icon('search')}<input id="q" type="search" placeholder="Search games or app ID …" autocomplete="off" spellcheck="false" value="${esc(state.filters.q)}"><kbd>/</kbd></div>` : ''}
    <div class="spacer"></div>
    <div class="top-actions">
      ${state.ready && !inGame ? `
        <button class="btn" data-action="scan-menu" title="Check the progress of all games through Steam">${icon('scan')}<span class="hide-s">Check progress</span></button>
        <button class="btn icon" data-action="reload-library" title="Reload the library from Steam" aria-label="Reload">${icon('refresh', state.libraryLoading ? 'spin' : '')}</button>` : ''}
      <button class="btn icon" data-action="theme" title="Theme: ${themeLabel}" aria-label="Change theme">${icon(themeIcon)}</button>
      ${state.status?.steam?.installed ? `
        <button class="acct-btn${acct ? '' : ' off'}" data-action="account-menu" title="Choose Steam account">
          ${acct ? accountAvatar(acct) : `<span class="avatar ph">${icon('user')}</span>`}
          <span class="who"><b>${esc(acct ? acct.personaName || acct.accountName : 'Not signed in')}</b><span>${acct ? 'Active account' : 'Steam offline'}</span></span>
          ${icon('chevronDown')}
        </button>` : ''}
    </div>`;
}

/* ---------- Account menu ---------- */
/** Popover listing the Steam accounts saved on this computer. */
function accountMenu(anchor) {
  const st = state.status;
  const accounts = st?.accounts || [];
  const gameRunning = st?.steam?.runningAppId > 0;
  const rows = accounts.map((a) => `
    <button class="acct-row${a.active ? ' active' : ''}" ${a.active ? 'disabled' : `data-action="switch-account" data-id="${esc(a.steamId)}"`}>
      ${accountAvatar(a, true)}
      <span class="grow"><b>${esc(a.personaName || a.accountName)}</b><small>${esc(a.accountName)}${a.rememberPassword ? '' : ' · password required'}</small></span>
      ${a.active ? '<span class="pill green">Active</span>' : `<span class="pill">${icon('swap')} Switch</span>`}
    </button>`).join('');
  openPopover(anchor, `
    <h4>Steam accounts on this computer</h4>
    ${rows || '<p class="note">No saved accounts found.</p>'}
    <div class="sep"></div>
    <p class="note">${icon('info')} Achievements are always unlocked for the <b>active Steam account</b>. Switching shuts Steam down and restarts it with the chosen account.${gameRunning ? `<br><br><b style="color:var(--amber)">Note:</b> A game is currently running in Steam – please quit it first.` : ''}</p>`);
}

/**
 * Switches the active Steam account after confirmation. Steam is restarted by the host; the UI shows a
 * progress dialog meanwhile.
 */
async function switchAccount(steamId) {
  closePopover();
  const target = state.status?.accounts?.find((a) => a.steamId === steamId);
  if (!target) return;
  const running = state.status?.steam?.running;
  const ok = await confirmBox({
    title: `Switch to “${target.personaName || target.accountName}”?`,
    icon: 'swap',
    cls: 'warn',
    body: running
      ? `<p>Steam will be <b>shut down</b> and restarted with this account. This takes about half a minute.</p><ul><li>Running downloads and chats are interrupted.</li><li>${target.rememberPassword ? 'Sign-in happens automatically.' : 'No password is saved for this account – you will have to enter it in Steam.'}</li><li>Unsaved changes here will be lost.</li></ul>`
      : `<p>Steam will be started with this account.</p>`,
    ok: 'Switch account',
  });
  if (!ok) return;
  if (state.game?.pending?.size) state.game.pending.clear();
  try {
    await api('/api/accounts/switch', { method: 'POST', body: { steamId } });
  } catch (e) {
    return toast(e.message, 'error', 7000);
  }
  state.game = null;
  location.hash = '#/';
  showSwitching();
}

/* ---------- Switch progress ---------- */
const SWITCH_STEPS = [['stopping', 'Shut down Steam'], ['configuring', 'Prepare account'], ['starting', 'Start Steam'], ['waiting', 'Wait for sign-in'], ['done', 'Done']];

/** Progress dialog for an account switch. It polls the host once a second. */
function showSwitching() {
  if (state.switching) return;
  const ov = document.createElement('div');
  ov.className = 'overlay';
  ov.style.zIndex = 90;
  // The dialog is built ONCE and only its contents are updated, and only when something actually changed.
  // (Rebuilding it on every poll replayed the dialog's entrance animation each second, so it seemed to
  // disappear and reappear.)
  ov.innerHTML = `<div class="modal"><h3></h3><div class="mbody"><p class="sw-msg"></p><ul class="steps"></ul></div>
    <div class="macts" style="display:none"><button class="btn primary" data-action="switch-close">Close</button></div></div>`;
  $('#overlay-root').appendChild(ov);
  state.switching = ov;
  const modalEl = $('.modal', ov), head = $('h3', ov), msg = $('.sw-msg', ov), steps = $('.steps', ov), acts = $('.macts', ov);
  const seen = {};
  const set = (key, el, html) => { if (seen[key] !== html) { seen[key] = html; el.innerHTML = html; } };
  (async function poll() {
    while (state.switching === ov) {
      let s = null;
      try { s = await api('/api/accounts/switch'); } catch { /* host briefly unavailable - keep trying */ }
      if (s) {
        const idx = SWITCH_STEPS.findIndex(([k]) => k === s.state);
        const err = s.state === 'error';
        modalEl.classList.toggle('danger', err);
        set('head', head, `${icon(err ? 'alert' : 'swap')}<span>${err ? 'Switch failed' : `Switching to “${esc(s.targetName || '')}”`}</span>`);
        set('msg', msg, esc(s.message || ''));
        set('steps', steps, err ? '' : SWITCH_STEPS.map(([k, label], i) => `<li class="${i < idx || s.state === 'done' ? 'done' : i === idx ? 'cur' : ''}"><span class="dot">${i < idx || s.state === 'done' ? icon('check') : i + 1}</span>${label}</li>`).join(''));
        acts.style.display = err ? '' : 'none';
        if (s.state === 'done') { await sleep(600); closeSwitching(); await afterSwitch(); return; }
        if (err) return;
      }
      await sleep(1000);
    }
  })();
}
/** Removes the switch progress dialog. */
function closeSwitching() { state.switching?.remove(); state.switching = null; }
/**
 * After a successful switch everything account-specific is thrown away and the app starts over with the new
 * account.
 */
async function afterSwitch() {
  state.library = null; state.games = []; state.byId = new Map();
  state.friends = { settings: { enabled: false, auto: false, selected: [] }, list: null, byId: new Map(), loading: false };
  state.friendProg = { games: {}, scan: { running: false, total: 0, done: 0, current: 0, failed: 0, skipped: 0, error: null, polling: false } };
  await init();
}

/* ==========================================================================
   Connection views
   ========================================================================== */
const steamReady = () => !!state.status?.steam?.loggedIn;

/** Reads Steam's status (installed, running, signed in) and the saved accounts. */
async function refreshStatus() {
  try { state.status = await api('/api/status'); return true; }
  catch (e) { state.statusError = e; return false; }
}

/** A full-page loading message. */
function viewLoading(text, sub = '') {
  $('#view').innerHTML = `<div class="state"><div class="loader"></div><h2>${esc(text)}</h2>${sub ? `<p>${esc(sub)}</p>` : ''}</div>`;
}

let statusTimer = null;
/**
 * Shown when Steam is missing, not running or nobody is signed in. Polls the status and continues by itself as
 * soon as Steam is ready.
 */
function viewConnect() {
  const s = state.status?.steam;
  const accounts = state.status?.accounts || [];
  let ico = 'power', title, text, actions = '';
  if (!s?.installed) { ico = 'alert'; title = 'Steam was not found'; text = 'No Steam installation is registered on this computer. Please install Steam and start this app again.'; }
  else if (!s.running) {
    title = 'Steam is not running';
    text = 'The Achievement Manager works with the running Steam client. Start Steam – optionally directly with a specific account.';
    actions = `<button class="btn primary" data-action="start-steam">${icon('power')} Start Steam</button>`;
  } else { title = 'Waiting for the sign-in in Steam …'; text = 'Steam is running, but no account is signed in yet. As soon as you have signed in, this page continues automatically.'; ico = 'user'; }

  const list = accounts.length && s?.installed
    ? `<div class="acct-list"><h4 style="padding:8px 10px 2px;font-size:11px;text-transform:uppercase;letter-spacing:.8px;color:var(--faint)">${s.running ? 'Choose account' : 'Start with this account'}</h4>${accounts.map((a) => `
        <button class="acct-row" data-action="switch-account" data-id="${esc(a.steamId)}">${accountAvatar(a, true)}<span class="grow"><b>${esc(a.personaName || a.accountName)}</b><small>${esc(a.accountName)}${a.rememberPassword ? '' : ' · password required'}</small></span><span class="pill accent">${icon('power')} Start</span></button>`).join('')}</div>` : '';

  $('#view').innerHTML = `<div class="state"><div class="ico warn">${icon(ico)}</div><h2>${esc(title)}</h2><p>${esc(text)}</p><div class="actions">${actions}</div>${list}</div>`;
  clearInterval(statusTimer);
  statusTimer = setInterval(async () => {
    if (state.switching) return;
    await refreshStatus();
    renderTopbar();
    if (steamReady()) { clearInterval(statusTimer); await init(); }
  }, 2500);
}

/** A full-page error with a "Try again" button. */
function viewError(e) {
  $('#view').innerHTML = `<div class="state"><div class="ico warn">${icon('alert')}</div><h2>Something went wrong</h2><p>${esc(e.message || e)}</p><div class="actions"><button class="btn primary" data-action="retry">${icon('refresh')} Try again</button></div></div>`;
}

/* ==========================================================================
   Startup
   ========================================================================== */
/** (Re)starts the app: checks Steam, loads the friends settings and the library. */
async function init() {
  clearInterval(statusTimer);
  state.ready = false;
  renderTopbar();
  viewLoading('Connecting to Steam …');
  await refreshStatus();
  if (state.statusError && !state.status) return viewError(state.statusError);
  if (!steamReady()) { renderTopbar(); return viewConnect(); }

  try { await loadFriendSettings(); } catch { /* optional */ }
  await loadLibrary(false);
}

/**
 * Loads the library and shows it. `refresh` asks Steam for the game list again (Reload button); otherwise the
 * cached list is shown at once and refreshed in the background if it is older than an hour.
 */
async function loadLibrary(refresh) {
  state.libraryLoading = true;
  state.ready = false;
  renderTopbar();
  if (!state.library) viewLoading(refresh ? 'Loading the library from Steam …' : 'Loading the library …', 'The first start can take a moment.');
  try {
    setLibrary(await api(`/api/library${refresh ? '?refresh=true' : ''}`));
    state.ready = true;
    state.libraryLoading = false;
    renderTopbar();
    route();
    resumeAuto();   // a run may still be going if the page was reloaded
    loadFriendProgress();   // what "Load friends' progress" found earlier (reads a local file, asks Steam nothing)
    // Show the cached library immediately, then look for new games in the background.
    if (!refresh && Date.now() / 1000 - (state.library.savedAt || 0) > 3600) silentRefresh();
  } catch (e) {
    state.libraryLoading = false;
    await refreshStatus();
    renderTopbar();
    if (['steam-not-running', 'steam-not-logged-in', 'steam-missing'].includes(e.code) || !steamReady()) return viewConnect();
    viewError(e);
  }
}

/** Reads the library again without any loading screen. Returns the games that became "new" meanwhile. */
async function quietReload(refresh = false) {
  try {
    const before = new Set(state.games.filter((g) => g.isNew).map((g) => g.id));
    setLibrary(await api(`/api/library${refresh ? '?refresh=true' : ''}`));
    if (state.route.name === 'library' && $('#grid')) { updateLibrary(); }
    return state.games.filter((g) => g.isNew && !before.has(g.id));
  } catch { return []; }
}

let refreshing = false;
/** Refreshes the library in the background and mentions games that are new. */
async function silentRefresh() {
  if (refreshing) return;
  refreshing = true;
  const added = await quietReload(true);
  refreshing = false;
  if (added.length) toast(`${plural(added.length, 'new game', 'new games')} in your library.`, 'info', 7000);
}

/* ==========================================================================
   Routing
   ========================================================================== */
/** Shows the page for the current URL hash: `#/game/<id>` = game detail, anything else = library. */
function route() {
  if (!state.ready) return;
  const m = /^#\/game\/(\d+)/.exec(location.hash);
  if (m) {
    state.route = { name: 'game', id: +m[1] };
    renderTopbar();
    openGame(+m[1]);
  } else {
    if (state.game) leaveGame();
    state.route = { name: 'library' };
    renderTopbar();
    renderLibrary();
  }
}

/* ==========================================================================
   Library
   ========================================================================== */
const isCollapsed = (id) => !!state.collapsed[id];
const sec = (id, title, body) => `<section class="fsec${isCollapsed(id) ? ' closed' : ''}" data-sec="${id}">
    <button class="fhead" data-action="toggle-sec" data-sec="${id}" aria-expanded="${!isCollapsed(id)}"><span class="fdot"></span><span class="ft">${title}</span><span class="fsum" data-sum="${id}"></span>${icon('chevronDown')}</button>
    <div class="fbody">${body}</div></section>`;

/** The filter sidebar. Only the structure - values and counters are filled in by `syncSidebar`. */
function sidebarHtml() {
  const seg = (name, opts) => `<div class="seg" data-seg="${name}">${opts.map(([v, label, ic]) => `<button data-action="seg" data-seg="${name}" data-value="${v}">${ic ? icon(ic) : ''}<span>${label}</span><span class="n" data-count="${name}:${v}"></span></button>`).join('')}</div>`;
  const chip = (key, label) => `<button class="chip" data-action="type" data-type="${key}">${label}</button>`;
  return `
    <div class="sbar"><span id="factive"></span><span class="sbtns"><button class="linkbtn" data-action="sec-collapse">Collapse all</button><button class="linkbtn" data-action="sec-expand">Expand all</button></span></div>
    <div class="fgroup toggles">
      <label class="switch hero"><input type="checkbox" data-filter="hideComplete"><span class="tr"></span><span><b>${icon('trophy')} Hide 100% games</b><small>Only games with open achievements</small></span></label>
      <label class="switch sub"><input type="checkbox" data-filter="hideNone"><span class="tr"></span><span><b>Hide games without achievements</b><small>Games not checked yet stay visible</small></span></label>
    </div>
    ${sec('status', 'Completion', seg('status', [['all', 'All games', 'library'], ['open', 'Open', 'minus'], ['complete', '100% reached', 'trophy'], ['none', 'No achievements', 'x'], ['unknown', 'Not checked yet', 'info']]))}
    ${sec('sam', 'Unlocking with SAM', seg('ingame', [['all', 'All', 'library'], ['with', 'Has in-game achievements', 'gamepad'], ['open', 'Still open in game', 'lock'], ['dlc', 'Needs a DLC I don’t own', 'package'], ['without', 'Fully possible via SAM', 'checkCircle']]) + `<p class="hint" style="margin:8px 4px 0">Games whose data names their DLC are checked automatically; for the others, DLC details appear once a game was opened or checked.</p>`)}
    ${sec('news', 'What’s new', seg('news', [['all', 'Everything', 'library'], ['any', 'Anything new', 'sparkles'], ['newgame', 'New games', 'star'], ['newach', 'New achievements', 'sparkles']]) + `<button class="btn sm ghost" data-action="mark-seen" id="markSeen" style="margin-top:8px">${icon('check')} Mark all as seen</button>`)}
    ${sec('progress', 'Progress', `<div class="range"><div class="vals"><span id="pctLabel"></span></div><input type="range" min="0" max="100" step="5" data-filter="pctMin" aria-label="Minimum progress"><input type="range" min="0" max="100" step="5" data-filter="pctMax" aria-label="Maximum progress"></div>`)}
    ${sec('minach', 'Minimum achievements', `<input class="field" id="minAch" type="number" min="0" step="1" data-filter="minAch" placeholder="e.g. 20" aria-label="Minimum achievements">`)}
    ${sec('types', 'Game type', `<div class="hbox">${chip('normal', 'Games')}${chip('demo', 'Demos')}${chip('mod', 'Mods')}${chip('junk', 'Other')}</div>`)}
    ${sec('install', 'Installation', `<div class="hbox">${['all:All', 'yes:Installed', 'no:Not installed'].map((s) => { const [v, l] = s.split(':'); return `<button class="chip" data-action="seg" data-seg="installed" data-value="${v}">${l}</button>`; }).join('')}</div>`)}
    ${sec('played', 'Last played', `<select class="field" id="played" data-filter="played" aria-label="Last played"><option value="all">Any time</option><option value="30d">Last 30 days</option><option value="180d">Last 6 months</option><option value="365d">Last year</option><option value="never">Never played</option></select>`)}
    ${sec('friends', 'Friends comparison', '<div id="friendbox"></div>')}
    <button class="btn sm" data-action="reset-filters" style="width:100%;margin-top:6px">${icon('refresh')} Reset all filters</button>`;
}

/** Builds the library page skeleton (sidebar, tiles, job bars, toolbar, grid) and fills it. */
function renderLibrary() {
  $('#view').innerHTML = `
    <div class="layout">
      <aside class="sidebar" id="sidebar">${sidebarHtml()}</aside>
      <main class="main">
        <div class="views" id="views"></div>
        <section class="tiles" id="tiles"></section>
        <div id="scanbar"></div>
        <div id="fscanbar"></div>
        <div id="autobar"></div>
        <section class="toolbar">
          <button class="btn sm only-narrow" data-action="toggle-sidebar">${icon('filter')} Filters</button>
          <div class="count" id="count"></div>
          <button class="btn sm primary" data-action="auto-open" title="Unlock all achievements automatically, game after game">${icon('zap')} Auto-unlock</button>
          <button class="btn sm" data-action="select-toggle" id="selBtn" title="Select games to save them as a view">${icon('checkCircle')} Select games</button>
          <div class="grp"><label for="sortKey" class="hide-s">Sort by</label>
            <select class="field" id="sortKey" style="width:auto;height:34px">${Object.entries(SORTS).map(([k, s]) => `<option value="${k}">${s.label}</option>`).join('')}</select>
            <button class="btn icon sm" data-action="sort-dir" id="sortDir" title="Reverse order" style="width:34px;padding:0"></button></div>
          <div class="grp hide-s" id="sizerGrp"><input class="sizer" type="range" min="190" max="380" step="10" id="sizer" title="Card size" aria-label="Card size"></div>
          <div class="viewtoggle"><button data-action="view" data-view="grid" title="Cards">${icon('grid')}</button><button data-action="view" data-view="list" title="List">${icon('list')}</button></div>
        </section>
        <div id="selbar"></div>
        <div class="chips" id="chips"></div>
        <section class="grid" id="grid"></section>
      </main>
    </div>`;
  $('#sortKey').value = state.sort.key;
  $('#sizer').value = state.size;
  document.documentElement.style.setProperty('--card-min', `${state.size}px`);
  renderScanbar();
  renderFriendScanbar();
  updateLibrary();
  renderFriendBox();
}

let rafPending = false;
/** Asks for a repaint of the library. Calls within one animation frame are merged into one repaint. */
function updateLibrary() {
  if (rafPending) return;
  rafPending = true;
  requestAnimationFrame(() => { rafPending = false; if (state.route.name === 'library' && $('#grid')) paintLibrary(); });
}

/**
 * Repaints the library: overview tiles (with the friends' rows), view chips, toolbar, filter chips and the
 * grid of cards.
 */
function paintLibrary() {
  const f = state.filters;
  const list = visibleGames();

  // Tiles: over all games of the selected types
  const pool = state.games.filter((g) => f.types[g.typeKey]);
  const withAch = pool.filter((g) => g.hasAch);
  const complete = pool.filter((g) => g.complete);
  const known = pool.filter((g) => g.progKnown);
  const sumU = known.reduce((s, g) => s + g.unlocked, 0), sumT = known.reduce((s, g) => s + g.total, 0);
  const protOpen = known.reduce((s, g) => s + g.protLocked, 0);
  const dlcOpen = known.reduce((s, g) => s + g.dlcLocked, 0);
  const unknown = pool.filter((g) => g.status === 'unknown').length;
  const pc = (a, b) => (b ? Math.round((a / b) * 100) : 0);
  const fs = friendTileStats(withAch);
  $('#tiles').innerHTML = `
    <div class="tile accent"><div class="l">${icon('library')} Games with achievements</div><div class="v">${nf.format(withAch.length)}<small>of ${nf.format(pool.length)} games</small></div>
      ${unknown ? `<div class="l" style="margin-top:8px">${icon('info')} ${nf.format(unknown)} not checked yet</div>` : ''}${friendTileRows('games', fs)}${friendTileNote(fs)}</div>
    <div class="tile gold"><div class="l">${icon('trophy')} 100% completed</div><div class="v">${nf.format(complete.length)}<small>of ${nf.format(withAch.length)} games (${pc(complete.length, withAch.length)}%)</small></div>${friendTileRows('done', fs)}</div>
    <div class="tile"><div class="l">${icon('checkCircle')} Achievements unlocked</div><div class="v">${nf.format(sumU)}<small>of ${nf.format(sumT)} (${pc(sumU, sumT)}%)</small></div><div class="mini"><i style="width:${sumT ? (sumU / sumT) * 100 : 0}%"></i></div>${friendTileRows('ach', fs)}</div>
    <div class="tile amber"><div class="l">${icon('lock')} Not unlockable via SAM</div><div class="v">${nf.format(protOpen + dlcOpen)}<small>${nf.format(protOpen)} in game · ${nf.format(dlcOpen)} missing DLC</small></div></div>`;

  $('#views').innerHTML = viewsHtml();
  $('#selbar').innerHTML = selbarHtml();
  $('#selBtn')?.classList.toggle('on', state.select.on);
  renderAutobar();
  const sz = $('#sizerGrp'); if (sz) sz.style.display = state.view === 'list' ? 'none' : '';
  $('#count').innerHTML = `<b>${nf.format(list.length)}</b> of ${nf.format(pool.length)} games`;
  $('#sortDir').innerHTML = icon('sort');
  $('#sortDir').title = state.sort.dir === 'asc' ? 'Ascending – click to reverse' : 'Descending – click to reverse';
  $('#sortDir').style.transform = state.sort.dir === 'asc' ? 'scaleY(-1)' : '';
  $('#sortKey').value = state.sort.key;
  $$('.viewtoggle button').forEach((b) => b.classList.toggle('on', b.dataset.view === state.view));

  // Active filters as chips (always visible, even when the sidebar sections are collapsed)
  const chips = [];
  if (f.q.trim()) chips.push(['Search: ' + f.q.trim(), () => { f.q = ''; const q = $('#q'); if (q) q.value = ''; }]);
  if (state.idFilter) chips.push([`Only ${plural(state.idFilter.length, 'selected game', 'selected games')}`, () => setIdFilter(null)]);
  if (f.status !== 'all') chips.push([STATUS_LABEL[f.status], () => (f.status = 'all')]);
  if (f.hideComplete) chips.push(['100% hidden', () => (f.hideComplete = false)]);
  if (f.hideNone) chips.push(['No-achievement games hidden', () => (f.hideNone = false)]);
  if (f.ingame !== 'all') chips.push([INGAME_LABEL[f.ingame], () => (f.ingame = 'all')]);
  if (f.news !== 'all') chips.push([NEWS_LABEL[f.news], () => (f.news = 'all')]);
  if (f.pctMin > 0 || f.pctMax < 100) chips.push([`Progress ${f.pctMin}–${f.pctMax}%`, () => { f.pctMin = 0; f.pctMax = 100; }]);
  if (f.minAch > 0) chips.push([`at least ${f.minAch} achievements`, () => (f.minAch = 0)]);
  if (sectionInfo('types')) chips.push([`Types: ${sectionInfo('types')}`, () => (f.types = defaultTypes())]);
  if (f.installed !== 'all') chips.push([f.installed === 'yes' ? 'Installed' : 'Not installed', () => (f.installed = 'all')]);
  if (f.played !== 'all') chips.push([`Played: ${PLAYED_LABEL[f.played]}`, () => (f.played = 'all')]);
  filterClearers = chips.map((c) => c[1]);
  $('#chips').innerHTML = chips.length
    ? chips.map((c, i) => `<span class="fchip">${esc(c[0])}<button data-action="clear-chip" data-i="${i}" aria-label="Remove filter">${icon('x')}</button></span>`).join('') + `<button class="reset-link" data-action="reset-filters">Reset all</button>`
    : '';

  syncSidebar();

  const grid = $('#grid');
  grid.className = `grid${state.view === 'list' ? ' list' : ''}${state.select.on ? ' selecting' : ''}`;
  grid.innerHTML = list.length ? list.map(cardHtml).join('') : `<div class="noresult"><div class="ico">${icon('search')}</div><h3>No games found</h3><p>No game matches the current filters.</p><p style="margin-top:14px"><button class="btn" data-action="reset-filters">Reset filters</button></p></div>`;
}
let filterClearers = [];

/**
 * Brings the sidebar in line with the state: counters, selected options, slider values, collapsed sections and
 * their summaries.
 */
function syncSidebar() {
  const f = state.filters;
  const countFor = (name, v) => state.games.filter((g) => matches(g, { ...f, [name]: v, ...(name === 'status' ? { hideComplete: false, hideNone: false } : {}) })).length;
  $$('#sidebar [data-count]').forEach((el) => {
    const [name, v] = el.dataset.count.split(':');
    el.textContent = nf.format(countFor(name, v));
  });
  $$('#sidebar [data-action="seg"]').forEach((b) => b.classList.toggle('on', f[b.dataset.seg] === b.dataset.value));
  $$('#sidebar [data-action="type"]').forEach((b) => b.classList.toggle('on', !!f.types[b.dataset.type]));
  const hide = $('#sidebar [data-filter="hideComplete"]');
  if (hide) { hide.checked = f.hideComplete; hide.disabled = f.status === 'complete'; }
  const hideN = $('#sidebar [data-filter="hideNone"]');
  if (hideN) { hideN.checked = f.hideNone; hideN.disabled = f.status === 'none'; }
  const set = (sel, val) => { const el = $(sel); if (el && document.activeElement !== el) el.value = val; };
  set('[data-filter="pctMin"]', f.pctMin); set('[data-filter="pctMax"]', f.pctMax);
  set('[data-filter="minAch"]', f.minAch || ''); set('[data-filter="played"]', f.played);
  const lbl = $('#pctLabel'); if (lbl) lbl.textContent = `${f.pctMin} – ${f.pctMax}%`;

  // Collapsible sections: a collapsed section still tells what is set inside it
  $$('#sidebar .fsec').forEach((el) => {
    const id = el.dataset.sec, info = sectionInfo(id);
    el.classList.toggle('act', !!info);
    el.classList.toggle('closed', isCollapsed(id));
    const sum = $('.fsum', el); if (sum) { sum.textContent = info || ''; sum.title = info || ''; }
    $('.fhead', el)?.setAttribute('aria-expanded', String(!isCollapsed(id)));
  });
  const n = activeFilterCount();
  const fa = $('#factive'); if (fa) fa.innerHTML = n ? `<b>${n}</b> ${n === 1 ? 'filter' : 'filters'} active` : 'No filters active';
  const ms = $('#markSeen'); if (ms) ms.hidden = !state.games.some((g) => g.isNew || g.newAch > 0);
}

/** One game as a card (or list row): cover, badges, progress bar, friends' bars and footer pills. */
function cardHtml(g) {
  const hue = hash(g.id) % 360;
  const badges = [];
  if (g.complete) badges.push(`<span class="badge gold" title="All ${g.total} achievements unlocked">${icon('trophy')}</span>`);
  if (g.dlcLocked > 0) badges.push(`<span class="badge violet" title="${plural(g.dlcLocked, 'achievement is', 'achievements are')} locked because the matching DLC is not owned">${icon('package')}<span>${g.dlcLocked}</span></span>`);
  if (g.protTotal > 0) {
    if (g.protLocked > 0) badges.push(`<span class="badge amber" title="${plural(g.protLocked, 'achievement', 'achievements')} can only be unlocked in the game itself">${icon('gamepad')}<span>${g.protLocked}</span></span>`);
    else badges.push(`<span class="badge amber dim" title="All ${g.protTotal} in-game achievements are already unlocked">${icon('gamepad')}${icon('check')}</span>`);
  }
  if (g.newAch > 0) badges.push(`<span class="badge cyan" title="${plural(g.newAch, 'new achievement', 'new achievements')} since you last looked">${icon('sparkles')}<span>+${g.newAch}</span></span>`);
  if (g.isNew) badges.push(`<span class="badge green" title="New in your library since you last looked">${icon('star')}<span>New</span></span>`);

  const status = g.complete ? 'complete' : g.status;
  let meta;
  if (g.status === 'none') meta = `<span>No achievements</span>`;
  else if (g.status === 'unknown') meta = `<span>${g.hasAch ? `${nf.format(g.total)} achievements · progress unknown` : 'Not checked yet'}</span>`;
  else meta = `<span>${nf.format(g.unlocked)} / ${nf.format(g.total)}</span><span class="pct">${pctText(g.pct)}</span>`;
  const foot = [];
  if (g.installed) foot.push(`<span class="pill">${icon('hdd')} Installed</span>`);
  if (g.progKnown && !g.complete && g.samLeft === 0) {
    if (g.protLocked > 0) foot.push(`<span class="pill amber">${icon('gamepad')} Rest in game only</span>`);
    if (g.dlcLocked > 0) foot.push(`<span class="pill violet">${icon('package')} Rest needs DLC</span>`);
  }
  if (g.typeKey !== 'normal') foot.push(`<span class="pill">${{ demo: 'Demo', mod: 'Mod', junk: 'Other' }[g.typeKey]}</span>`);
  const bar = `<div class="bar"><i style="width:${g.pct == null ? 0 : g.pct * 100}%"></i></div>`;
  const fbars = friendBarsHtml(g);
  const body = state.view === 'list'
    ? `<div class="name" title="${esc(g.name)}">${esc(g.name)}</div><div class="lbar"><div class="meta">${meta}</div>${bar}${fbars}</div><div class="badges">${badges.join('')}</div>`
    : `<div class="name" title="${esc(g.name)}">${esc(g.name)}</div><div class="meta">${meta}</div>${bar}${fbars}<div class="foot">${foot.join('')}</div>`;
  const sel = state.select.on && state.select.ids.has(g.id);
  return `<article class="card ${status}${g.status === 'none' ? ' dim' : ''}${sel ? ' sel' : ''}" data-action="open-game" data-id="${g.id}" tabindex="0" role="button" aria-label="${esc(state.select.on ? 'Select ' : 'Open ')}${esc(g.name)}">
    <div class="thumb" style="--ph1:hsl(${hue} 42% 34%);--ph2:hsl(${(hue + 50) % 360} 48% 22%)"><img class="gimg" loading="lazy" decoding="async" src="${imgUrl(g.id)}" alt=""><span class="ph">${initials(g.name)}</span><div class="shade"></div>${state.select.on ? `<span class="selbox">${icon('check')}</span>` : ''}${state.view === 'list' ? '' : `<div class="badges">${badges.join('')}</div>`}</div>
    <div class="body">${body}</div></article>`;
}

/* ---------- Friends' progress on the cards ---------- */
const FRIEND_BARS_MAX = 3;
const friendsActive = () => state.friends.settings.enabled && state.friends.settings.selected.length > 0;

/** One slim bar per selected friend (only friends that have data for this game; Steam hides the rest). */
function friendBarsHtml(g) {
  if (!friendsActive() || !g.hasAch) return '';
  const data = state.friendProg.games[g.id];
  if (!data) return '';
  const rows = state.friends.settings.selected.map((id) => ({ id, r: data[id] })).filter((x) => x.r && x.r.s === 'ok' && x.r.t > 0);
  if (!rows.length) return noFriendDataHtml(data);
  const shown = rows.slice(0, FRIEND_BARS_MAX), more = rows.slice(FRIEND_BARS_MAX);
  const items = shown.map(({ id, r }) => {
    const name = friendName(id), pct = r.u / r.t;
    return `<div class="fbar${r.u >= r.t ? ' full' : ''}" title="${esc(`${name}: ${r.u} of ${r.t} achievements (${pctText(pct)})`)}">${avatarHtml(name, id, { img: avatarUrl(id) })}<span class="fname">${esc(name)}</span><div class="bar"><i style="width:${pct * 100}%;--fh:${hash(id) % 360}"></i></div><span class="fnum">${nf.format(r.u)}<small>/${nf.format(r.t)}</small></span></div>`;
  }).join('');
  const rest = more.length ? `<div class="fmore" title="${esc(more.map(({ id, r }) => `${friendName(id)}: ${r.u} / ${r.t}`).join('\n'))}">+${more.length} more</div>` : '';
  return `<div class="fbars">${items}${rest}</div>`;
}

/**
 * Friends' numbers for the overview tiles. Only games where Steam handed out the friend's progress can be compared, so
 * everything is counted over those "shared" games; `checked` says for how many of your games a result exists at all.
 */
function friendTileStats(withAch) {
  if (!friendsActive()) return null;
  const rows = state.friends.settings.selected.map((id) => {
    const s = { id, checked: 0, shared: 0, done: 0, u: 0, t: 0, myDone: 0, myU: 0, myT: 0 };
    for (const g of withAch) {
      const r = state.friendProg.games[g.id]?.[id];
      if (!r) continue;
      s.checked++;
      if (r.s !== 'ok' || !(r.t > 0)) continue;
      s.shared++; s.u += r.u; s.t += r.t;
      if (r.u >= r.t) s.done++;
      if (g.complete) s.myDone++;
      if (g.progKnown) { s.myU += g.unlocked; s.myT += g.total; }
    }
    return s;
  });
  return { rows, total: withAch.length, checked: rows.length ? Math.min(...rows.map((r) => r.checked)) : 0 };
}

/** One slim row per friend under a tile (kind: games | done | ach). */
function friendTileRows(kind, fs) {
  if (!fs) return '';
  const rows = fs.rows.filter((r) => r.checked > 0);
  if (!rows.length) return '';
  const pc = (a, b) => (b ? Math.round((a / b) * 100) : 0);
  const one = (r) => {
    const name = friendName(r.id);
    const part = r.checked < fs.total ? ` Based on the ${nf.format(r.checked)} of ${nf.format(fs.total)} games checked so far.` : '';
    let val = '–', tip, bar = '';
    if (kind === 'games') {
      val = `${nf.format(r.shared)}<small> shared</small>`;
      tip = `Steam shows ${name}’s progress for ${nf.format(r.shared)} of your ${nf.format(fs.total)} games with achievements – only those can be compared.${part}`;
    } else if (kind === 'done') {
      if (r.shared) val = `${nf.format(r.done)}<small> of ${nf.format(r.shared)}</small>`;
      tip = `${name} completed ${nf.format(r.done)} of the ${nf.format(r.shared)} games you both have (${pc(r.done, r.shared)}%). You completed ${nf.format(r.myDone)} of them (${pc(r.myDone, r.shared)}%).${part}`;
    } else {
      if (r.shared) {
        val = `${nf.format(r.u)}<small> of ${nf.format(r.t)}</small>`;
        bar = `<div class="mini"><i style="width:${r.t ? (r.u / r.t) * 100 : 0}%;--fh:${hash(r.id) % 360}"></i></div>`;
      }
      tip = `${name} unlocked ${nf.format(r.u)} of ${nf.format(r.t)} achievements (${pc(r.u, r.t)}%) in the games you both have. You: ${nf.format(r.myU)} of ${nf.format(r.myT)} (${pc(r.myU, r.myT)}%) in the same games.${part}`;
    }
    return `<div class="tfr" title="${esc(tip)}">${avatarHtml(name, r.id, { img: avatarUrl(r.id) })}<span class="fname">${esc(name)}</span><span class="fval">${val}</span></div>${bar}`;
  };
  const shown = rows.slice(0, FRIEND_BARS_MAX), more = rows.slice(FRIEND_BARS_MAX);
  return `<div class="tfs">${shown.map(one).join('')}${more.length ? `<div class="tfmore">+${more.length} more</div>` : ''}</div>`;
}

/** Tells the user how much of the library the friend numbers are based on, and offers to load the rest. */
function friendTileNote(fs) {
  if (!fs || fs.checked >= fs.total) return '';
  const busy = state.friendProg.scan.running;
  const txt = busy ? 'Loading friends’ progress …' : fs.checked ? `Friends: checked ${nf.format(fs.checked)} of ${nf.format(fs.total)} games` : 'Friends’ progress is not loaded yet';
  return `<div class="tfnote"><span>${txt}</span>${busy ? '' : '<button class="linkbtn" data-action="fprog-menu">Load</button>'}</div>`;
}

/** The game was checked for every selected friend, but Steam handed out nothing: say so instead of leaving the card silent. */
function noFriendDataHtml(data) {
  const sel = state.friends.settings.selected;
  const entries = sel.map((id) => data[id]);
  if (!entries.length || entries.some((r) => !r)) return '';   // not checked for everyone yet - nothing to explain
  const many = sel.length > 1;
  const who = many ? 'your selected friends' : 'your friend';
  const failed = entries.some((r) => r.s === 'timeout' || r.s === 'request');
  const label = failed ? (many ? 'Friends: check failed' : 'Friend: check failed') : (many ? 'Friends: no data' : 'Friend: no data');
  const tip = failed
    ? `Steam did not answer in time for ${who} in this game. Use “Load friends’ progress” again to retry.`
    : `Steam returned no achievement data for ${who} in this game. They probably do not own it or never played it – or they keep their game details private. Steam does not say which.`;
  return `<div class="fnone" title="${esc(tip)}">${icon('users')}<span>${label}</span></div>`;
}

/**
 * Reads the stored friends' progress from the host (a local file - this asks Steam nothing) and picks up a
 * scan that may still be running.
 */
async function loadFriendProgress() {
  if (!friendsActive()) { state.friendProg.games = {}; updateLibrary(); renderFriendBox(); return; }
  try {
    const d = await api('/api/friends/progress');
    state.friendProg.games = d.games || {};
    if (Object.keys(state.friendProg.games).length && !state.friends.list) ensureFriendList().then(() => updateLibrary()).catch(() => {});   // names only, for the bars
    resumeFriendScan();
  } catch { /* optional */ }
  updateLibrary();
  renderFriendBox();
}

const friendScanCandidates = (mode) => {
  const f = state.filters;
  const pool = mode === 'view' ? visibleGames() : state.games.filter((g) => f.types[g.typeKey]);
  return pool.filter((g) => g.hasAch);
};
const friendScanMinutes = (n) => Math.max(1, Math.round((n * (1.2 + 0.3 * Math.max(1, state.friends.settings.selected.length))) / 60));   // rough: ~1.5 s per game for one friend (worker start + own stats + friend)

/** Popover that offers to load the friends' progress for the current view or for all games. */
function fprogMenu(anchor) {
  const view = friendScanCandidates('view').length, all = friendScanCandidates('all').length;
  return openPopover(anchor, `
    <h4>Load friends’ progress</h4>
    <p class="note">Asks Steam how far <b>${state.friends.settings.selected.length === 1 ? 'your selected friend is' : `your ${state.friends.settings.selected.length} selected friends are`}</b> in each game, so every card can show a bar per friend. Steam only hands out what they made visible to friends.</p>
    <button class="acct-row" data-action="fprog-start" data-mode="view" ${view ? '' : 'disabled'}><span class="avatar ph">${icon('filter')}</span><span class="grow"><b>Games in the current view</b><small>${plural(view, 'game', 'games')} · about ${friendScanMinutes(view)} min</small></span></button>
    <button class="acct-row" data-action="fprog-start" data-mode="all" ${all ? '' : 'disabled'}><span class="avatar ph">${icon('library')}</span><span class="grow"><b>All games with achievements</b><small>${plural(all, 'game', 'games')} · about ${friendScanMinutes(all)} min</small></span></button>
    <div class="sep"></div><p class="note">${icon('info')} Games loaded within the last 6 hours are skipped.</p>`, { width: 380 });
}

/**
 * Asks for confirmation, then starts loading the selected friends' progress for the chosen games and begins
 * polling it.
 */
async function startFriendProgress(mode) {
  closePopover();
  const games = friendScanCandidates(mode);
  if (!games.length || !friendsActive()) return;
  const ok = await confirmBox({
    title: 'Load friends’ progress from Steam?', icon: 'users', cls: 'warn',
    body: `<p>SAM will ask Steam about <b>${plural(state.friends.settings.selected.length, 'friend', 'friends')}</b> in <b>${plural(games.length, 'game', 'games')}</b>, one game after another (about ${friendScanMinutes(games.length)} minutes).</p>
      <ul><li>Only the friends you selected are queried.</li>
      <li>For each game you appear as <b>“playing” for a few seconds</b> – to your friends as well.</li>
      <li>Only the counts (for example 12 of 45) are stored on this computer – never achievement names or dates. Deselecting a friend or deactivating the comparison deletes them.</li>
      <li>Nothing is changed, it is only read. You can cancel at any time.</li></ul>`,
    ok: 'Start loading',
  });
  if (!ok) return;
  let s;
  try { s = await api('/api/friends/scan/start', { method: 'POST', body: { ids: games.map((g) => g.id), confirm: true } }); }
  catch (e) { return toast(e.message, 'error', 7000); }
  if (!s.running) return toast(`Everything is up to date – ${plural(s.skipped, 'game was', 'games were')} loaded within the last 6 hours.`, 'info', 6000);
  Object.assign(state.friendProg.scan, { running: true, total: s.total, done: 0, current: 0, failed: 0, skipped: s.skipped, error: null });
  if (!state.friends.list) ensureFriendList().catch(() => {});
  renderFriendScanbar();
  pollFriendScan();
}

/** After a page reload: continues showing a friends' progress job that is still running on the host. */
async function resumeFriendScan() {
  if (state.friendProg.scan.polling) return;
  try {
    const s = await api('/api/friends/scan/status?cursor=0');
    if (!s.running) return;
    Object.assign(state.friendProg.scan, { running: true, total: s.total, done: s.done, current: s.current, failed: s.failed, skipped: s.skipped, error: null });
    renderFriendScanbar();
    pollFriendScan();
  } catch { /* none running */ }
}

/**
 * Polls the running job once per 0.7 s, stores every result that arrives (so cards fill in while it runs) and
 * finishes with a toast.
 */
async function pollFriendScan() {
  const sc = state.friendProg.scan;
  if (sc.polling) return;
  sc.polling = true;
  let cursor = 0;
  while (true) {
    let s;
    try { s = await api(`/api/friends/scan/status?cursor=${cursor}`); } catch { await sleep(1500); continue; }
    cursor = s.cursor;
    s.results.forEach((r) => { state.friendProg.games[r.id] = r.friends; });
    Object.assign(sc, { running: s.running, total: s.total, done: s.done, current: s.current, failed: s.failed, skipped: s.skipped, error: s.error });
    renderFriendScanbar();
    if (s.results.length) updateLibrary();
    if (!s.running) break;
    await sleep(700);
  }
  sc.polling = false;
  renderFriendScanbar();
  renderFriendBox();
  updateLibrary();
  if (sc.error) toast(sc.error, 'error', 8000);
  else toast(`Friends’ progress loaded for ${plural(sc.done, 'game', 'games')}.`, 'ok', 6000);
}

/** The progress bar of the friends' progress job (with Cancel). */
function renderFriendScanbar() {
  const el = $('#fscanbar');
  if (!el) return;
  const s = state.friendProg.scan;
  if (!s.running) { el.innerHTML = ''; return; }
  const cur = state.byId.get(s.current);
  el.innerHTML = `<div class="scanbar"><span class="avatar ph">${icon('users', 'spin')}</span><div class="grow"><div class="t"><span>Loading friends’ progress: <b>${esc(cur?.name || '…')}</b></span><span>${nf.format(s.done)} / ${nf.format(s.total)}</span></div><div class="bar"><i style="width:${s.total ? (s.done / s.total) * 100 : 0}%"></i></div></div><button class="btn sm" data-action="fprog-cancel">Cancel</button></div>`;
}

/* ---------- Check progress (deep scan) ---------- */
/** Games a progress check could cover: `missing` = progress unknown, otherwise every game that has achievements. */
function scanCandidates(mode) {
  const f = state.filters;
  return state.games.filter((g) => f.types[g.typeKey] && (mode === 'missing' ? g.status === 'unknown' : g.status !== 'none'));
}
const scanMinutes = (n) => Math.max(1, Math.round((n * 1.2) / 60));
/** Popover for "Check progress": unknown games only, or re-check everything. */
function scanMenu(anchor) {
  const missing = scanCandidates('missing').length, all = scanCandidates('all').length;
  return openPopover(anchor, `
    <h4>Check progress through Steam</h4>
    <p class="note">Progress is read instantly from the local Steam cache. Here you can query missing or outdated values directly from Steam. This also finds new achievements and missing DLC.</p>
    <button class="acct-row" data-action="scan-start" data-mode="missing" ${missing ? '' : 'disabled'}><span class="avatar ph">${icon('scan')}</span><span class="grow"><b>Unknown games only</b><small>${plural(missing, 'game', 'games')} · about ${scanMinutes(missing)} min</small></span></button>
    <button class="acct-row" data-action="scan-start" data-mode="all" ${all ? '' : 'disabled'}><span class="avatar ph">${icon('refresh')}</span><span class="grow"><b>Re-check all games</b><small>${plural(all, 'game', 'games')} · about ${scanMinutes(all)} min</small></span></button>
    <div class="sep"></div><p class="note">${icon('info')} The currently selected game types apply.</p>`);
}
/** Asks for confirmation, then starts the progress check through the host and begins polling it. */
async function startScan(mode) {
  closePopover();
  const games = scanCandidates(mode);
  if (!games.length) return;
  const ok = await confirmBox({
    title: 'Query progress from Steam?', icon: 'scan', cls: 'warn',
    body: `<p>SAM will query <b>${plural(games.length, 'game', 'games')}</b> from Steam one after another (about ${scanMinutes(games.length)} minutes).</p><ul><li>For each game you appear as <b>“playing” for 1–2 seconds</b> – to your friends as well.</li><li>Nothing is changed, it is only read.</li><li>You can cancel at any time.</li></ul>`,
    ok: 'Start check',
  });
  if (!ok) return;
  try { await api('/api/scan/start', { method: 'POST', body: { ids: games.map((g) => g.id) } }); }
  catch (e) { return toast(e.message, 'error', 7000); }
  state.scan = { running: true, total: games.length, done: 0, current: 0, failed: 0, error: null, polling: false };
  renderScanbar();
  pollScan();
}
/**
 * Polls the progress check, applies every result as it arrives and, when done, recomputes the "new
 * achievements" markers.
 */
async function pollScan() {
  if (state.scan.polling) return;
  state.scan.polling = true;
  let cursor = 0;
  while (true) {
    let s;
    try { s = await api(`/api/scan/status?cursor=${cursor}`); } catch { await sleep(1500); continue; }
    cursor = s.cursor;
    s.results.forEach(applyProgress);
    Object.assign(state.scan, { running: s.running, total: s.total, done: s.done, current: s.current, failed: s.failed, error: s.error });
    renderScanbar();
    if (s.results.length) updateLibrary();
    if (!s.running) break;
    await sleep(700);
  }
  state.scan.polling = false;
  renderScanbar();
  await quietReload(false);   // recompute "new achievements" markers from the fresh data
  const upd = state.games.filter((g) => g.newAch > 0).length;
  if (state.scan.error) toast(state.scan.error, 'error', 8000);
  else toast(`Check finished – ${plural(state.scan.done, 'game', 'games')} updated${upd ? `, ${plural(upd, 'game has', 'games have')} new achievements` : ''}.`, 'ok', 7000);
  updateLibrary();
}
/** The progress bar of the progress check (with Cancel). */
function renderScanbar() {
  const el = $('#scanbar');
  if (!el) return;
  const s = state.scan;
  if (!s.running) { el.innerHTML = ''; return; }
  const cur = state.byId.get(s.current);
  el.innerHTML = `<div class="scanbar"><span class="avatar ph">${icon('scan', 'spin')}</span><div class="grow"><div class="t"><span>Checking: <b>${esc(cur?.name || '…')}</b></span><span>${nf.format(s.done)} / ${nf.format(s.total)}</span></div><div class="bar"><i style="width:${s.total ? (s.done / s.total) * 100 : 0}%"></i></div></div><button class="btn sm" data-action="scan-cancel">Cancel</button></div>`;
}

/* ==========================================================================
   Auto-unlock (batch over several games)
   ========================================================================== */
const AUTO_SECONDS_PER_GAME = 4;

/** Games an auto run could work on right now. n = how many achievements it would change (null = unknown yet). */
function autoCandidates(mode, includeUnknown) {
  const f = state.filters, rows = [];
  for (const g of state.games) {
    if (!f.types[g.typeKey]) continue;
    if (mode === 'unlock') {
      if (g.progKnown && g.samLeft > 0) rows.push({ g, n: g.samLeft });
      else if (includeUnknown && g.status === 'unknown') rows.push({ g, n: null, note: 'progress unknown – checked live' });
    } else {
      const lockable = g.progKnown ? g.unlocked - (g.protTotal - g.protLocked) : 0;
      if (lockable > 0) rows.push({ g, n: lockable });
    }
  }
  return rows.sort((a, b) => a.g.name.localeCompare(b.g.name, 'en', { sensitivity: 'base' }));
}

/**
 * The Auto-unlock dialog: mode (unlock/lock), which games, and the confirmations. Locking needs an extra typed
 * word. Starts the run when confirmed.
 */
async function autoDialog(preset) {
  if (state.auto.running) return toast('An auto run is already in progress.', 'warn');
  const acct = activeAccount();
  const acctName = acct?.personaName || acct?.accountName || 'the active account';
  let mode = 'unlock', includeUnknown = false, query = '', ack = false, typed = '', lastPreset = preset || (state.select.ids.size ? 'selected' : 'all');
  const checked = new Set();

  const applyPreset = (p) => {
    lastPreset = p;
    checked.clear();
    const inView = new Set(visibleGames().map((g) => g.id));
    for (const r of autoCandidates(mode, includeUnknown)) {
      if (p === 'all' || (p === 'view' && inView.has(r.g.id)) || (p === 'selected' && state.select.ids.has(r.g.id))) checked.add(r.g.id);
    }
  };
  applyPreset(lastPreset);

  const r = await modal({
    title: 'Auto-unlock achievements', icon: 'zap', cls: 'wide2',
    body: `<p id="amodehint"></p>
      <div class="mode2" role="group" aria-label="Mode"><button data-mode="unlock"></button><button data-mode="lock"></button></div>
      <div class="apresets"><span>Choose games:</span>
        <button class="chip" data-preset="all">All with achievements left</button>
        <button class="chip" data-preset="view">Games in the current view</button>
        <button class="chip" data-preset="selected">Selected games (${state.select.ids.size})</button>
        <button class="chip" data-preset="none">None</button></div>
      <div class="search" style="margin:12px 0 8px;max-width:none">${icon('search')}<input id="asearch" type="search" placeholder="Filter the list …" autocomplete="off"></div>
      <div class="flist alist2" id="alist2"></div>
      <label class="switch" id="aunkwrap" style="margin-top:12px"><input type="checkbox" id="aunk"><span class="tr"></span><span>Also include games whose progress is not known yet (they are checked live)</span></label>
      <p class="note asum" id="asum"></p>
      <label class="switch"><input type="checkbox" id="aack"><span class="tr"></span><span id="aacktext"></span></label>
      <div id="atypewrap" hidden style="margin-top:12px"><p style="margin-bottom:6px">To confirm, type <b>LOCK</b>:</p><input class="field" id="atype" autocomplete="off" spellcheck="false"></div>`,
    actions: [{ label: 'Cancel', value: false }, { label: 'Start', value: true, cls: 'primary' }],
    onMount: (ov) => {
      const go = $('[data-mv="1"]', ov), list = $('#alist2', ov), sum = $('#asum', ov);
      const paint = () => {
        const rows = autoCandidates(mode, includeUnknown);
        const q = query.trim().toLowerCase();
        const shown = rows.filter((x) => !q || x.g.nameLower.includes(q));
        list.innerHTML = shown.length ? shown.map((x) => `<label class="frow"><input type="checkbox" value="${x.g.id}" ${checked.has(x.g.id) ? 'checked' : ''}><span class="grow"><b>${esc(x.g.name)}</b>${x.note ? `<small>${esc(x.note)}</small>` : ''}</span><span class="pill ${mode === 'lock' ? 'amber' : 'accent'}">${x.n == null ? '?' : (mode === 'unlock' ? '+' : '−') + nf.format(x.n)}</span><span class="tick">${icon('check')}</span></label>`).join('')
          : `<p class="note" style="padding:20px;text-align:center">${rows.length ? 'No game found.' : mode === 'unlock' ? 'No game has achievements left that SAM can unlock.' : 'No game has achievements that SAM could lock.'}</p>`;
        const sel = rows.filter((x) => checked.has(x.g.id));
        const n = sel.reduce((s, x) => s + (x.n || 0), 0), unknown = sel.filter((x) => x.n == null).length;
        sum.innerHTML = `<b>${plural(sel.length, 'game', 'games')}</b> selected · ${mode === 'unlock' ? 'unlocks' : 'locks'} about <b>${nf.format(n)}</b> achievements${unknown ? ` plus ${unknown} not counted yet` : ''} · roughly <b>${Math.max(1, Math.round((sel.length * AUTO_SECONDS_PER_GAME) / 60))} min</b>`;
        $$('.mode2 button', ov).forEach((b) => b.classList.toggle('on', b.dataset.mode === mode));
        $('.mode2 [data-mode="unlock"]', ov).innerHTML = `${icon('zap')} Unlock achievements`;
        $('.mode2 [data-mode="lock"]', ov).innerHTML = `${icon('lock')} Lock achievements`;
        $('#amodehint', ov).innerHTML = mode === 'unlock'
          ? `Unlocks every achievement SAM can change, game after game. Achievements that only the game itself can set, and those that need a DLC you don’t own, are skipped. Unlock dates will be today’s date. <span class="hint">Some games link achievements to statistics (for example “travel one mile”) – Steam may then raise those statistics as well.</span>`
          : `<b style="color:var(--red)">Locks achievements again</b> (their unlock dates are lost). Only games you tick below are touched. Achievements set by the game itself are skipped. <span class="hint">Statistics that are linked to an achievement can drop back to 0.</span>`;
        $('#aunkwrap', ov).hidden = mode !== 'unlock';
        $('#atypewrap', ov).hidden = mode !== 'lock';
        $('#aacktext', ov).innerHTML = mode === 'unlock'
          ? `I understand that SAM will unlock these achievements for <b>${esc(acctName)}</b>. I can undo the run afterwards.`
          : `I understand that these achievements will be locked for <b>${esc(acctName)}</b> and that unlock dates are lost.`;
        $$('.apresets [data-preset]', ov).forEach((b) => b.classList.toggle('on', b.dataset.preset === lastPreset));
        go.textContent = mode === 'unlock' ? `Unlock in ${plural(sel.length, 'game', 'games')}` : `Lock in ${plural(sel.length, 'game', 'games')}`;
        go.classList.toggle('danger', mode === 'lock');
        go.classList.toggle('primary', mode === 'unlock');
        go.disabled = !(sel.length && ack && (mode !== 'lock' || typed.trim().toUpperCase() === 'LOCK'));
      };
      ov.addEventListener('click', (e) => {
        const m = e.target.closest('[data-mode]');
        if (m && m.dataset.mode !== mode) { mode = m.dataset.mode; applyPreset(mode === 'unlock' ? 'all' : 'none'); paint(); return; }
        const p = e.target.closest('[data-preset]');
        if (p) { applyPreset(p.dataset.preset); paint(); }
      });
      list.addEventListener('change', (e) => {
        const cb = e.target; if (cb.type !== 'checkbox') return;
        if (cb.checked) checked.add(+cb.value); else checked.delete(+cb.value);
        lastPreset = null; paint();
      });
      $('#asearch', ov).addEventListener('input', (e) => { query = e.target.value; paint(); });
      $('#aunk', ov).addEventListener('change', (e) => { includeUnknown = e.target.checked; applyPreset(lastPreset || 'all'); paint(); });
      $('#aack', ov).addEventListener('change', (e) => { ack = e.target.checked; paint(); });
      $('#atype', ov).addEventListener('input', (e) => { typed = e.target.value; paint(); });
      paint();
    },
  });
  if (r !== true || !checked.size) return;
  startAuto(mode, [...checked]);
}

/** Starts an Auto-unlock run - or its undo - and begins polling. */
async function startAuto(mode, ids, undo = false) {
  try {
    const s = await api(undo ? '/api/auto/undo' : '/api/auto/start', {
      method: 'POST',
      body: undo ? undefined : { mode, ids, confirm: true, confirmText: mode === 'lock' ? 'LOCK' : undefined },
    });
    state.auto = { running: true, finished: false, mode: s.mode, total: s.total, done: 0, changed: 0, problems: 0, current: 0, currentName: '', results: [], error: null, polling: false, canUndo: false, showDetails: false, cancelling: false };
  } catch (e) { return toast(e.message, 'error', 8000); }
  renderAutobar();
  pollAuto();
}

/** Polls the run, collects the per-game results and, when finished, reloads the library and reports the outcome. */
async function pollAuto() {
  const a = state.auto;
  if (a.polling) return;
  a.polling = true;
  let cursor = a.results.length;
  while (true) {
    let s;
    try { s = await api(`/api/auto/status?cursor=${cursor}`); } catch { await sleep(1500); continue; }
    cursor = s.cursor;
    a.results.push(...s.results);
    Object.assign(a, { running: s.running, mode: s.mode, total: s.total, done: s.done, changed: s.changed, problems: s.problems, current: s.current, currentName: s.currentName, error: s.error, canUndo: s.canUndo });
    renderAutobar();
    if (!s.running) break;
    await sleep(900);
  }
  a.polling = false;
  a.finished = true;
  a.cancelling = false;
  await quietReload(false);   // progress and markers of every touched game
  renderAutobar();
  const verb = a.mode === 'unlock' ? 'unlocked' : 'locked';
  if (a.error) toast(a.error, 'error', 9000);
  else toast(`Done – ${plural(a.changed, 'achievement', 'achievements')} ${verb} in ${plural(a.results.filter((r) => r.changed > 0).length, 'game', 'games')}.`, 'ok', 8000);
}

/** After a page reload: picks up a run that is still going on the host. */
async function resumeAuto() {
  try {
    const s = await api('/api/auto/status?cursor=0');
    if (s.running && !state.auto.running) {
      state.auto = { running: true, finished: false, mode: s.mode, total: s.total, done: s.done, changed: s.changed, problems: s.problems, current: s.current, currentName: s.currentName, results: [...s.results], error: null, polling: false, canUndo: false, showDetails: false, cancelling: false };
      renderAutobar();
      pollAuto();
    }
  } catch { /* not important */ }
}

/** The Auto-unlock bar: progress while running, afterwards the summary with Undo and per-game details. */
function renderAutobar() {
  const el = $('#autobar');
  if (!el) return;
  const a = state.auto;
  if (!a.running && !a.finished) { el.innerHTML = ''; return; }
  const verb = a.mode === 'unlock' ? 'unlocked' : 'locked';
  if (a.running) {
    el.innerHTML = `<div class="scanbar auto"><span class="avatar ph">${icon('zap', 'spin')}</span><div class="grow"><div class="t"><span>${a.mode === 'unlock' ? 'Unlocking' : 'Locking'}: <b>${esc(a.currentName || state.byId.get(a.current)?.name || '…')}</b></span><span>${nf.format(a.done)} / ${nf.format(a.total)} games · ${nf.format(a.changed)} achievements ${verb}</span></div><div class="bar"><i style="width:${a.total ? (a.done / a.total) * 100 : 0}%"></i></div></div>
      <button class="btn sm" data-action="auto-cancel" ${a.cancelling ? 'disabled' : ''}>${a.cancelling ? 'Stopping …' : 'Stop after this game'}</button></div>`;
    return;
  }
  const touched = a.results.filter((r) => r.changed > 0);
  const issues = a.results.filter((r) => (r.error && r.error !== 'No achievements') || r.failed > 0);
  const skipped = a.results.reduce((s, r) => s + r.skippedProtected + r.skippedDlc, 0);
  const stoppedEarly = a.done < a.total;
  const row = (r) => `<div class="adrow"><b>${esc(r.name || state.byId.get(r.id)?.name || `App ${r.id}`)}</b><span>${[
    r.changed ? `${verb} ${r.changed}` : '',
    r.skippedProtected ? `${r.skippedProtected} in game only` : '',
    r.skippedDlc ? `${r.skippedDlc} missing DLC` : '',
    r.failed ? `${r.failed} failed` : '',
    r.error && r.error !== 'No achievements' ? esc(r.error) : '',
  ].filter(Boolean).join(' · ')}${r.failWhy ? `<span class="why">${esc(r.failWhy)}</span>` : ''}</span></div>`;
  const detailRows = a.results.filter((r) => r.changed || r.failed || r.skippedProtected || r.skippedDlc || (r.error && r.error !== 'No achievements'));
  el.innerHTML = `<div class="autosum${a.error ? ' err' : ''}">
    <div class="ahead">${icon(a.error ? 'alert' : 'checkCircle')}<div class="grow"><b>${a.error ? 'Auto run stopped' : stoppedEarly ? 'Auto run stopped early' : 'Auto run finished'}</b></div>
      <div class="abtns">${a.canUndo ? `<button class="btn sm" data-action="auto-undo">${icon('swap')} Undo this run</button>` : ''}<button class="btn sm ghost" data-action="auto-details">${a.showDetails ? 'Hide details' : 'Details'}</button><button class="btn sm ghost" data-action="auto-dismiss" aria-label="Dismiss">${icon('x')}</button></div></div>
    ${a.error ? `<p class="aerr">${esc(a.error)}</p>` : ''}
    <div class="astats"><span><b>${nf.format(a.changed)}</b> achievements ${verb}</span><span><b>${nf.format(touched.length)}</b> games changed</span>${skipped ? `<span><b>${nf.format(skipped)}</b> skipped (in game only / missing DLC)</span>` : ''}${issues.length ? `<span class="warn"><b>${nf.format(issues.length)}</b> games with problems</span>` : ''}</div>
    ${a.showDetails ? `<div class="adetails">${detailRows.length ? detailRows.map(row).join('') : '<p class="note">Nothing was changed.</p>'}</div>` : ''}</div>`;
}

/** Reverses the last run after confirmation (re-locks exactly what it unlocked, or the other way round). */
async function undoAuto() {
  const a = state.auto;
  const ok = await confirmBox({
    title: 'Undo the last run?', icon: 'swap', cls: 'warn',
    body: `<p>This ${a.mode === 'unlock' ? '<b>locks again</b>' : '<b>unlocks again</b>'} exactly the achievements the last run changed – nothing else.</p>
      <p class="hint" style="margin-top:10px">${icon('info')} Statistics that Steam links to these achievements (for example a distance counter) follow the achievement and may not return to their old values.</p>`,
    ok: 'Undo run',
  });
  if (ok) startAuto(null, null, true);
}

/* ==========================================================================
   Friends comparison (opt-in)
   ========================================================================== */
/** Reads the friends comparison settings (on/off, selected friends) from the host. */
async function loadFriendSettings() {
  state.friends.settings = await api('/api/friends/settings');
}

/** The "Friends comparison" box in the sidebar: activate, choose friends, load progress, deactivate. */
function renderFriendBox() {
  const el = $('#friendbox');
  if (!el) return;
  const s = state.friends.settings;
  if (!s.enabled) {
    el.innerHTML = `<p class="hint">Compare your achievements with friends. It is <b>off</b> – only friends you explicitly choose are queried.</p>
      <button class="btn sm" data-action="friends-activate" style="width:100%">${icon('users')} Activate</button>`;
  } else {
    const checked = Object.keys(state.friendProg.games).length;
    const loaded = Object.values(state.friendProg.games).filter((d) => Object.values(d).some((r) => r.s === 'ok')).length;
    const busy = state.friendProg.scan.running;
    el.innerHTML = `<p class="hint">${s.selected.length ? `${plural(s.selected.length, 'friend', 'friends')} selected. Open a game and switch to the “Friends” tab, or load their progress for the whole library to see a bar per friend on every card.` : 'No friends selected yet.'}</p>
      ${s.selected.length ? `<p class="hint" style="margin-top:6px">${checked ? `Checked ${plural(checked, 'game', 'games')}: ${loaded ? `data found for ${nf.format(loaded)}` : 'no data found'}${loaded < checked ? ` – for the others Steam handed out nothing (not owned, never played or private)` : ''}.` : 'Friends’ progress is not loaded yet.'}</p>` : ''}
      <div style="display:grid;gap:8px">${s.selected.length ? `<button class="btn sm primary" data-action="fprog-menu" ${busy ? 'disabled' : ''}>${icon('users', busy ? 'spin' : '')} ${busy ? 'Loading …' : 'Load friends’ progress'}</button>` : ''}
      <button class="btn sm${s.selected.length ? '' : ' primary'}" data-action="friends-pick">${icon('users')} Choose friends</button>
      <button class="btn sm ghost" data-action="friends-deactivate">Deactivate</button></div>`;
  }
  syncSidebar();
}

/**
 * Switches the friends comparison on after explaining what it does, then lets the user choose friends. Nothing
 * about friends is queried before that.
 */
async function friendsActivate() {
  const ok = await confirmBox({
    title: 'Activate friends comparison', icon: 'users',
    body: `<p>This shows which achievements your friends have unlocked in a game.</p>
      <ul><li><b>Off by default:</b> As long as you do not activate it, nothing about friends is queried.</li>
      <li><b>You decide who takes part:</b> Achievement data is only queried for the friends you choose next – and only when you click “Compare” inside a game or “Load friends’ progress” in the library.</li>
      <li><b>Little is stored:</b> the Steam IDs of your selection and, if you load their progress, only counts per game (for example 12 of 45) – never achievement names or dates. Deselecting a friend or deactivating deletes them.</li>
      <li>Steam only hands out what your friend has made visible to friends.</li></ul>`,
    ok: 'Activate and choose friends',
  });
  if (!ok) return;
  try {
    state.friends.settings = await api('/api/friends/settings', { method: 'POST', body: { enabled: true, auto: false, selected: [] } });
  } catch (e) { return toast(e.message, 'error'); }
  renderFriendBox();
  friendsPick();
}

/** Switches the comparison off and deletes the selection and the stored friend progress (also on the host). */
async function friendsDeactivate() {
  const ok = await confirmBox({ title: 'Deactivate friends comparison?', icon: 'users', body: '<p>Your selection and any loaded friend progress will be deleted. No friend data will be queried anymore.</p>', ok: 'Deactivate', danger: true });
  if (!ok) return;
  api('/api/friends/scan/cancel', { method: 'POST' }).catch(() => {});
  try { state.friends.settings = await api('/api/friends/settings', { method: 'POST', body: { enabled: false, selected: [] } }); }
  catch (e) { return toast(e.message, 'error'); }
  state.friends.list = null;
  state.friendProg.games = {};
  updateLibrary();
  if (state.game) { state.game.friends = { status: 'idle', results: {}, done: 0, diff: 'all' }; if (state.game.tab === 'friends') state.game.tab = 'achievements'; renderGameShell(); }
  renderFriendBox();
  toast('Friends comparison deactivated.', 'ok');
}

const friendName = (id) => state.friends.byId.get(id)?.name || (state.friends.list ? `Unknown (${id.slice(-4)})` : '…');

/** Loads the friends list once (names and ids only) and keeps it in `state.friends`. */
async function ensureFriendList(force = false) {
  if (state.friends.list && !force) return state.friends.list;
  state.friends.loading = true;
  try {
    const d = await api('/api/friends/list');
    state.friends.list = (d.friends || []).map((f) => ({ ...f, name: f.name || `Unknown (${f.steamId.slice(-4)})` }));
    state.friends.byId = new Map(state.friends.list.map((f) => [f.steamId, f]));
    return state.friends.list;
  } finally { state.friends.loading = false; }
}

/**
 * Dialog to choose up to 25 friends. Saving also tells the host, which deletes data of friends that were
 * deselected.
 */
async function friendsPick() {
  const selected = new Set(state.friends.settings.selected);
  const MAX = 25;
  let auto = state.friends.settings.auto;
  const rowsHtml = (list, q) => {
    const items = list.filter((f) => !q || f.name.toLowerCase().includes(q));
    if (!items.length) return `<p class="note" style="padding:20px;text-align:center">${list.length ? 'No friend found.' : 'No friends found in your Steam friends list.'}</p>`;
    return items.map((f) => `<label class="frow"><input type="checkbox" value="${esc(f.steamId)}" ${selected.has(f.steamId) ? 'checked' : ''}>${avatarHtml(f.name, f.steamId, { lg: true })}<span class="grow"><b>${esc(f.name)}</b></span><span class="tick">${icon('check')}</span></label>`).join('');
  };

  const result = modal({
    title: 'Choose friends to compare with', icon: 'users', cls: 'wide',
    body: `<p>Only the friends selected here are queried (at most ${MAX}).</p>
      <div class="search" style="margin:14px 0 8px;max-width:none">${icon('search')}<input id="fsearch" type="search" placeholder="Search friends …" autocomplete="off"></div>
      <div class="flist" id="flist"><div style="padding:36px;text-align:center"><div class="loader"></div>Reading your friends list from Steam …</div></div>
      <label class="switch" style="margin-top:14px"><input type="checkbox" id="fauto"><span class="tr"></span><span>Compare automatically when a game is opened</span></label>
      <p class="note" id="fcount" style="padding:8px 0 0"></p>`,
    actions: [{ label: 'Cancel', value: false }, { label: 'Save selection', value: true, cls: 'primary' }],
    onMount: async (ov) => {
      const list = $('#flist', ov), count = $('#fcount', ov), q = $('#fsearch', ov);
      $('#fauto', ov).checked = auto;
      $('#fauto', ov).addEventListener('change', (e) => (auto = e.target.checked));
      const upd = () => (count.textContent = `${selected.size} of ${MAX} selected`);
      try {
        const friends = await ensureFriendList(true);
        const paint = () => { list.innerHTML = rowsHtml(friends, q.value.trim().toLowerCase()); upd(); };
        paint();
        q.addEventListener('input', paint);
        list.addEventListener('change', (e) => {
          const cb = e.target; if (cb.type !== 'checkbox') return;
          if (cb.checked) { if (selected.size >= MAX) { cb.checked = false; return toast(`At most ${MAX} friends.`, 'warn'); } selected.add(cb.value); }
          else selected.delete(cb.value);
          upd();
        });
        q.focus();
      } catch (e) { list.innerHTML = `<p class="note" style="padding:20px">${esc(e.message)}</p>`; }
    },
  });
  if ((await result) !== true) return;
  try {
    state.friends.settings = await api('/api/friends/settings', { method: 'POST', body: { enabled: true, auto, selected: [...selected] } });
  } catch (e) { return toast(e.message, 'error'); }
  renderFriendBox();
  loadFriendProgress();   // friends that were deselected are gone on the server too
  if (state.game) { state.game.friends = { status: 'idle', results: {}, done: 0, diff: 'all' }; renderGameShell(); }
  toast(selected.size ? `${plural(selected.size, 'friend', 'friends')} selected.` : 'No friends selected.', 'ok');
}

/* ==========================================================================
   Game detail
   ========================================================================== */
const ACH_SORTS = { default: 'Default', name: 'Name', unlock: 'Unlock date', rarity: 'Rarity', status: 'Status', friends: 'Friends' };
const newGameState = (id) => ({
  id, name: state.byId.get(id)?.name || `App ${id}`, loading: true, error: null, data: null, ach: [], stats: [], dlcs: [], summary: null, news: null,
  tab: 'achievements', pending: new Map(), statPending: new Map(), statsEdit: false, saving: false, rarity: null, refused: {},
  f: { q: '', status: 'all', kind: 'all', hidden: 'all', pending: false, sort: 'default', dir: 'asc' },
  friends: { status: 'idle', results: {}, done: 0, diff: 'all' },
});

/** An achievement that needs a DLC the account does not own cannot be unlocked. */
const isDlcBlocked = (a) => !!(a.dlc && a.dlc.owned === false && !a.achieved);

/**
 * Opens the detail view of a game: asks the host to load it (this starts a Steam "game" session) and shows it.
 * Opening counts as having seen the game.
 */
async function openGame(id) {
  if (state.game?.id === id && !state.game.error && state.game.data) { renderGameShell(); return; }
  state.game = newGameState(id);
  renderGameShell();
  try {
    await loadFriendSettings().catch(() => {});   // always use the server's current state
    const data = await api(`/api/game/${id}/open`, { method: 'POST' });
    if (state.game?.id !== id) return;
    setGameData(data);
    state.game.news = data.news && (data.news.isNew || data.news.newAch > 0) ? data.news : null;
    const g = state.byId.get(id);
    if (g) { g.isNew = false; g.newAch = 0; }    // opening a game counts as seeing it
  } catch (e) {
    if (state.game?.id !== id) return;
    state.game.error = e;
  }
  state.game.loading = false;
  renderGameShell();
  if (state.game.data) {
    loadRarity(id);
    if (state.friends.settings.enabled && state.friends.settings.auto && state.friends.settings.selected.length) compareFriends();
  }
}

/**
 * Installs the host's answer for the open game (achievements, stats, DLCs, summary) and updates the library
 * entry with the fresh progress.
 */
function setGameData(data) {
  const G = state.game;
  G.data = data;
  G.name = data.name || G.name;
  G.ach = data.achievements.map((a) => ({ ...a, rarity: G.rarity?.[a.id] ?? null }));
  G.stats = data.stats;
  G.dlcs = data.dlcs || [];
  G.summary = { dlcLocked: 0, ...data.summary };
  applyProgress({ id: G.id, ...G.summary, ts: Date.now() / 1000, src: 'live' });
}

/** Loads the global unlock percentages for the open game (optional, from the public Steam web API). */
async function loadRarity(id) {
  try {
    const r = await api(`/api/game/${id}/rarity`);
    if (state.game?.id !== id) return;
    state.game.rarity = r;
    state.game.ach.forEach((a) => (a.rarity = r[a.id] ?? null));
    if (state.game.tab === 'achievements') updateAchList();
  } catch { /* optional */ }
}

/** Leaves the game view and closes the game session on the host, so Steam stops showing "playing". */
function leaveGame() {
  const G = state.game;
  state.game = null;
  if (G) api(`/api/game/${G.id}/close`, { method: 'POST' }).catch(() => {});
}

const desired = (a) => (state.game.pending.has(a.id) ? state.game.pending.get(a.id) : a.achieved);
const pendingCount = () => state.game.pending.size + state.game.statPending.size;

/** Builds the game page: loading state, error state, or hero + tabs + panel + action bar. */
function renderGameShell() {
  const G = state.game;
  if (!G) return;
  const view = $('#view');
  if (G.loading) {
    view.innerHTML = `<div class="detail"><div class="hero"><div class="in"><div class="cover skel" style="aspect-ratio:460/215"></div><div class="info"><h1>${esc(G.name)}</h1><div class="sub">Connecting to Steam and loading achievements …</div></div><div class="loader" style="margin:0"></div></div></div>
      <div class="alist">${'<div class="skel" style="height:80px"></div>'.repeat(6)}</div></div>`;
    return;
  }
  if (G.error) {
    view.innerHTML = `<div class="state"><div class="ico warn">${icon('alert')}</div><h2>${esc(G.name)}</h2><p>${esc(G.error.message)}</p><div class="actions"><button class="btn" data-action="back">${icon('arrowLeft')} Back</button><button class="btn primary" data-action="game-reload">${icon('refresh')} Load again</button></div></div>`;
    return;
  }

  const fr = state.friends.settings;
  const hasFriends = fr.enabled && fr.selected.length > 0;
  if (G.tab === 'friends' && !hasFriends) G.tab = 'achievements';
  view.innerHTML = `<div class="detail">
    <section class="hero" id="hero"></section>
    <div class="tabs" id="tabs">
      <button data-action="tab" data-tab="achievements">${icon('trophy')} Achievements <span class="n">${G.ach.length}</span></button>
      <button data-action="tab" data-tab="stats">${icon('edit')} Statistics <span class="n">${G.stats.length}</span></button>
      ${hasFriends ? `<button data-action="tab" data-tab="friends">${icon('users')} Friends <span class="n">${fr.selected.length}</span></button>` : ''}
    </div>
    <div id="panel"></div></div>
    <div class="actionbar" id="actionbar"></div>`;
  renderHero();
  renderPanel();
  renderActionBar();
  $$('#tabs button').forEach((b) => b.classList.toggle('on', b.dataset.tab === G.tab));
}

/** The banner at the top of the game page with cover, status pills and the progress ring. */
function renderHero() {
  const G = state.game, s = G.summary, acct = activeAccount(), news = G.news;
  const pct = s.total ? s.unlocked / s.total : 0;
  const C = 2 * Math.PI * 46;
  const protOpen = s.protLocked, dlcOpen = s.dlcLocked || 0, samLeft = Math.max(0, s.total - s.unlocked - protOpen - dlcOpen);
  const hue = hash(G.id) % 360;
  $('#hero').innerHTML = `<div class="bg" style="background-image:url('${imgUrl(G.id, 'hero')}'), url('${imgUrl(G.id, 'header')}')"></div><div class="veil"></div>
    <div class="in"><div class="cover" style="--ph1:hsl(${hue} 42% 34%);--ph2:hsl(${(hue + 50) % 360} 48% 22%)"><img src="${imgUrl(G.id)}" alt="" onerror="this.parentElement.classList.add('noimg')"><span class="ph">${initials(G.name)}</span></div>
      <div class="info"><h1>${esc(G.name)}</h1>
        <div class="sub"><span>App ID ${G.id}</span><span>·</span>${acct ? `<span class="pill">${icon('user')} ${esc(acct.personaName || acct.accountName)}</span>` : ''}
          ${s.unlocked === s.total && s.total ? `<span class="pill gold">${icon('trophy')} 100% completed</span>` : `<span class="pill green">${nf.format(samLeft)} unlockable via SAM</span>`}
          ${protOpen ? `<span class="pill amber">${icon('gamepad')} ${nf.format(protOpen)} in game only</span>` : ''}
          ${dlcOpen ? `<span class="pill violet">${icon('package')} ${nf.format(dlcOpen)} need a missing DLC</span>` : ''}
          ${news?.newAch ? `<span class="pill cyan">${icon('sparkles')} ${plural(news.newAch, 'new achievement', 'new achievements')} since your last visit</span>` : ''}
          ${news?.isNew ? `<span class="pill green">${icon('star')} New in your library</span>` : ''}</div></div>
      <div class="ring${pct >= 1 ? ' full' : ''}"><svg viewBox="0 0 112 112"><defs><linearGradient id="ringgrad" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7c5cff"/><stop offset="1" stop-color="#38bdf8"/></linearGradient></defs>
        <circle class="tr" cx="56" cy="56" r="46"/><circle class="fg" cx="56" cy="56" r="46" stroke-dasharray="${C}" stroke-dashoffset="${C * (1 - pct)}"/></svg>
        <div class="c"><b>${pctText(pct)}</b><small>${nf.format(s.unlocked)} / ${nf.format(s.total)}</small></div></div>
      <button class="btn" data-action="game-reload" title="Reload from Steam">${icon('refresh')}</button></div>`;
}

/** Explains "why not 100%": achievements that need a DLC the account does not own, grouped by DLC. */
function dlcBannerHtml() {
  const G = state.game;
  const blocked = G.ach.filter(isDlcBlocked);
  if (!blocked.length) return '';
  const groups = new Map();
  blocked.forEach((a) => groups.set(a.dlc.label, (groups.get(a.dlc.label) || 0) + 1));
  const pills = [...groups].sort((a, b) => b[1] - a[1]).map(([n, c]) => `<span class="pill violet">${esc(n)} · ${c}</span>`).join(' ');
  return `<div class="dlc-note">${icon('package')}<div><b>${plural(blocked.length, 'achievement needs', 'achievements need')} a DLC this account does not own</b>
    <p>These cannot be unlocked until you own the DLC – that is why this game cannot reach 100% yet.</p><div class="pills">${pills}</div></div></div>`;
}

/** The content of the active tab: achievements (with filters and bulk buttons), statistics, or friends. */
function renderPanel() {
  const G = state.game;
  const p = $('#panel');
  if (!p) return;
  if (G.tab === 'stats') p.innerHTML = statsHtml();
  else if (G.tab === 'friends') { p.innerHTML = friendsTabHtml(); initFriendsTab(); }
  else {
    p.innerHTML = `
      ${dlcBannerHtml()}
      <div class="dtools">
        <div class="search">${icon('search')}<input id="aq" type="search" placeholder="Search achievements …" autocomplete="off" value="${esc(G.f.q)}"></div>
        <div class="grp">
          <select class="field" data-af="status"><option value="all">All</option><option value="locked">Locked</option><option value="unlocked">Unlocked</option></select>
          <select class="field" data-af="kind"><option value="all">All kinds</option><option value="sam">Possible via SAM</option><option value="ingame">In game only</option><option value="dlc">Needs missing DLC</option></select>
          <select class="field" data-af="hidden"><option value="all">Hidden: all</option><option value="hidden">Hidden only</option><option value="visible">Without hidden</option></select>
        </div>
        <div class="grp" style="margin-left:auto"><select class="field" data-af="sort">${Object.entries(ACH_SORTS).filter(([k]) => k !== 'friends' || Object.keys(G.friends.results).length).map(([k, l]) => `<option value="${k}">${l}</option>`).join('')}</select>
          <button class="btn icon sm" data-action="adir" title="Reverse order" style="width:34px;padding:0">${icon('sort')}</button></div>
      </div>
      <div class="bulk">
        <button class="btn sm" data-action="bulk" data-bulk="select">${icon('checkCircle')} Select all unlockable</button>
        <button class="btn sm" data-action="bulk" data-bulk="lock">${icon('lock')} Lock all unlocked</button>
        <button class="btn sm" data-action="bulk" data-bulk="invert">${icon('swap')} Invert selection</button>
        <button class="btn sm ghost" data-action="bulk" data-bulk="clear">${icon('x')} Discard</button>
        <span class="pill" id="acount" style="margin-left:auto"></span>
      </div>
      <div class="alist" id="alist"></div>`;
    $$('[data-af]').forEach((el) => (el.value = G.f[el.dataset.af]));
    updateAchList();
  }
}

/** The achievements that pass the filters of the game page, in the chosen order. */
function visibleAch() {
  const G = state.game, f = G.f, q = f.q.trim().toLowerCase();
  const list = G.ach.filter((a) => {
    if (q && !a.name.toLowerCase().includes(q) && !(a.description || '').toLowerCase().includes(q) && !a.id.toLowerCase().includes(q)) return false;
    if (f.status === 'locked' && a.achieved) return false;
    if (f.status === 'unlocked' && !a.achieved) return false;
    if (f.kind === 'sam' && (a.protected || isDlcBlocked(a))) return false;
    if (f.kind === 'ingame' && !a.protected) return false;
    if (f.kind === 'dlc' && !isDlcBlocked(a)) return false;
    if (f.hidden === 'hidden' && !a.hidden) return false;
    if (f.hidden === 'visible' && a.hidden) return false;
    if (f.pending && !G.pending.has(a.id)) return false;
    return true;
  });
  const dir = f.dir === 'asc' ? 1 : -1;
  const friendCount = (a) => Object.values(G.friends.results).filter((r) => r.ok && r.achieved[a.id] !== undefined).length;
  const idx = new Map(G.ach.map((a, i) => [a.id, i]));
  const keys = {
    default: (a) => idx.get(a.id),
    name: (a) => a.name.toLowerCase(),
    unlock: (a) => (a.unlockTime ?? null),
    rarity: (a) => a.rarity,
    status: (a) => (a.achieved ? 1 : 0),
    friends: friendCount,
  };
  const k = keys[f.sort] || keys.default;
  return list.sort((a, b) => {
    const va = k(a), vb = k(b);
    if (va == null && vb == null) return idx.get(a.id) - idx.get(b.id);
    if (va == null) return 1;
    if (vb == null) return -1;
    const r = typeof va === 'string' ? va.localeCompare(vb, 'en') : va - vb;
    return r === 0 ? idx.get(a.id) - idx.get(b.id) : r * dir;
  });
}

/**
 * One achievement row: icon, texts, badges (hidden, in game only, DLC, refused), rarity, date and the unlock
 * switch - or a lock if it cannot be changed.
 */
function achRowHtml(a) {
  const G = state.game, want = desired(a), changed = G.pending.has(a.id), blocked = isDlcBlocked(a);
  const rar = a.rarity;
  const rarHtml = rar == null ? '' : `<div class="rare${rar < 5 ? ' ultra' : ''}" title="Share of all players who have this achievement">${icon('sparkles')} ${rar}%${rar < 5 ? ' · rare' : ''}</div>`;
  const dateHtml = a.achieved && a.unlockTime ? `<div>${fmtDate(a.unlockTime)}</div>` : a.achieved ? '<div>Unlocked</div>' : '';
  const friendsWho = Object.entries(G.friends.results).filter(([, r]) => r.ok && r.achieved[a.id] !== undefined).map(([id]) => friendName(id));
  const fHtml = friendsWho.length ? `<div class="rare" title="${esc(friendsWho.join(', '))}">${icon('users')} ${friendsWho.length}</div>` : '';
  const hiddenDesc = a.hidden && !a.achieved && !a.description;
  const dlcSrc = a.dlc?.source === 'steam' ? 'Steam links this achievement to the DLC “' + (a.dlc?.name || '') + '”.' : 'Guessed from the achievement text.';
  const dlcPill = !a.dlc ? '' : a.dlc.owned
    ? `<span class="pill" title="${esc('This achievement belongs to a DLC you own. ' + dlcSrc)}">${icon('package')} ${esc(a.dlc.label)}</span>`
    : `<span class="pill violet" title="${esc('This achievement needs a DLC this account does not own. ' + dlcSrc)}">${icon('package')} Missing DLC: ${esc(a.dlc.label)}</span>`;
  const refusedPill = G.refused[a.id] && !a.achieved && !a.protected && !blocked
    ? `<span class="pill red" title="Steam refused to change this achievement the last time you tried it.">${icon('alert')} Refused by Steam</span>` : '';
  const control = a.protected
    ? `<div class="lockmark" title="This achievement is set by the game and cannot be changed with SAM. You have to unlock it in the game.">${icon('lock')}</div>`
    : blocked
      ? `<div class="lockmark dlc" title="Needs the DLC “${esc(a.dlc.label)}”, which this account does not own. It cannot be unlocked until you have it.">${icon('package')}</div>`
      : `<label class="switch" title="${want ? 'Unlocked' : 'Locked'}"><input type="checkbox" data-ach-toggle="${esc(a.id)}" ${want ? 'checked' : ''}><span class="tr"></span></label>`;
  return `<div class="ach${a.achieved ? ' on' : ''}${a.protected ? ' prot' : ''}${blocked ? ' dlcb' : ''}${changed ? ' changed' : ''}${want ? ' want-on' : ''}" data-ach="${esc(a.id)}">
    <img class="aicon" loading="lazy" decoding="async" src="${achIconUrl(G.id, a.achieved ? a.iconNormal : a.iconLocked)}" alt="" onerror="this.style.visibility='hidden'">
    <div class="atext"><div class="aname">${esc(a.name)}${a.hidden ? `<span class="pill">${icon('eyeOff')} Hidden</span>` : ''}${a.protected ? `<span class="pill amber">${icon('gamepad')} In game only</span>` : ''}${dlcPill}${refusedPill}</div>
      <div class="adesc${hiddenDesc ? ' hid' : ''}">${hiddenDesc ? `${icon('eyeOff')} Hidden achievement – the description becomes visible once it is unlocked.` : esc(a.description || '')}</div></div>
    <div class="ameta">${rarHtml}${fHtml}${dateHtml}</div>
    ${control}
  </div>`;
}

/** Repaints only the achievement list and its counter (keeps the rest of the page, e.g. the search box focus). */
function updateAchList() {
  const G = state.game, list = $('#alist');
  if (!list) return;
  const items = visibleAch();
  list.innerHTML = items.length ? items.map(achRowHtml).join('') : `<div class="noresult"><div class="ico">${icon('search')}</div><h3>No achievements found</h3><p>Adjust the filters.</p></div>`;
  const c = $('#acount');
  if (c) c.textContent = `${nf.format(items.length)} of ${nf.format(G.ach.length)} shown`;
}

/**
 * Marks an achievement to be unlocked or locked when the user applies the changes (nothing is sent yet).
 * Protected and DLC-blocked ones are ignored.
 */
function toggleAch(id, on) {
  const G = state.game, a = G.ach.find((x) => x.id === id);
  if (!a || a.protected || isDlcBlocked(a)) return;
  if (on === a.achieved) G.pending.delete(id); else G.pending.set(id, on);
  const row = $(`.ach[data-ach="${CSS.escape(id)}"]`);
  if (row) {
    row.classList.toggle('changed', G.pending.has(id));
    row.classList.toggle('want-on', on);
    const cb = $('input', row); if (cb) cb.checked = on;
  }
  renderActionBar();
}

/**
 * Bulk selection buttons over the visible achievements: select all unlockable, lock all unlocked, invert,
 * discard changes.
 */
function bulk(kind) {
  const G = state.game;
  const items = visibleAch().filter((a) => !a.protected && !isDlcBlocked(a));
  const set = (a, v) => { if (v === a.achieved) G.pending.delete(a.id); else G.pending.set(a.id, v); };
  if (kind === 'select') items.forEach((a) => set(a, true));
  else if (kind === 'lock') items.forEach((a) => set(a, false));
  else if (kind === 'invert') items.forEach((a) => set(a, !desired(a)));
  else if (kind === 'clear') { G.pending.clear(); G.statPending.clear(); if (G.tab === 'stats') renderPanel(); }
  updateAchList();
  renderActionBar();
}

/* ---------- Statistics ---------- */
/** The statistics table. Editing is behind a switch and a warning, protected statistics are read-only. */
function statsHtml() {
  const G = state.game;
  if (!G.stats.length) return `<div class="noresult"><div class="ico">${icon('info')}</div><h3>No statistics</h3><p>This game has no editable statistics.</p></div>`;
  return `<div class="stats-warn">${icon('alert')}<div><b>Statistics change the game</b><p>Wrong values can affect save games, leaderboards or achievements. Only edit them if you know what you are doing.</p></div>
      <label class="switch" style="margin-left:auto"><input type="checkbox" id="statsEdit" ${G.statsEdit ? 'checked' : ''}><span class="tr"></span><span>Edit</span></label></div>
    <table class="stable"><thead><tr><th>Statistic</th><th style="text-align:right">Value</th><th>Note</th></tr></thead><tbody>
    ${G.stats.map((s) => {
      const cur = G.statPending.has(s.id) ? G.statPending.get(s.id) : String(s.value);
      return `<tr class="${G.statPending.has(s.id) ? 'changed' : ''}" data-stat="${esc(s.id)}"><td>${esc(s.name)}<span class="id">${esc(s.id)}</span></td>
        <td style="text-align:right"><input class="field" type="text" inputmode="decimal" data-stat-input="${esc(s.id)}" value="${esc(cur)}" ${G.statsEdit && !s.protected ? '' : 'disabled'}></td>
        <td>${s.protected ? `<span class="pill amber">${icon('lock')} Protected</span>` : ''}${s.incrementOnly ? `<span class="pill">Increment only</span>` : ''}</td></tr>`;
    }).join('')}</tbody></table>
    <div class="danger-zone"><div><b>Reset everything</b><p>Resets all statistics (optionally all achievements as well) of this game. This cannot be undone.</p></div>
      <button class="btn danger" data-action="reset-stats">${icon('alert')} Reset …</button></div>`;
}

/* ---------- Friends tab ---------- */
/** The Friends tab: one card per person (you + selected friends) and, once data is loaded, the comparison matrix. */
function friendsTabHtml() {
  const G = state.game, fr = G.friends, sel = state.friends.settings.selected;
  const you = G.summary;
  const people = [{ id: 'me', name: 'You', total: you.total, unlocked: you.unlocked, me: true }];
  sel.forEach((id) => people.push({ id, name: friendName(id), r: fr.results[id] }));
  const reason = (r) => ({ hidden: 'Not visible (privacy)', 'no-data': 'No data (game not owned or private)', timeout: 'No response from Steam', request: 'Request failed' }[r.reason] || 'Not available');
  const cards = people.map((p) => {
    if (p.me) return personCard(p.name, 'me', p.unlocked, p.total, true);
    if (!p.r) return `<div class="pcard idle">${avatarHtml(p.name, p.id, { lg: true })}<div class="grow"><b>${esc(p.name)}</b><small>${fr.status === 'loading' ? 'Waiting …' : 'Not queried yet'}</small></div></div>`;
    if (!p.r.ok) return `<div class="pcard off">${avatarHtml(p.name, p.id, { lg: true })}<div class="grow"><b>${esc(p.name)}</b><small>${icon('lock')} ${reason(p.r)}</small></div></div>`;
    return personCard(p.name, p.id, p.r.unlocked, p.r.total, false);
  }).join('');
  const okCols = sel.filter((id) => fr.results[id]?.ok);
  const loaded = Object.keys(fr.results).length;
  return `
    <div class="dtools">
      <button class="btn primary" data-action="friends-compare" ${fr.status === 'loading' ? 'disabled' : ''}>${icon(fr.status === 'loading' ? 'refresh' : 'users', fr.status === 'loading' ? 'spin' : '')} ${fr.status === 'loading' ? `Querying … ${fr.done}/${sel.length}` : loaded ? 'Compare again' : 'Compare'}</button>
      <button class="btn" data-action="friends-pick">${icon('edit')} Change selection</button>
      <span class="note" style="color:var(--muted);font-size:13px">Only the friends you selected are queried (${sel.length}).</span>
    </div>
    <div class="pgrid">${cards}</div>
    ${okCols.length ? `<div class="dtools" style="margin-top:20px"><div class="search">${icon('search')}<input id="aq" type="search" placeholder="Search achievements …" autocomplete="off" value="${esc(G.f.q)}"></div>
      <select class="field" data-fdiff><option value="all">All achievements</option><option value="theyhave">Friends have it, I don't</option><option value="ihave">I have it, friends don't</option><option value="none">No friend has it</option></select></div>
      <div class="mwrap"><table class="matrix" id="matrix"></table></div>` : fr.status === 'idle' && !loaded ? `<div class="noresult"><div class="ico">${icon('users')}</div><h3>Nothing queried yet</h3><p>Click “Compare” to load the achievements of your selected friends for this game.</p></div>` : ''}`;
}
/** A card with a person's avatar, count and bar. */
function personCard(name, id, unlocked, total, me) {
  const p = total ? unlocked / total : 0;
  return `<div class="pcard${me ? ' me' : ''}${p >= 1 ? ' full' : ''}">${me ? `<span class="avatar lg ph">${icon('user')}</span>` : avatarHtml(name, id, { lg: true })}
    <div class="grow"><b>${esc(name)}</b><small>${nf.format(unlocked)} / ${nf.format(total)} · ${pctText(p)}</small><div class="bar"><i style="width:${p * 100}%"></i></div></div></div>`;
}

/** Finishes the Friends tab after it was rendered (makes sure names are loaded, then draws the matrix). */
async function initFriendsTab() {
  const G = state.game;
  if (!state.friends.list) {
    try { await ensureFriendList(); if (state.game === G && G.tab === 'friends') { $('#panel').innerHTML = friendsTabHtml(); initFriendsTab(); } return; }
    catch (e) { toast(e.message, 'error'); }
  }
  const sel = $('[data-fdiff]'); if (sel) sel.value = G.friends.diff;
  paintMatrix();
}

/** Draws the achievement matrix: one row per achievement, one column for you and one per friend with data. */
function paintMatrix() {
  const G = state.game, m = $('#matrix');
  if (!m) return;
  const cols = state.friends.settings.selected.filter((id) => G.friends.results[id]?.ok);
  const q = G.f.q.trim().toLowerCase();
  const rows = G.ach.filter((a) => {
    if (q && !a.name.toLowerCase().includes(q) && !a.id.toLowerCase().includes(q)) return false;
    const fh = cols.filter((id) => G.friends.results[id].achieved[a.id] !== undefined).length;
    if (G.friends.diff === 'theyhave') return fh > 0 && !a.achieved;
    if (G.friends.diff === 'ihave') return a.achieved && fh === 0;
    if (G.friends.diff === 'none') return fh === 0;
    return true;
  });
  const cell = (has, time) => has ? `<td class="y" title="${time ? fmtDate(time) : 'Unlocked'}">${icon('check')}</td>` : `<td class="n">${icon('minus')}</td>`;
  m.innerHTML = `<thead><tr><th>Achievement</th><th class="c">You</th>${cols.map((id) => `<th class="c" title="${esc(friendName(id))}">${avatarHtml(friendName(id), id)}<span>${esc(friendName(id).slice(0, 14))}</span></th>`).join('')}</tr></thead>
    <tbody>${rows.map((a) => `<tr><td><div class="mname"><img loading="lazy" src="${achIconUrl(G.id, a.achieved ? a.iconNormal : a.iconLocked)}" alt="" onerror="this.style.visibility='hidden'"><span>${esc(a.name)}</span>${a.protected ? `<span class="pill amber">${icon('gamepad')}</span>` : ''}</div></td>${cell(a.achieved, a.unlockTime)}${cols.map((id) => cell(G.friends.results[id].achieved[a.id] !== undefined, G.friends.results[id].achieved[a.id])).join('')}</tr>`).join('') || `<tr><td colspan="${cols.length + 2}" style="padding:30px;text-align:center;color:var(--muted)">No achievements for this filter.</td></tr>`}</tbody>`;
}

/**
 * Queries the selected friends for the open game, one friend per request, and updates the tab as each answer
 * arrives.
 */
async function compareFriends() {
  const G = state.game, ids = [...state.friends.settings.selected];
  if (!G || !ids.length || G.friends.status === 'loading') return;
  const gid = G.id;
  G.friends = { status: 'loading', results: {}, done: 0, diff: G.friends.diff };
  if (G.tab === 'friends') renderPanel();
  try { await ensureFriendList(); } catch { /* names are optional */ }
  for (const id of ids) {
    if (state.game !== G) return;           // the game was left
    try {
      const res = await api(`/api/game/${gid}/friends`, { method: 'POST', body: { steamIds: [id] } });
      G.friends.results[id] = res.results[0] || { ok: false, reason: 'request' };
    } catch (e) {
      G.friends.results[id] = { ok: false, reason: e.code === 'no-schema' ? 'no-data' : 'request' };
      if (e.code === 'friends-disabled') break;
    }
    G.friends.done++;
    if (state.game === G && G.tab === 'friends') renderPanel();
  }
  G.friends.status = 'done';
  if (state.game === G) { if (G.tab === 'friends') renderPanel(); else updateAchList(); }
}

/* ---------- Action bar & saving ---------- */
/** The bar at the bottom with the number of pending changes and the Apply/Discard buttons. */
function renderActionBar() {
  const bar = $('#actionbar'), G = state.game;
  if (!bar || !G) return;
  const n = pendingCount();
  const up = [...G.pending.values()].filter(Boolean).length, down = G.pending.size - up;
  bar.classList.toggle('show', n > 0);
  bar.innerHTML = `<div class="txt"><b>${plural(n, 'change', 'changes')}</b><small>${[up ? `${up} to unlock` : '', down ? `${down} to lock` : '', G.statPending.size ? `${plural(G.statPending.size, 'statistic', 'statistics')}` : ''].filter(Boolean).join(' · ')}</small></div>
    <button class="btn" data-action="bulk" data-bulk="clear" ${G.saving ? 'disabled' : ''}>Discard</button>
    <button class="btn primary" data-action="apply" ${G.saving ? 'disabled' : ''}>${G.saving ? `${icon('refresh', 'spin')} Saving …` : `${icon('check')} Apply changes`}</button>`;
}

/** Why Steam (or SAM) could not change an achievement or statistic, in plain words. */
const FAIL_WHY = {
  dlc: (f) => `Needs the DLC “${f.detail || 'unknown'}”, which this account does not own. Steam only allows it once you own that DLC.`,
  protected: () => 'The game controls this one itself – it can only be earned by playing the game.',
  unknown: () => 'Steam does not know this entry. The game data stored on this PC may be outdated – reload the game and try again.',
  invalid: () => 'The value is not valid for this statistic.',
  steam: () => 'Steam refused the change without giving a reason. Usually the game controls it itself, or it depends on something this account does not have (for example a DLC that the game data does not link).',
};
const failWhy = (f) => (FAIL_WHY[f.reason] || FAIL_WHY.steam)(f);
const FAIL_ICON = { dlc: 'package', protected: 'gamepad' };

/** After saving: one row per change that did not work, each with its reason. */
function showFailures(G, res) {
  const nameOf = (id) => G.ach.find((a) => a.id === id)?.name || G.stats.find((s) => s.id === id)?.name || id;
  const rows = res.failed.map((f) => `<div class="failrow">${icon(FAIL_ICON[f.reason] || 'alert')}<div><b>${esc(nameOf(f.id))}</b><small>${esc(failWhy(f))}</small></div></div>`).join('');
  modal({
    title: res.changed ? `${res.changed} saved, ${res.failed.length} not possible` : `${plural(res.failed.length, 'change', 'changes')} not possible`,
    icon: 'alert', cls: 'warn',
    body: `<div class="faillist">${rows}</div>`,
    actions: [{ label: 'OK', value: true, cls: 'primary' }],
  });
}

/**
 * Applies the pending changes after confirmation. Failed items are listed with their reason; the game is
 * reloaded from Steam's answer.
 */
async function applyChanges() {
  const G = state.game;
  if (!G || G.saving || !pendingCount()) return;
  const unlock = [], lock = [];
  G.pending.forEach((v, id) => (v ? unlock : lock).push(id));
  const stats = [...G.statPending].map(([id, value]) => ({ id, value }));
  const acct = activeAccount();
  const ok = await confirmBox({
    title: 'Apply changes?', icon: 'checkCircle',
    body: `<p>These changes will be saved for the account <b>${esc(acct?.personaName || acct?.accountName || 'active account')}</b> in <b>${esc(G.name)}</b>:</p>
      <ul>${unlock.length ? `<li><b>Unlock ${plural(unlock.length, 'achievement', 'achievements')}</b></li>` : ''}${lock.length ? `<li><b>Lock ${plural(lock.length, 'achievement', 'achievements')}</b> again</li>` : ''}${stats.length ? `<li><b>Change ${plural(stats.length, 'statistic', 'statistics')}</b></li>` : ''}</ul>`,
    ok: 'Apply now',
  });
  if (!ok) return;
  G.saving = true; renderActionBar();
  try {
    const res = await api(`/api/game/${G.id}/apply`, { method: 'POST', body: { unlock, lock, stats } });
    if (state.game !== G) return;
    G.pending.clear(); G.statPending.clear();
    setGameData(res.data);
    if (res.failed?.length) {
      res.failed.forEach((f) => { if (f.reason === 'steam') G.refused[f.id] = true; });
      toast(`${res.changed} saved, ${res.failed.length} could not be changed.`, 'warn', 6000);
      showFailures(G, res);
    }
    else if (!res.stored) toast('Saved, but Steam has not confirmed completion yet. Please reload shortly.', 'warn', 8000);
    else toast(`${plural(res.changed, 'change', 'changes')} saved.`, 'ok');
  } catch (e) {
    toast(e.message, 'error', 8000);
  } finally {
    G.saving = false;
    if (state.game === G) { renderGameShell(); }
  }
}

/** Resets all statistics (optionally the achievements too). Needs a typed confirmation word. */
async function resetStats() {
  const G = state.game;
  let both = false;
  const r = await modal({
    title: 'Reset statistics', icon: 'alert', cls: 'danger',
    body: `<p>All statistics of <b>${esc(G.name)}</b> will be reset to their initial values. This cannot be undone.</p>
      <label class="switch" style="margin-top:14px"><input type="checkbox" id="rstAch"><span class="tr"></span><span>Also reset <b>all achievements</b></span></label>
      <p style="margin-top:14px">To confirm, type <b>RESET</b>:</p><input class="field" id="rstText" autocomplete="off" spellcheck="false">`,
    actions: [{ label: 'Cancel', value: false }, { label: 'Reset permanently', value: true, cls: 'danger' }],
    onMount: (ov) => {
      const go = $('.btn.danger', ov);
      go.disabled = true;
      $('#rstAch', ov).addEventListener('change', (e) => (both = e.target.checked));
      $('#rstText', ov).addEventListener('input', (e) => { go.disabled = e.target.value.trim().toUpperCase() !== 'RESET'; });
    },
  });
  if (r !== true) return;
  G.saving = true;
  try {
    const res = await api(`/api/game/${G.id}/reset`, { method: 'POST', body: { achievements: both } });
    if (state.game !== G) return;
    G.pending.clear(); G.statPending.clear();
    setGameData(res.data);
    toast(both ? 'Statistics and achievements were reset.' : 'Statistics were reset.', 'ok');
  } catch (e) {
    toast(e.message, 'error', 8000);
  } finally {
    G.saving = false;
    if (state.game === G) renderGameShell();
  }
}

/* ==========================================================================
   Events
   ========================================================================== */
/** Leaves the game page, after asking if there are unapplied changes. */
async function goBack() {
  const G = state.game;
  if (G && pendingCount() && !(await confirmBox({ title: 'Discard unsaved changes?', icon: 'alert', body: '<p>You have changes that have not been applied yet.</p>', ok: 'Discard', danger: true }))) return;
  location.hash = '#/';
}

/** Dispatcher for every `data-action` click in the app. One case per action. */
async function handleAction(action, el) {
  const G = state.game;
  switch (action) {
    case 'open-game':
      if (state.auto.running && !state.select.on) { toast('An auto run is in progress – stop it first to open a game.', 'warn'); break; }
      if (state.select.on) {
        const id = +el.dataset.id;
        if (state.select.ids.has(id)) state.select.ids.delete(id); else state.select.ids.add(id);
        el.classList.toggle('sel', state.select.ids.has(id));
        updateSelBar();
      } else location.hash = `#/game/${el.dataset.id}`;
      break;
    case 'back': goBack(); break;
    case 'theme': cycleTheme(); break;
    case 'account-menu': state.popover && state.popover.dataset.anchor === 'account-menu' ? closePopover() : accountMenu(el); break;
    case 'switch-account': switchAccount(el.dataset.id); break;
    case 'switch-close': closeSwitching(); init(); break;
    case 'start-steam': try { await api('/api/steam/start', { method: 'POST' }); toast('Starting Steam …', 'info'); } catch (err) { toast(err.message, 'error'); } break;
    case 'retry': init(); break;
    case 'reload-library': if (!state.libraryLoading) {
      const before = new Set(state.games.filter((g) => g.isNew).map((g) => g.id));
      await loadLibrary(true);
      const added = state.games.filter((g) => g.isNew && !before.has(g.id)).length;
      toast(added ? `Library updated – ${plural(added, 'new game', 'new games')}.` : 'Library updated.', 'ok');
    } break;
    case 'scan-menu': state.popover && state.popover.dataset.anchor === 'scan-menu' ? closePopover() : scanMenu(el); break;
    case 'scan-start': startScan(el.dataset.mode); break;
    case 'scan-cancel': api('/api/scan/cancel', { method: 'POST' }).catch(() => {}); break;
    case 'auto-open': autoDialog(el.dataset.preset); break;
    case 'auto-cancel': state.auto.cancelling = true; renderAutobar(); api('/api/auto/cancel', { method: 'POST' }).catch(() => {}); break;
    case 'auto-undo': undoAuto(); break;
    case 'auto-details': state.auto.showDetails = !state.auto.showDetails; renderAutobar(); break;
    case 'auto-dismiss': state.auto.finished = false; renderAutobar(); break;
    case 'seg': state.filters[el.dataset.seg] = el.dataset.value; persistFilters(); updateLibrary(); break;
    case 'type': state.filters.types[el.dataset.type] = !state.filters.types[el.dataset.type]; persistFilters(); updateLibrary(); break;
    case 'clear-chip': filterClearers[+el.dataset.i]?.(); persistFilters(); updateLibrary(); break;
    case 'reset-filters': { state.filters = { ...defaultFilters(), q: '' }; setIdFilter(null); persistFilters(); const qi = $('#q'); if (qi) qi.value = ''; updateLibrary(); break; }
    case 'sort-dir': state.sort.dir = state.sort.dir === 'asc' ? 'desc' : 'asc'; LS.set('sort', state.sort); updateLibrary(); break;
    case 'view': state.view = el.dataset.view; LS.set('view', state.view); updateLibrary(); break;
    case 'toggle-sidebar': $('#sidebar')?.classList.toggle('open'); $('#scrim').classList.toggle('show'); break;
    case 'toggle-sec': state.collapsed[el.dataset.sec] = !isCollapsed(el.dataset.sec); LS.set('collapsed', state.collapsed); syncSidebar(); break;
    case 'sec-collapse': $$('#sidebar .fsec').forEach((s) => (state.collapsed[s.dataset.sec] = true)); LS.set('collapsed', state.collapsed); syncSidebar(); break;
    case 'sec-expand': $$('#sidebar .fsec').forEach((s) => (state.collapsed[s.dataset.sec] = false)); LS.set('collapsed', state.collapsed); syncSidebar(); break;
    case 'view-apply': { const v = allViews().find((x) => x.id === el.dataset.id); if (v) applyView(v); break; }
    case 'view-save': saveViewDialog(); break;
    case 'view-delete': deleteView(el.dataset.id); break;
    case 'select-toggle': toggleSelectMode(); break;
    case 'select-all': visibleGames().forEach((g) => state.select.ids.add(g.id)); updateLibrary(); break;
    case 'select-clear': state.select.ids = new Set(); updateLibrary(); break;
    case 'mark-seen': try { await api('/api/seen', { method: 'POST' }); await quietReload(false); toast('Everything is marked as seen.', 'ok'); } catch (err) { toast(err.message, 'error'); } break;
    case 'tab': G.tab = el.dataset.tab; $$('#tabs button').forEach((b) => b.classList.toggle('on', b === el)); renderPanel(); break;
    case 'adir': G.f.dir = G.f.dir === 'asc' ? 'desc' : 'asc'; updateAchList(); break;
    case 'bulk': bulk(el.dataset.bulk); break;
    case 'apply': applyChanges(); break;
    case 'game-reload': {
      if (G && pendingCount() && !(await confirmBox({ title: 'Discard changes?', icon: 'alert', body: '<p>Reloading discards changes that have not been applied.</p>', ok: 'Reload', danger: true }))) return;
      const id = G.id, news = G.news;
      state.game = newGameState(id); renderGameShell();
      try {
        const data = await api(`/api/game/${id}/open`, { method: 'POST' });
        if (state.game?.id === id) { setGameData(data); state.game.news = news; state.game.loading = false; renderGameShell(); loadRarity(id); }
      } catch (err) { if (state.game?.id === id) { state.game.error = err; state.game.loading = false; renderGameShell(); } }
      break;
    }
    case 'reset-stats': resetStats(); break;
    case 'fprog-menu': state.popover && state.popover.dataset.anchor === 'fprog-menu' ? closePopover() : fprogMenu(el); break;
    case 'fprog-start': startFriendProgress(el.dataset.mode); break;
    case 'fprog-cancel': api('/api/friends/scan/cancel', { method: 'POST' }).catch(() => {}); break;
    case 'friends-activate': friendsActivate(); break;
    case 'friends-deactivate': friendsDeactivate(); break;
    case 'friends-pick': friendsPick(); break;
    case 'friends-compare': compareFriends(); break;
  }
}

document.addEventListener('click', (e) => {
  const act = e.target.closest('[data-action]');
  if (state.popover && !e.target.closest('.popover') && !(act && ['account-menu', 'scan-menu', 'fprog-menu'].includes(act.dataset.action))) closePopover();
  if (e.target.id === 'scrim') { $('#sidebar')?.classList.remove('open'); $('#scrim').classList.remove('show'); return; }
  if (act) { handleAction(act.dataset.action, act); return; }

  // Clicking an achievement row toggles it (except locked rows and the switch itself)
  const row = e.target.closest('.ach');
  if (row && !row.classList.contains('prot') && !row.classList.contains('dlcb') && !e.target.closest('label.switch')) {
    const a = state.game?.ach.find((x) => x.id === row.dataset.ach);
    if (a) toggleAch(a.id, !desired(a));
  }
});

document.addEventListener('input', (e) => {
  const t = e.target;
  if (t.id === 'q') { state.filters.q = t.value; updateLibrary(); return; }
  if (t.id === 'aq') { state.game.f.q = t.value; if (state.game.tab === 'friends') paintMatrix(); else updateAchList(); return; }
  if (t.dataset.filter === 'pctMin') { state.filters.pctMin = Math.min(+t.value, state.filters.pctMax); persistFilters(); updateLibrary(); return; }
  if (t.dataset.filter === 'pctMax') { state.filters.pctMax = Math.max(+t.value, state.filters.pctMin); persistFilters(); updateLibrary(); return; }
  if (t.dataset.filter === 'minAch') { state.filters.minAch = Math.max(0, parseInt(t.value, 10) || 0); persistFilters(); updateLibrary(); return; }
  if (t.id === 'sizer') { state.size = +t.value; LS.set('size', state.size); document.documentElement.style.setProperty('--card-min', `${state.size}px`); return; }
  if (t.dataset.statInput) {
    const G = state.game, s = G.stats.find((x) => x.id === t.dataset.statInput);
    const v = t.value.trim().replace(',', '.');
    if (v === String(s.value)) G.statPending.delete(s.id); else G.statPending.set(s.id, v);
    t.closest('tr').classList.toggle('changed', G.statPending.has(s.id));
    renderActionBar();
  }
});

document.addEventListener('change', (e) => {
  const t = e.target;
  if (t.dataset.filter === 'hideComplete') { state.filters.hideComplete = t.checked; persistFilters(); updateLibrary(); }
  else if (t.dataset.filter === 'hideNone') { state.filters.hideNone = t.checked; persistFilters(); updateLibrary(); }
  else if (t.dataset.filter === 'played') { state.filters.played = t.value; persistFilters(); updateLibrary(); }
  else if (t.id === 'sortKey') { state.sort.key = t.value; state.sort.dir = SORTS[t.value].dir; LS.set('sort', state.sort); updateLibrary(); }
  else if (t.dataset.af) { state.game.f[t.dataset.af] = t.value; updateAchList(); }
  else if (t.dataset.achToggle) toggleAch(t.dataset.achToggle, t.checked);
  else if (t.id === 'statsEdit') { state.game.statsEdit = t.checked; $$('[data-stat-input]').forEach((i) => { const s = state.game.stats.find((x) => x.id === i.dataset.statInput); i.disabled = !(t.checked && !s.protected); }); }
  else if (t.dataset.fdiff !== undefined) { state.game.friends.diff = t.value; paintMatrix(); }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    // Modals handle their own Escape (capture phase). Here: popover -> drawer -> back from a game.
    if (state.popover) { closePopover(); return; }
    const drawer = $('#sidebar');
    if (drawer?.classList.contains('open')) { drawer.classList.remove('open'); $('#scrim')?.classList.remove('show'); return; }
    if (document.querySelector('.overlay')) return;
    if (state.route.name === 'game') {
      const el = document.activeElement;
      if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) { el.blur(); return; }   // first Esc leaves the field
      e.preventDefault();
      goBack();
      return;
    }
    if (state.select.on) { toggleSelectMode(); return; }
  }
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName);
  if (e.key === '/' && !typing && state.route.name === 'library') { e.preventDefault(); $('#q')?.focus(); }
  if ((e.key === 'Enter' || e.key === ' ') && document.activeElement?.classList.contains('card')) { e.preventDefault(); document.activeElement.click(); }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's' && state.game && pendingCount()) { e.preventDefault(); applyChanges(); }
});

// Images that fail to load get a colored placeholder
document.addEventListener('error', (e) => {
  if (e.target.tagName === 'IMG' && e.target.classList.contains('gimg')) e.target.closest('.thumb')?.classList.add('noimg');
}, true);

window.addEventListener('hashchange', route);
window.addEventListener('beforeunload', () => { if (state.game) navigator.sendBeacon?.(`/api/game/${state.game.id}/close?t=${encodeURIComponent(state.token)}`); });

/* ---------- Boot ---------- */
(async function boot() {
  const m = /[#&]t=([A-Za-z0-9_-]+)/.exec(location.hash);
  if (m) {
    state.token = m[1];
    try { sessionStorage.setItem('sam-token', state.token); } catch { /* ignore */ }
    history.replaceState(null, '', location.pathname + location.search + '#/');
  } else {
    try { state.token = sessionStorage.getItem('sam-token') || ''; } catch { state.token = ''; }
  }
  if (!state.token) {
    applyTheme();
    renderTopbar();
    $('#view').innerHTML = `<div class="state"><div class="ico warn">${icon('lock')}</div><h2>Access denied</h2><p>This page can only be opened from within the app. Please restart the Steam Achievement Manager.</p></div>`;
    return;
  }
  await loadPrefs();
  applyPrefs();
  applyTheme();
  renderTopbar();
  init();
})();
