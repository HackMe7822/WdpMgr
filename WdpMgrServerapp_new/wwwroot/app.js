// WdpMgr Admin Panel — full featured JS

let API_KEY  = '';
let USERNAME = '';
let revokeId = '', revokeLabel_ = '';

// ── Session persistence — restore synchronously, no round-trip ───────────────
const _sk = sessionStorage.getItem('wdp_key');
const _su = sessionStorage.getItem('wdp_user');
if (_sk) { API_KEY = _sk; USERNAME = _su || 'admin'; }

// ── SVG icons injected into <i data-icon> ─────────────────────────────────────
const ICONS = {
  grid:     '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/></svg>',
  file:     '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/></svg>',
  monitor:  '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="3" width="20" height="14" rx="2"/><polyline points="8 21 12 17 16 21"/></svg>',
  box:      '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="21 8 21 21 3 21 3 8"/><rect x="1" y="3" width="22" height="5"/><line x1="10" y1="12" x2="14" y2="12"/></svg>',
  users:    '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>',
  settings: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>'
};
document.querySelectorAll('i[data-icon]').forEach(el => { el.innerHTML = ICONS[el.dataset.icon] || ''; });

// ── Login ─────────────────────────────────────────────────────────────────────
function doLogin() {
  const user = document.getElementById('login-user').value.trim();
  const pass = document.getElementById('login-pass').value;
  if (!user || !pass) { showLoginErr('Enter username and password.'); return; }

  // Special: username "master" + master key
  if (user === 'master') {
    fetch('/api/admin/stats', { headers: { 'X-Admin-Key': pass } })
      .then(r => { if (r.status === 401) throw new Error('Invalid master key.'); return r.json(); })
      .then(() => { API_KEY = pass; USERNAME = 'master (admin key)'; enterApp(); })
      .catch(e => showLoginErr(e.message));
    return;
  }

  fetch('/api/admin/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: user, password: pass })
  }).then(r => {
    if (r.status === 401) throw new Error('Invalid username or password.');
    if (!r.ok) throw new Error('Server error ' + r.status);
    return r.json();
  }).then(d => {
    API_KEY = d.apiKey; USERNAME = user; enterApp();
  }).catch(e => showLoginErr(e.message));
}

function showLoginErr(m) { document.getElementById('login-error').textContent = m; }

function enterApp() {
  sessionStorage.setItem('wdp_key', API_KEY);
  sessionStorage.setItem('wdp_user', USERNAME);
  document.getElementById('login-overlay').style.display = 'none';
  document.getElementById('app').classList.remove('hidden');
  document.getElementById('whoami').textContent = USERNAME;
  loadAll();
}

['login-user','login-pass'].forEach(id =>
  document.getElementById(id).addEventListener('keydown', e => { if (e.key==='Enter') doLogin(); }));

function doLogout() {
  API_KEY = ''; USERNAME = '';
  sessionStorage.clear();
  document.getElementById('app').classList.add('hidden');
  document.getElementById('login-overlay').style.display = '';
  document.getElementById('login-pass').value = '';
  document.getElementById('login-error').textContent = '';
}

// ── Navigation + auto-refresh ─────────────────────────────────────────────────
let currentView = 'dashboard';
let _autoRefreshTimer = null;

function nav(name, el) {
  stopCountdowns(); // stop ticker when leaving licenses/machines
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
  const view = document.getElementById('view-' + name);
  if (view) view.classList.add('active');
  if (el)  el.classList.add('active');
  currentView = name;
  const loaders = { dashboard: loadDashboard, licenses: loadLicenses, machines: loadMachines, apps: loadApps, users: loadUsers, settings: loadSettings };
  if (loaders[name]) loaders[name]();
  clearInterval(_autoRefreshTimer);
  if (name === 'licenses' || name === 'machines') {
    _autoRefreshTimer = setInterval(() => { if (currentView === 'licenses') loadLicenses(); else if (currentView === 'machines') loadMachines(); }, 30000);
  }
}

// Navigate to Licenses/Machines tab with a pre-set filter.
// Search box is set BEFORE nav() so that when the fetch resolves and
// calls filterLicenses/filterMachines, the filter is already in place.
function navLicensesFiltered(status) {
  document.getElementById('lic-search').value = status;
  nav('licenses', document.querySelector('[data-view=licenses]'));
}
function navMachinesFiltered(status) {
  document.getElementById('mach-search').value = status;
  nav('machines', document.querySelector('[data-view=machines]'));
}

