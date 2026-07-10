// WdpMgr Admin Panel — full featured JS

let API_KEY  = '';
let USERNAME = '';
let revokeId = '', revokeLabel_ = '';

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
  document.getElementById('login-overlay').style.display = 'none';
  document.getElementById('app').classList.remove('hidden');
  document.getElementById('whoami').textContent = USERNAME;
  loadAll();
}

['login-user','login-pass'].forEach(id =>
  document.getElementById(id).addEventListener('keydown', e => { if (e.key==='Enter') doLogin(); }));

function doLogout() {
  API_KEY = ''; USERNAME = '';
  document.getElementById('app').classList.add('hidden');
  document.getElementById('login-overlay').style.display = '';
  document.getElementById('login-pass').value = '';
  document.getElementById('login-error').textContent = '';
}

// ── Navigation ────────────────────────────────────────────────────────────────
function nav(name, el) {
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
  const view = document.getElementById('view-' + name);
  if (view) view.classList.add('active');
  if (el)  el.classList.add('active');
  const loaders = { dashboard: loadDashboard, licenses: loadLicenses, machines: loadMachines, apps: loadApps, users: loadUsers };
  if (loaders[name]) loaders[name]();
}

// ── API helper ────────────────────────────────────────────────────────────────
function api(method, path, body) {
  const opts = { method, headers: { 'X-Admin-Key': API_KEY, 'Content-Type': 'application/json' } };
  if (body != null) opts.body = JSON.stringify(body);
  return fetch(path, opts).then(r => r.json().then(d => {
    if (!r.ok) throw new Error(d.error || 'Error ' + r.status);
    return d;
  }));
}

function loadAll() { loadDashboard(); }