// ── API helper ────────────────────────────────────────────────────────────────
function api(method, path, body) {
  const opts = { method, headers: { 'X-Admin-Key': API_KEY, 'Content-Type': 'application/json' } };
  if (body != null) opts.body = JSON.stringify(body);
  return fetch(path, opts).then(r => r.json().then(d => {
    if (r.status === 401) { doLogout(); throw new Error('Session expired'); }
    if (!r.ok) throw new Error(d.error || 'Error ' + r.status);
    return d;
  }));
}

let _allApps = [];
function loadAppsCache() {
  api('GET','/api/admin/apps').then(apps => {
    _allApps = apps;
    // Refresh dropdown if modal is open
    const sel = document.getElementById('nl-app');
    if (sel && !document.getElementById('modal-lic').classList.contains('hidden'))
      populateAppDropdown(sel.value);
  }).catch(()=>{});
}
function populateAppDropdown(selectedId='') {
  const sel = document.getElementById('nl-app');
  if (!_allApps.length) {
    sel.innerHTML = '<option value="">⚠ No apps — go to Apps tab and add first</option>';
    return;
  }
  sel.innerHTML = '<option value="">— select an app —</option>' +
    _allApps.map(a => `<option value="${a.id}"${a.id===selectedId?' selected':''}>${e(a.name)} (${e(a.slug)})</option>`).join('');
  if (selectedId) sel.value = selectedId;
}

function loadAll() { loadDashboard(); loadAppsCache(); }

// Enter app on load if session was restored
document.addEventListener('DOMContentLoaded', () => { if (API_KEY) enterApp(); });

// ── Dashboard ─────────────────────────────────────────────────────────────────
function loadDashboard() {
  api('GET','/api/admin/stats').then(d => {
    document.getElementById('s-total').textContent   = d.totalLicenses;
    document.getElementById('s-active').textContent  = d.activeLicenses;
    document.getElementById('s-expired').textContent = d.expiredLicenses;
    document.getElementById('s-revoked').textContent = d.revokedLicenses;
    document.getElementById('s-total-machines').textContent = d.totalMachines ?? 0;
    document.getElementById('s-machines').textContent= d.activeMachines;
    document.getElementById('s-offline').textContent = d.offlineMachines ?? 0;
    document.getElementById('s-users').textContent   = d.totalAdminUsers;
  }).catch(e => toast(e.message, true));

  api('GET','/api/admin/machines').then(ms => {
    const tbody = document.getElementById('dash-machines');
    const recent = ms.slice(0,6);
    if (!recent.length) { tbody.innerHTML = noData(5); return; }
    tbody.innerHTML = recent.map(m => `<tr>
      <td>${e(m.hostname||'—')}</td>
      <td class="mono">${e(m.windowsUser||'—')}</td>
      <td>${e(m.licenseLabel||m.licenseId)}</td>
      <td class="muted">${e(m.lastSeen||'—')}</td>
      <td>${badge(m.status)}</td></tr>`).join('');
  }).catch(()=>{});
}

// ── Licenses ──────────────────────────────────────────────────────────────────
let _allLicenses = [];
function loadLicenses() {
  api('GET','/api/admin/licenses').then(list => {
    _allLicenses = list;
    filterLicenses(); // respect whatever is currently in the search box
  }).catch(e2 => toast(e2.message, true));
}
function filterLicenses() {
  const q = (document.getElementById('lic-search').value||'').toLowerCase();
  renderLicenses(!q ? _allLicenses : _allLicenses.filter(l =>
    (l.label||'').toLowerCase().includes(q) ||
    (l.type||'').toLowerCase().includes(q) ||
    (l.notes||'').toLowerCase().includes(q) ||
    (l.appName||'').toLowerCase().includes(q) ||
    (l.revoked?'revoked':l.expiry&&new Date(l.expiry)<new Date()?'expired':'active').includes(q)
  ));
}
function renderLicenses(list) {
  const tbody = document.getElementById('lic-body');
  if (!list.length) { tbody.innerHTML = '<tr><td colspan="8" class="empty">No licenses found.</td></tr>'; stopCountdowns(); return; }
  tbody.innerHTML = list.map(l => {
    const unlimited = l.maxActivations < 0;
    const status = l.revoked ? 'revoked'
      : (l.expiryEpochMs && l.expiryEpochMs < Date.now()) ? 'expired'
      : (l.daysLeft != null && l.daysLeft < 0) ? 'expired'
      : (l.type==='days' && l.activatedAt && hoursExpired(l.activatedAt, l.durationDays)) ? 'expired'
      : 'active';
    let expCol;
    if (l.type === 'lifetime') {
      expCol = '∞ Lifetime';
    } else if (l.revoked) {
      expCol = '<span class="muted">—</span>';
    } else if (l.expiryEpochMs != null) {
      // Server is up-to-date — live second-precision countdown
      expCol = `<span data-expires-ms="${l.expiryEpochMs}" data-expiry-display="${e(l.expiryDisplay||'')}"></span>`;
    } else if (l.daysLeft != null) {
      // Older server binary — fall back to minute-precision display
      if (l.daysLeft < 0) {
        expCol = `<span style="color:var(--red)">Expired${l.expiryDisplay ? ' ('+l.expiryDisplay+')' : ''}</span>`;
      } else if (l.daysLeft >= 2880 && l.expiryDisplay) {
        expCol = `<span style="color:var(--green)" title="${fmtMins(l.daysLeft)} remaining">${l.expiryDisplay}</span>`;
      } else {
        const col = l.daysLeft <= 2880 ? 'var(--amber)' : 'var(--green)';
        expCol = `<span style="color:${col}"${l.expiryDisplay ? ` title="Expires ${l.expiryDisplay}"` : ''}>${fmtMins(l.daysLeft)}</span>`;
      }
    } else {
      expCol = l.type === 'days' ? fmtHours(l.durationDays) + ' <span class="muted">(not activated)</span>' : '<span class="muted">—</span>';
    }
    const seatsCol = unlimited ? `${l.activeSeats} / ∞` : `${l.activeSeats} / ${l.maxActivations}`;
    return `<tr>
      <td><strong>${e(l.label)}</strong>${l.notes?'<br><small class="muted">'+e(l.notes)+'</small>':''}</td>
      <td><span class="badge badge-type-${l.type}">${l.type}</span></td>
      <td class="muted">${e(l.appName||'—')}</td>
      <td class="muted">${e(l.issued)}</td>
      <td class="muted">${expCol}</td>
      <td class="muted">${seatsCol}</td>
      <td>${badge(status)}</td>
      <td>
        ${!l.revoked
          ? `<button class="btn-icon" onclick='dlExe("${l.id}")'>${l.appSlug==='macoverlay'?'⬇ .lic':'⬇ EXE'}</button>
             <button class="btn-icon" onclick='openEditModal("${l.id}","${e(l.label)}","${l.type}","${e(l.expiry||'')}",${l.durationDays},${l.maxActivations},"${e(l.notes||'')}","${e(l.appId||'')}",${l.countReinstalls||0})'>✏</button>
             <button class="btn-icon danger" onclick='openBlock("${l.id}","${e(l.label)}")'>🚫 Revoke</button>
             <button class="btn-icon danger" onclick='purgeLicense("${l.id}","${e(l.label)}")'>🗑</button>`
          : `<button class="btn-icon" onclick='reactivateLicense("${l.id}","${e(l.label)}")'>↺ Reactivate</button>
             <button class="btn-icon danger" onclick='purgeLicense("${l.id}","${e(l.label)}")'>🗑 Delete</button>`
        }
      </td></tr>`;
  }).join('');
  startCountdowns();
}