// ── Dashboard ─────────────────────────────────────────────────────────────────
function loadDashboard() {
  api('GET','/api/admin/stats').then(d => {
    document.getElementById('s-total').textContent   = d.totalLicenses;
    document.getElementById('s-active').textContent  = d.activeLicenses;
    document.getElementById('s-expired').textContent = d.expiredLicenses;
    document.getElementById('s-revoked').textContent = d.revokedLicenses;
    document.getElementById('s-machines').textContent= d.activeMachines;
    document.getElementById('s-apps').textContent    = d.totalApps;
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
function loadLicenses() {
  api('GET','/api/admin/licenses').then(list => {
    const tbody = document.getElementById('lic-body');
    if (!list.length) { tbody.innerHTML = '<tr><td colspan="8" class="empty">No licenses yet.</td></tr>'; return; }
    tbody.innerHTML = list.map(l => {
      const status = l.revoked ? 'revoked'
        : (l.type==='temp' && l.expiry && l.expiry < today()) ? 'expired'
        : (l.type==='days' && l.activatedAt && daysExpired(l.activatedAt, l.durationDays)) ? 'expired'
        : 'active';
      const expCol = l.type==='lifetime' ? '∞ Lifetime'
        : l.type==='temp'    ? e(l.expiry)
        : l.type==='days'    ? `${l.durationDays} days${l.activatedAt ? ' (activated '+l.activatedAt+')':' (not yet activated)'}`
        : `${l.maxActivations} seats (HR)`;
      return `<tr>
        <td><strong>${e(l.label)}</strong>${l.notes?'<br><small class="muted">'+e(l.notes)+'</small>':''}</td>
        <td><span class="badge badge-type-${l.type}">${l.type}</span></td>
        <td class="muted">${e(l.appName||'—')}</td>
        <td class="muted">${e(l.issued)}</td>
        <td class="muted">${expCol}</td>
        <td class="muted">${l.activeSeats} / ${l.maxActivations}</td>
        <td>${badge(status)}</td>
        <td>
          ${!l.revoked?`<button class="btn-icon" onclick='dlLic("${l.id}")'>⬇ .lic</button>
          <button class="btn-icon danger" onclick='openRevoke("${l.id}","${e(l.label)}")'>✕ Revoke</button>`
          :'<span class="muted small">revoked</span>'}
        </td></tr>`;
    }).join('');
  }).catch(e2 => toast(e2.message, true));
}

// ── Machines ──────────────────────────────────────────────────────────────────
function loadMachines() {
  api('GET','/api/admin/machines').then(list => {
    const tbody = document.getElementById('mach-body');
    if (!list.length) { tbody.innerHTML = '<tr><td colspan="9" class="empty">No machines have checked in yet.</td></tr>'; return; }
    tbody.innerHTML = list.map(m => `<tr>
      <td>${e(m.hostname||'—')}</td>
      <td class="mono">${e(m.windowsUser||'—')}</td>
      <td>${e(m.licenseLabel||m.licenseId)}</td>
      <td class="muted">${e(m.ipAddress||'—')}</td>
      <td class="muted">${e(m.firstSeen)}</td>
      <td class="muted">${e(m.lastSeen||'—')}</td>
      <td><span class="mono fp" title="${e(m.seatKey)}">${e(m.seatKey.substring(0,14))}…</span></td>
      <td>${badge(m.status)}</td>
      <td>${m.status!=='revoked'?`<button class="btn-icon danger" onclick='revokeMachine("${m.id}")'>✕</button>`:''}</td>
    </tr>`).join('');
  }).catch(e2 => toast(e2.message, true));
}

// ── Apps ──────────────────────────────────────────────────────────────────────
function loadApps() {
  api('GET','/api/admin/apps').then(list => {
    const tbody = document.getElementById('apps-body');
    if (!list.length) { tbody.innerHTML = '<tr><td colspan="4" class="empty">No apps registered yet.</td></tr>'; return; }
    tbody.innerHTML = list.map(a => `<tr>
      <td><strong>${e(a.name)}</strong></td>
      <td class="muted">${e(a.description||'—')}</td>
      <td class="muted">${e(a.createdAt)}</td>
      <td><button class="btn-icon danger" onclick='deleteApp("${a.id}","${e(a.name)}")'>✕ Delete</button></td>
    </tr>`).join('');
  }).catch(e2 => toast(e2.message, true));
}

function createApp() {
  const name = document.getElementById('na-name').value.trim();
  const desc = document.getElementById('na-desc').value.trim();
  if (!name) { toast('Name required', true); return; }
  api('POST','/api/admin/apps',{name,description:desc}).then(()=>{
    closeModal('modal-app'); toast('App registered'); loadApps();
  }).catch(e2 => toast(e2.message, true));
}

function deleteApp(id, name) {
  if (!confirm(`Delete app "${name}"?`)) return;
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

// ── License CRUD ──────────────────────────────────────────────────────────────
function openLicModal() {
  document.getElementById('nl-label').value  = '';
  document.getElementById('nl-type').value   = 'lifetime';
  document.getElementById('nl-expiry').value = '';
  document.getElementById('nl-days').value   = '30';
  document.getElementById('nl-maxact').value = '1';
  document.getElementById('nl-notes').value  = '';
  onLicTypeChange();
  // Populate app dropdown
  api('GET','/api/admin/apps').then(apps => {
    const sel = document.getElementById('nl-app');
    sel.innerHTML = '<option value="">— any app —</option>' +
      apps.map(a => `<option value="${a.id}">${e(a.name)}</option>`).join('');
  }).catch(()=>{});
  nav('licenses', document.querySelector('[data-view=licenses]'));
  openModal('modal-lic');
}

function onLicTypeChange() {
  const t = document.getElementById('nl-type').value;
  document.getElementById('nl-row-expiry').classList.toggle('hidden', t !== 'temp');
  document.getElementById('nl-row-days').classList.toggle('hidden',   t !== 'days');
  document.getElementById('nl-seats-label').textContent =
    t === 'hr' ? 'Max Seats (unique Windows users)' : 'Max Machines';
}

function createLicense() {
  const label  = document.getElementById('nl-label').value.trim();
  const type   = document.getElementById('nl-type').value;
  const expiry = document.getElementById('nl-expiry').value;
  const days   = parseInt(document.getElementById('nl-days').value) || 0;
  const maxAct = parseInt(document.getElementById('nl-maxact').value) || 1;
  const appId  = document.getElementById('nl-app').value;
  const notes  = document.getElementById('nl-notes').value.trim();
  if (!label) { toast('Label required', true); return; }
  if (type==='temp' && !expiry) { toast('Expiry date required', true); return; }
  if (type==='days' && days < 1) { toast('Duration must be >= 1', true); return; }
  api('POST','/api/admin/licenses',{label,type,expiry,durationDays:days,maxActivations:maxAct,appId,notes}).then(()=>{
    closeModal('modal-lic'); toast('License created'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

function openRevoke(id, label) {
  revokeId = id; revokeLabel_ = label;
  document.getElementById('rv-label').textContent = label;
  openModal('modal-revoke');
}

function confirmRevoke() {
  api('DELETE',`/api/admin/licenses/${revokeId}`).then(()=>{
    closeModal('modal-revoke'); toast('License revoked'); loadLicenses(); loadDashboard();
  }).catch(e2 => toast(e2.message, true));
}

function revokeMachine(id) {
  if (!confirm('Revoke this machine?')) return;
  api('DELETE',`/api/admin/machines/${id}`).then(()=>{ toast('Machine revoked'); loadMachines(); })
    .catch(e2 => toast(e2.message, true));
}

function dlLic(id) {
  fetch(`/api/admin/licenses/${id}/download`, { headers:{'X-Admin-Key':API_KEY} })
    .then(r => { if (!r.ok) throw new Error('Error '+r.status); return r.blob(); })
    .then(blob => {
      const url  = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url; link.download = 'wdp.lic'; link.click();
      URL.revokeObjectURL(url); toast('wdp.lic downloaded');
    }).catch(e2 => toast(e2.message, true));
}

// ── Settings ──────────────────────────────────────────────────────────────────
function loadAdminKey() {
  api('GET','/api/admin/settings').then(d => {
    document.getElementById('adminkey-area').value = d.adminKey || '';
    toast('Admin key loaded');
  }).catch(err => toast(err.message, true));
}
function toggleAdminKey() {
  const el = document.getElementById('adminkey-area');
  el.type = el.type === 'password' ? 'text' : 'password';
}
function copyAdminKey() {
  const v = document.getElementById('adminkey-area').value;
  if (!v) { toast('Load the key first', true); return; }
  navigator.clipboard.writeText(v).then(() => toast('Copied'));
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
  const m = { active:'badge-active', expired:'badge-expired', revoked:'badge-revoked' };
  return `<span class="badge ${m[status]||''}">${status}</span>`;
}

function today() { return new Date().toISOString().slice(0,10); }

function daysExpired(activatedAt, durationDays) {
  const exp = new Date(activatedAt);
  exp.setDate(exp.getDate() + durationDays);
  return new Date() > exp;
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