// ── Machines ──────────────────────────────────────────────────────────────────
let _allMachines = [];
function loadMachines() {
  api('GET','/api/admin/machines').then(list => {
    _allMachines = list;
    filterMachines(); // respect whatever is currently in the search box
  }).catch(e2 => toast(e2.message, true));
}
function filterMachines() {
  const q = (document.getElementById('mach-search').value||'').toLowerCase();
  renderMachines(!q ? _allMachines : _allMachines.filter(m =>
    (m.hostname||'').toLowerCase().includes(q) ||
    (m.windowsUser||'').toLowerCase().includes(q) ||
    (m.licenseLabel||m.licenseId||'').toLowerCase().includes(q) ||
    (m.ipAddress||'').toLowerCase().includes(q) ||
    (m.status||'').toLowerCase().includes(q)
  ));
}
function renderMachines(list) {
  const tbody = document.getElementById('mach-body');
  if (!list.length) { tbody.innerHTML = '<tr><td colspan="9" class="empty">No machines found.</td></tr>'; stopCountdowns(); return; }
  tbody.innerHTML = list.map(m => {
    let timeLeft;
    if (m.licenseType === 'lifetime' || (m.expiryEpochMs == null && m.daysLeft == null)) {
      timeLeft = '<span style="color:var(--green)">∞</span>';
    } else if (m.expiryEpochMs != null) {
      // Server up-to-date — live second-precision countdown
      timeLeft = `<span data-expires-ms="${m.expiryEpochMs}" data-expiry-display="${e(m.expiryDisplay||'')}"></span>`;
    } else {
      // Older server binary — minute-precision fallback
      if (m.daysLeft < 0) {
        timeLeft = `<span style="color:var(--red)">Expired${m.expiryDisplay ? ' ('+m.expiryDisplay+')' : ''}</span>`;
      } else if (m.daysLeft >= 2880 && m.expiryDisplay) {
        timeLeft = `<span style="color:var(--green)" title="${fmtMins(m.daysLeft)} remaining">${m.expiryDisplay}</span>`;
      } else {
        const col = m.daysLeft <= 2880 ? 'var(--amber)' : 'var(--green)';
        timeLeft = `<span style="color:${col}"${m.expiryDisplay ? ` title="Expires ${m.expiryDisplay}"` : ''}>${fmtMins(m.daysLeft)}</span>`;
      }
    }
    return `<tr>
      <td>${e(m.hostname||'—')}</td>
      <td class="mono">${e(m.windowsUser||'—')}</td>
      <td>${e(m.licenseLabel||m.licenseId)}</td>
      <td class="muted">${e(m.ipAddress||'—')}</td>
      <td>${timeLeft}</td>
      <td class="muted">${e(m.lastSeen||'—')}</td>
      <td><span class="mono fp" title="${e(m.seatKey)}">${e((m.seatKey||'').substring(0,14))}…</span></td>
      <td>${badge(m.status)}</td>
      <td style="white-space:nowrap">
        ${m.status==='revoked'
          ?`<button class="btn-icon" onclick='unrevokeMachine("${m.id}")' title="Un-Revoke">↺ Un-Revoke</button>`
          :`<button class="btn-icon${m.status==='offline'?'':' danger'}" onclick='revokeMachine("${m.id}")' title="Revoke — blocks this machine">✕ Revoke</button>`
        }
        <button class="btn-icon danger" onclick='deleteMachine("${m.id}","${e(m.hostname||m.id)}")' title="Delete row and free the seat">🗑</button>
      </td>
    </tr>`;
  }).join('');
  startCountdowns();
}

// ── Apps ──────────────────────────────────────────────────────────────────────
function loadApps() {
  api('GET','/api/admin/apps').then(list => {
    _allApps = list;
    const tbody = document.getElementById('apps-body');
    if (!list.length) { tbody.innerHTML = '<tr><td colspan="5" class="empty">No apps yet. Add WdpMgr, WinOverlay, MacOverlay etc.</td></tr>'; return; }
    tbody.innerHTML = list.map(a => `<tr>
      <td><strong>${e(a.name)}</strong></td>
      <td><code class="mono">${e(a.slug)}</code></td>
      <td class="muted">${e(a.description||'—')}</td>
      <td class="muted">${e(a.createdAt||'—')}</td>
      <td><button class="btn-icon danger" onclick='deleteApp("${a.id}","${e(a.name)}")'>✕ Delete</button></td>
    </tr>`).join('');
  }).catch(e2 => toast(e2.message, true));
}

function createApp() {
  const name = document.getElementById('na-name').value.trim();
  const slug = document.getElementById('na-slug').value.trim().toLowerCase().replace(/\s+/g,'');
  const desc = document.getElementById('na-desc').value.trim();
  if (!name) { toast('Name required', true); return; }
  if (!slug) { toast('Slug required', true); return; }
  api('POST','/api/admin/apps',{name,slug,description:desc}).then(()=>{
    closeModal('modal-app');
    document.getElementById('na-name').value = '';
    document.getElementById('na-slug').value = '';
    document.getElementById('na-desc').value = '';
    toast('App added'); loadApps();
  }).catch(e2 => toast(e2.message, true));
}

function deleteApp(id, name) {
  if (!confirm(`Delete app "${name}"?\n\nExisting licenses with this app will still work but won't validate app-scope.`)) return;
  api('DELETE',`/api/admin/apps/${id}`).then(()=>{ toast('App deleted'); loadApps(); })
    .catch(e2 => toast(e2.message, true));
}

// ── Admin Users ───────────────────────────────────────────────────────────────
function loadUsers() {
  api('GET','/api/admin/users').then(list => {
    const tbody = document.getElementById('users-body');
    if (!list.length) { tbody.innerHTML = '<tr><td colspan="5" class="empty">No admin users.</td></tr>'; return; }
    tbody.innerHTML = list.map(u => `<tr>
      <td><strong>${e(u.username)}</strong></td>
      <td><span class="badge badge-role-${u.role}">${u.role}</span></td>
      <td class="muted">${e(u.createdAt)}</td>
      <td class="muted">${e(u.lastLogin||'Never')}</td>
      <td>
        <button class="btn-icon" onclick='changePassword("${u.id}","${e(u.username)}")'>🔑 Change Pwd</button>
        <button class="btn-icon" onclick='resetKey("${u.id}","${e(u.username)}")'>⟳ Reset Key</button>
        <button class="btn-icon danger" onclick='deleteUser("${u.id}","${e(u.username)}")'>✕</button>
      </td></tr>`).join('');
  }).catch(e2 => toast(e2.message, true));
}

function createUser() {
  const username = document.getElementById('nu-username').value.trim();
  const password = document.getElementById('nu-password').value;
  const role     = document.getElementById('nu-role').value;
  if (!username || !password) { toast('Username and password required', true); return; }
  api('POST','/api/admin/users',{username,password,role}).then(d => {
    closeModal('modal-user');
    toast(`User "${username}" created. API key: ${d.apiKey}`);
    loadUsers();
  }).catch(e2 => toast(e2.message, true));
}

function deleteUser(id, name) {
  if (!confirm(`Delete admin user "${name}"?`)) return;
  api('DELETE',`/api/admin/users/${id}`).then(()=>{ toast('User deleted'); loadUsers(); })
    .catch(e2 => toast(e2.message, true));
}

function resetKey(id, name) {
  if (!confirm(`Reset API key for "${name}"?`)) return;
  api('POST',`/api/admin/users/${id}/reset-key`).then(d => {
    toast(`New API key for ${name}: ${d.apiKey}`);
  }).catch(e2 => toast(e2.message, true));
}

let _chgPwdId = '';
function changePassword(id, name) {
  _chgPwdId = id;
  document.getElementById('chgpwd-name').textContent = name;
  document.getElementById('chgpwd-pass').value = '';
  openModal('modal-chgpwd');
}
function submitChangePassword() {
  const pass = document.getElementById('chgpwd-pass').value;
  if (!pass) { toast('Password required', true); return; }
  api('POST',`/api/admin/users/${_chgPwdId}/change-password`,{password:pass}).then(()=>{
    closeModal('modal-chgpwd'); toast('Password changed');
  }).catch(e2 => toast(e2.message, true));
}

// ── License CRUD ──────────────────────────────────────────────────────────────
function onUnlimitedChange() {
  const chk = document.getElementById('nl-unlimited');
  const inp = document.getElementById('nl-maxact');
  inp.disabled = chk.checked;
  if (chk.checked) inp.value = '';
}

function openLicModal() {
  editLicId = null;
  document.querySelector('#modal-lic .modal-header h2').textContent = 'New License';
  document.querySelector('#modal-lic .modal-footer .btn-primary').textContent = 'Create';
  document.getElementById('nl-countreinstalls').checked = false;
  document.getElementById('nl-label').value    = '';
  document.getElementById('nl-type').value     = 'lifetime';
  document.getElementById('nl-type').disabled  = false;
  document.getElementById('nl-expiry').value   = '';
  document.getElementById('nl-days').value     = '720';
  document.getElementById('nl-maxact').value   = '1';
  document.getElementById('nl-maxact').disabled = false;
  document.getElementById('nl-unlimited').checked = false;
  document.getElementById('nl-notes').value    = '';
  onLicTypeChange();
  populateAppDropdown();
  nav('licenses', document.querySelector('[data-view=licenses]'));
  openModal('modal-lic');
}

function onLicTypeChange() {
  const t = document.getElementById('nl-type').value;
  document.getElementById('nl-row-expiry').classList.toggle('hidden', t !== 'temp' && t !== 'hr');
  document.getElementById('nl-row-days').classList.toggle('hidden',   t !== 'days');
  document.getElementById('nl-seats-label').textContent =
    t === 'hr' ? 'Max Seats (unique Windows users)' : 'Max Machines';
  // Update expiry label for HR context
  const expLabel = document.querySelector('#nl-row-expiry label');
  if (expLabel) expLabel.innerHTML = t === 'hr'
    ? 'Validity — expires at (optional) <span class="muted small">(your local time — auto-converted to UTC)</span>'
    : 'Expiry Date &amp; Time <span class="muted small">(your local time — auto-converted to UTC)</span>';
}

function submitLicModal() {
  if (editLicId) { saveLicenseEdit(); return; }
  const label      = document.getElementById('nl-label').value.trim();
  const type       = document.getElementById('nl-type').value;
  const expiryRaw  = document.getElementById('nl-expiry').value;
  const expiryUtc  = localInputToUtc(expiryRaw);
  const days   = parseInt(document.getElementById('nl-days').value) || 0;
  const maxAct          = document.getElementById('nl-unlimited').checked ? -1 : (parseInt(document.getElementById('nl-maxact').value) || 1);
  const appId           = document.getElementById('nl-app').value;
  const notes           = document.getElementById('nl-notes').value.trim();
  const countReinstalls = document.getElementById('nl-countreinstalls').checked ? 1 : 0;
  if (!label) { toast('Label required', true); return; }
  if (!appId) { toast('Please select an App for this license', true); return; }
  if (type==='temp' && !expiryRaw) { toast('Expiry date required', true); return; }
  if (type==='days' && days < 1) { toast('Duration must be >= 1 hour', true); return; }
  api('POST','/api/admin/licenses',{label,type,expiry:expiryUtc,durationDays:days,maxActivations:maxAct,appId,notes,countReinstalls}).then(()=>{
    closeModal('modal-lic'); toast('License created'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

let editLicId = null;

// Convert UTC ISO string → datetime-local input value (YYYY-MM-DDTHH:MM in local time)
function utcToLocalInput(utcStr) {
  if (!utcStr) return '';
  const d = new Date(utcStr);
  if (isNaN(d)) return '';
  const pad = n => String(n).padStart(2,'0');
  return d.getFullYear()+'-'+pad(d.getMonth()+1)+'-'+pad(d.getDate())+'T'+pad(d.getHours())+':'+pad(d.getMinutes());
}

// Convert datetime-local input value (local time, no tz) → UTC ISO string
function localInputToUtc(localStr) {
  if (!localStr) return '';
  return new Date(localStr).toISOString();
}

function openEditModal(id, label, type, expiry, durationDays, maxAct, notes, appId, countReinstalls) {
  editLicId = id;
  document.querySelector('#modal-lic .modal-header h2').textContent = 'Edit License';
  document.querySelector('#modal-lic .modal-footer .btn-primary').textContent = 'Save';
  document.getElementById('nl-countreinstalls').checked = !!countReinstalls;
  document.getElementById('nl-label').value   = label;
  document.getElementById('nl-type').value    = type;
  document.getElementById('nl-type').disabled = true;
  document.getElementById('nl-expiry').value  = utcToLocalInput(expiry);
  document.getElementById('nl-days').value    = durationDays || 720;
  const unlimited = maxAct < 0;
  document.getElementById('nl-unlimited').checked  = unlimited;
  document.getElementById('nl-maxact').value        = unlimited ? '' : maxAct;
  document.getElementById('nl-maxact').disabled     = unlimited;
  document.getElementById('nl-notes').value   = notes || '';
  onLicTypeChange();
  populateAppDropdown(appId || '');
  openModal('modal-lic');
}

function saveLicenseEdit() {
  const label     = document.getElementById('nl-label').value.trim();
  const expiryRaw = document.getElementById('nl-expiry').value;
  const expiryUtc = localInputToUtc(expiryRaw);
  const hours  = parseInt(document.getElementById('nl-days').value) || 0;
  const maxAct          = document.getElementById('nl-unlimited').checked ? -1 : (parseInt(document.getElementById('nl-maxact').value) || 1);
  const notes           = document.getElementById('nl-notes').value.trim();
  const countReinstalls = document.getElementById('nl-countreinstalls').checked ? 1 : 0;
  if (!label) { toast('Label required', true); return; }
  api('PUT',`/api/admin/licenses/${editLicId}`,{label,expiry:expiryUtc,durationDays:hours,maxActivations:maxAct,notes,countReinstalls}).then(()=>{
    closeModal('modal-lic'); toast('License updated'); loadLicenses();
  }).catch(e2 => toast(e2.message, true));
}

function openBlock(id, label) {
  revokeId = id; revokeLabel_ = label;
  document.getElementById('rv-label').textContent = label;
  openModal('modal-revoke');
}

function confirmRevoke() {
  api('POST',`/api/admin/licenses/${revokeId}/block`).then(()=>{
    closeModal('modal-revoke'); toast('License revoked — all machines will self-remove on next check-in'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

function reactivateLicense(id, label) {
  if (!confirm(`Reactivate "${label}"? Machines can check in again.`)) return;
  api('POST',`/api/admin/licenses/${id}/reactivate`).then(()=>{
    toast('License reactivated'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

function purgeLicense(id, label) {
  if (!confirm(`Permanently DELETE "${label}"? This cannot be undone.`)) return;
  api('DELETE',`/api/admin/licenses/${id}/purge`).then(()=>{
    toast('License deleted'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

function revokeMachine(id) {
  if (!confirm('Revoke this machine? It will self-uninstall on next check-in and cannot reinstall with this EXE.')) return;
  api('POST',`/api/admin/machines/${id}/revoke`).then(()=>{ toast('Machine revoked — will self-remove on next check-in'); loadMachines(); })
    .catch(e2 => toast(e2.message, true));
}

function unrevokeMachine(id) {
  if (!confirm('Un-revoke this machine? It will be able to check in and use the license again.')) return;
  api('POST',`/api/admin/machines/${id}/activate`).then(()=>{ toast('Machine re-allowed'); loadMachines(); })
    .catch(e2 => toast(e2.message, true));
}

function deleteMachine(id, label) {
  if (!confirm(`Delete machine "${label}"?\n\nThis removes it from the database and frees the seat.\nThe machine will not be notified — it will keep running until its next check-in fails or it is revoked first.`)) return;
  api('DELETE',`/api/admin/machines/${id}`).then(()=>{ toast('Machine deleted — seat freed'); loadMachines(); loadDashboard(); })
    .catch(e2 => toast(e2.message, true));
}

function dlExe(id) {
  fetch(`/api/admin/licenses/${id}/download`, { headers:{'X-Admin-Key':API_KEY} })
    .then(r => {
      if (!r.ok) return r.json().then(d => { throw new Error(d.error || 'Error '+r.status); });
      const cd = r.headers.get('content-disposition') || '';
      const m  = cd.match(/filename="?([^";\r\n]+)"?/);
      return r.blob().then(blob => ({ blob, fname: m ? m[1] : 'WdpMgr.exe' }));
    })
    .then(({blob, fname}) => {
      const url  = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url; link.download = fname; link.click();
      URL.revokeObjectURL(url); toast(fname + ' downloaded');
    }).catch(e2 => toast(e2.message, true));
}

// ── Settings ──────────────────────────────────────────────────────────────────
function loadSettings() {
  api('GET','/api/admin/settings').then(d => {
    const urlInput = document.getElementById('server-url-input');
    if (d.serverUrl) {
      urlInput.value = d.serverUrl;
    } else if (d.detectedUrl) {
      urlInput.value = d.detectedUrl;
      // Auto-save the detected URL so it's embedded in all EXEs
      api('POST','/api/admin/settings',{serverUrl: d.detectedUrl}).catch(()=>{});
    }
    const exSt = document.getElementById('exe-status');
    if (d.exeUploaded) {
      exSt.innerHTML = `<span style="color:var(--green)">✓ WdpMgr_base.exe ready</span> <span class="muted">(${(d.exeSize/1024).toFixed(1)} KB)</span>`;
    } else {
      exSt.innerHTML = '<span style="color:var(--amber)">⚠ No base EXE found — upload WdpMgr.exe to enable license downloads.</span>';
    }
    if (d.adminKey) { document.getElementById('adminkey-area').value = d.adminKey; }
  }).catch(err => toast(err.message, true));
}

function saveServerUrl() {
  const url = document.getElementById('server-url-input').value.trim();
  if (!url) { toast('Enter a server URL', true); return; }
  api('POST','/api/admin/settings',{serverUrl: url}).then(()=>{
    toast('Server URL saved');
  }).catch(err => toast(err.message, true));
}

function uploadExe() {
  const fi = document.getElementById('exe-file-input');
  if (!fi.files || !fi.files[0]) { toast('Select a .exe file first', true); return; }
  const fd = new FormData();
  fd.append('exe', fi.files[0]);
  const exSt = document.getElementById('exe-status');
  exSt.textContent = 'Uploading…';
  fetch('/api/admin/exe/upload', {
    method: 'POST',
    headers: { 'X-Admin-Key': API_KEY },
    body: fd
  }).then(r => r.json().then(d => {
    if (!r.ok) throw new Error(d.error || 'Upload failed');
    exSt.innerHTML = `<span style="color:var(--green)">✓ WdpMgr_base.exe uploaded (${(d.size/1024).toFixed(1)} KB)</span>`;
    toast('EXE uploaded successfully');
    fi.value = '';
  })).catch(err => { exSt.textContent = 'Upload failed'; toast(err.message, true); });
}

function toggleAdminKey() {
  const el = document.getElementById('adminkey-area');
  el.type = el.type === 'password' ? 'text' : 'password';
}
function copyAdminKey() {
  const v = document.getElementById('adminkey-area').value;
  if (!v) { toast('Nothing to copy', true); return; }
  navigator.clipboard.writeText(v).then(() => toast('Copied'));
}
function toggleNewMasterKey() {
  const el = document.getElementById('newmasterkey-input');
  el.type = el.type === 'password' ? 'text' : 'password';
}
function changeMasterKey() {
  const newKey = document.getElementById('newmasterkey-input').value;
  if (!newKey || newKey.length < 8) { toast('Key must be at least 8 characters', true); return; }
  if (!confirm('Change master key? You will need the new key to log in next time.')) return;
  api('POST', '/api/admin/settings/master-key', { key: newKey }).then(() => {
    toast('Master key updated');
    document.getElementById('newmasterkey-input').value = '';
    document.getElementById('adminkey-area').value = newKey;
    API_KEY = newKey;
    sessionStorage.setItem('wdp_key', newKey);
  }).catch(e2 => toast(e2.message, true));
}

function loadPubKey() {
  api('GET','/api/admin/publickey').then(d => {
    document.getElementById('pubkey-area').value = d.publicKeyXml || '';
    toast('Public key loaded');
  }).catch(e2 => toast(e2.message, true));
}

function copyPubKey() {
  const v = document.getElementById('pubkey-area').value;
  if (!v) { toast('Load the key first', true); return; }
  navigator.clipboard.writeText(v).then(()=>toast('Copied'));
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function e(s) {
  return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function badge(status) {
  const m = { active:'badge-active', expired:'badge-expired', revoked:'badge-revoked', offline:'badge-offline' };
  return `<span class="badge ${m[status]||''}">${status}</span>`;
}

function today() { return new Date().toISOString().slice(0,10); }

function hoursExpired(activatedAt, durationHours) {
  const exp = new Date(activatedAt);
  exp.setTime(exp.getTime() + durationHours * 3600000);
  return new Date() > exp;
}

function fmtHours(h) {
  if (h >= 48) return Math.floor(h/24) + 'd ' + (h%24 ? (h%24)+'h' : '');
  return h + 'h';
}

function fmtMins(m) {
  if (m >= 2880) return Math.floor(m/1440) + 'd ' + (Math.floor((m%1440)/60) ? Math.floor((m%1440)/60)+'h' : '');
  if (m >= 60)   return Math.floor(m/60) + 'h ' + (m%60 ? (m%60)+'m' : '');
  if (m > 0)     return m + 'm';
  return 'Expired';
}

// Format milliseconds remaining into a colour-coded HTML countdown string.
// Used for live second-precision countdowns in both Licenses and Machines tables.
function fmtMs(ms, expiryDisplay) {
  const expNote = expiryDisplay ? ` <span class="muted small">(${expiryDisplay})</span>` : '';
  if (ms <= 0) return `<span style="color:var(--red)">Expired${expNote}</span>`;
  const totalSec = Math.floor(ms / 1000);
  const days  = Math.floor(totalSec / 86400);
  const hours = Math.floor((totalSec % 86400) / 3600);
  const mins  = Math.floor((totalSec % 3600) / 60);
  const secs  = totalSec % 60;
  let text, color;
  if (days >= 2)  { text = days + 'd ' + hours + 'h';       color = 'var(--green)'; }
  else if (days)  { text = days + 'd ' + hours + 'h ' + mins + 'm'; color = 'var(--amber)'; }
  else if (hours) { text = hours + 'h ' + mins + 'm';        color = 'var(--amber)'; }
  else if (mins)  { text = mins + 'm ' + String(secs).padStart(2,'0') + 's'; color = 'var(--amber)'; }
  else            { text = secs + 's';                       color = 'var(--red)'; }
  const tip = expiryDisplay ? ` title="Expires ${expiryDisplay}"` : '';
  return `<span style="color:${color}"${tip}>${text}</span>`;
}

// ── Live countdown ticker (updates every second for Licenses + Machines) ──────
let _countdownTimer = null;
function _tickCountdowns() {
  document.querySelectorAll('[data-expires-ms]').forEach(el => {
    const ms = Number(el.dataset.expiresMs) - Date.now();
    el.innerHTML = fmtMs(ms, el.dataset.expiryDisplay || '');
  });
}
function startCountdowns() {
  stopCountdowns();
  _tickCountdowns();
  _countdownTimer = setInterval(_tickCountdowns, 1000);
}
function stopCountdowns() {
  if (_countdownTimer) { clearInterval(_countdownTimer); _countdownTimer = null; }
}

function noData(cols) { return `<tr><td colspan="${cols}" class="empty">No data yet.</td></tr>`; }

let toastT;
function toast(msg, err=false) {
  const el = document.getElementById('toast');
  el.textContent = msg; el.className = 'toast'+(err?' error':'');
  clearTimeout(toastT); toastT = setTimeout(()=>el.classList.add('hidden'), 4000);
}

function openModal(id)  { document.getElementById(id).classList.remove('hidden'); }
function closeModal(id) { document.getElementById(id).classList.add('hidden'); }
document.querySelectorAll('.modal-overlay').forEach(el =>
  el.addEventListener('click', ev => { if (ev.target===el) el.classList.add('hidden'); }));
