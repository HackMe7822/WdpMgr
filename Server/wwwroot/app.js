// WdpMgr Admin Panel — wired to real API endpoints

let ADMIN_KEY = '';
let revokeTargetId = '';
let revokeTargetLabel = '';
let keyVisible = false;

// ── Auth ──────────────────────────────────────────────────────────────────────
function doLogin() {
  const key = document.getElementById('key-input').value.trim();
  if (!key) { document.getElementById('login-error').textContent = 'Enter the admin key.'; return; }
  // Test the key by hitting /api/admin/stats
  fetch('/api/admin/stats', { headers: { 'X-Admin-Key': key } })
    .then(r => {
      if (r.status === 401) throw new Error('Invalid admin key.');
      if (!r.ok) throw new Error('Server error ' + r.status);
      return r.json();
    })
    .then(() => {
      ADMIN_KEY = key;
      document.getElementById('login-overlay').style.display = 'none';
      document.getElementById('app').classList.remove('hidden');
      document.getElementById('key-display').textContent = key;
      loadAll();
    })
    .catch(e => { document.getElementById('login-error').textContent = e.message; });
}

document.getElementById('key-input').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });

function doLogout() {
  ADMIN_KEY = '';
  document.getElementById('app').classList.add('hidden');
  document.getElementById('login-overlay').style.display = '';
  document.getElementById('key-input').value = '';
  document.getElementById('login-error').textContent = '';
}

// ── Navigation ────────────────────────────────────────────────────────────────
function switchView(name, el) {
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
  document.getElementById('view-' + name).classList.add('active');
  if (el) el.classList.add('active');
  if (name === 'dashboard') loadDashboard();
  if (name === 'licenses')  loadLicenses();
  if (name === 'machines')  loadMachines();
}

// ── API helper ────────────────────────────────────────────────────────────────
function api(method, path, body) {
  const opts = {
    method,
    headers: { 'X-Admin-Key': ADMIN_KEY, 'Content-Type': 'application/json' }
  };
  if (body) opts.body = JSON.stringify(body);
  return fetch(path, opts).then(r => {
    if (!r.ok) return r.json().then(d => Promise.reject(d.error || 'Server error ' + r.status));
    return r.json();
  });
}

// ── Load everything ───────────────────────────────────────────────────────────
function loadAll() { loadDashboard(); }

// ── Dashboard ─────────────────────────────────────────────────────────────────
function loadDashboard() {
  api('GET', '/api/admin/stats').then(d => {
    document.getElementById('stat-total').textContent    = d.total;
    document.getElementById('stat-active').textContent   = d.active;
    document.getElementById('stat-expired').textContent  = d.expired;
    document.getElementById('stat-revoked').textContent  = d.revoked;
    document.getElementById('stat-machines').textContent = d.machines;
  }).catch(e => toast('Stats error: ' + e, true));

  api('GET', '/api/admin/machines').then(machines => {
    const tbody = document.getElementById('recent-machines');
    const recent = machines.slice(0, 5);
    if (!recent.length) { tbody.innerHTML = '<tr><td colspan="4" style="color:var(--muted);text-align:center">No machines yet</td></tr>'; return; }
    tbody.innerHTML = recent.map(m => `
      <tr>
        <td>${esc(m.hostname || '—')}</td>
        <td>${esc(m.licenseLabel || m.licenseId)}</td>
        <td style="color:var(--muted)">${esc(m.lastSeen || '—')}</td>
        <td>${badge(m.status)}</td>
      </tr>`).join('');
  }).catch(() => {});
}

// ── Licenses ──────────────────────────────────────────────────────────────────
function loadLicenses() {
  api('GET', '/api/admin/licenses').then(licenses => {
    const tbody = document.getElementById('licenses-body');
    if (!licenses.length) {
      tbody.innerHTML = '<tr><td colspan="7" style="color:var(--muted);text-align:center;padding:24px">No licenses yet. Click "+ New License" to create one.</td></tr>';
      return;
    }
    tbody.innerHTML = licenses.map(l => `
      <tr>
        <td><strong>${esc(l.label)}</strong>${l.notes ? '<br><small style="color:var(--muted)">' + esc(l.notes) + '</small>' : ''}</td>
        <td><span class="badge badge-${l.type}">${l.type}</span></td>
        <td style="color:var(--muted)">${esc(l.issued)}</td>
        <td style="color:var(--muted)">${l.type === 'temp' ? esc(l.expiry) : '∞'}</td>
        <td style="color:var(--muted)">${l.maxActivations}</td>
        <td>${l.revoked ? badge('revoked') : (l.type === 'temp' && l.expiry && l.expiry < today() ? badge('expired') : badge('active'))}</td>
        <td>
          ${!l.revoked ? `
            <button class="btn-icon" onclick='downloadLic("${l.id}")' title="Download .lic">⬇ .lic</button>
            <button class="btn-icon danger" onclick='openRevoke("${l.id}","${esc(l.label)}")' title="Revoke">✕</button>
          ` : '<span style="color:var(--muted);font-size:12px">revoked</span>'}
        </td>
      </tr>`).join('');
  }).catch(e => toast('Error loading licenses: ' + e, true));
}

// ── Machines ──────────────────────────────────────────────────────────────────
function loadMachines() {
  api('GET', '/api/admin/machines').then(machines => {
    const tbody = document.getElementById('machines-body');
    if (!machines.length) {
      tbody.innerHTML = '<tr><td colspan="8" style="color:var(--muted);text-align:center;padding:24px">No machines have checked in yet.</td></tr>';
      return;
    }
    tbody.innerHTML = machines.map(m => `
      <tr>
        <td>${esc(m.hostname || '—')}</td>
        <td>${esc(m.licenseLabel || m.licenseId)}</td>
        <td style="color:var(--muted)">${esc(m.ipAddress || '—')}</td>
        <td style="color:var(--muted)">${esc(m.firstSeen)}</td>
        <td style="color:var(--muted)">${esc(m.lastSeen || '—')}</td>
        <td><span class="mono" title="${esc(m.fingerprint)}">${esc(m.fingerprint.substring(0,16))}…</span></td>
        <td>${badge(m.status)}</td>
        <td>
          ${m.status !== 'revoked' ? `<button class="btn-icon danger" onclick='revokeMachine("${m.id}")' title="Revoke machine">✕</button>` : ''}
        </td>
      </tr>`).join('');
  }).catch(e => toast('Error loading machines: ' + e, true));
}

// ── Create license ────────────────────────────────────────────────────────────
function openCreateModal() {
  document.getElementById('new-label').value  = '';
  document.getElementById('new-type').value   = 'lifetime';
  document.getElementById('new-expiry').value = '';
  document.getElementById('new-notes').value  = '';
  document.getElementById('new-maxact').value = '1';
  toggleExpiry();
  openModal('modal-create');
  switchView('licenses', document.querySelector('[data-view=licenses]'));
}

function toggleExpiry() {
  const isTemp = document.getElementById('new-type').value === 'temp';
  document.getElementById('expiry-row').style.display = isTemp ? '' : 'none';
}

function createLicense() {
  const label  = document.getElementById('new-label').value.trim();
  const type   = document.getElementById('new-type').value;
  const expiry = document.getElementById('new-expiry').value;
  const notes  = document.getElementById('new-notes').value.trim();
  const maxAct = parseInt(document.getElementById('new-maxact').value) || 1;
  if (!label) { toast('Label is required', true); return; }
  if (type === 'temp' && !expiry) { toast('Expiry date required for temporary license', true); return; }
  api('POST', '/api/admin/licenses', { label, type, expiry, notes, maxActivations: maxAct })
    .then(() => {
      closeModal('modal-create');
      toast('License created');
      loadLicenses();
      loadDashboard();
    })
    .catch(e => toast('Error: ' + e, true));
}

// ── Revoke license ────────────────────────────────────────────────────────────
function openRevoke(id, label) {
  revokeTargetId    = id;
  revokeTargetLabel = label;
  document.getElementById('revoke-label').textContent = label;
  openModal('modal-revoke');
}

function confirmRevoke() {
  api('DELETE', '/api/admin/licenses/' + revokeTargetId)
    .then(() => {
      closeModal('modal-revoke');
      toast('License revoked — machine will be uninstalled on next check-in');
      loadLicenses();
      loadDashboard();
    })
    .catch(e => toast('Error: ' + e, true));
}

// ── Revoke machine ────────────────────────────────────────────────────────────
function revokeMachine(id) {
  if (!confirm('Revoke this machine? It will be uninstalled on next check-in.')) return;
  api('DELETE', '/api/admin/machines/' + id)
    .then(() => { toast('Machine revoked'); loadMachines(); })
    .catch(e => toast('Error: ' + e, true));
}

// ── Download .lic ─────────────────────────────────────────────────────────────
function downloadLic(id) {
  const a = document.createElement('a');
  a.href = '/api/admin/licenses/' + id + '/download';
  a.setAttribute('download', 'wdp.lic');
  a.setAttribute('data-key', ADMIN_KEY); // NOTE: header auth can't be set on anchor download
  // Fetch manually and trigger download so we can pass the auth header
  fetch('/api/admin/licenses/' + id + '/download', { headers: { 'X-Admin-Key': ADMIN_KEY } })
    .then(r => {
      if (!r.ok) throw new Error('Error ' + r.status);
      return r.blob();
    })
    .then(blob => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url; link.download = 'wdp.lic';
      link.click(); URL.revokeObjectURL(url);
      toast('wdp.lic downloaded');
    })
    .catch(e => toast('Download error: ' + e, true));
}

// ── Public key ────────────────────────────────────────────────────────────────
function loadPublicKey() {
  api('GET', '/api/admin/publickey')
    .then(d => {
      document.getElementById('pubkey-area').value = d.publicKeyXml || '';
      toast('Public key loaded');
    })
    .catch(e => toast('Error: ' + e, true));
}

function copyPubKey() {
  const v = document.getElementById('pubkey-area').value;
  if (!v) { toast('Load the key first', true); return; }
  navigator.clipboard.writeText(v).then(() => toast('Copied to clipboard'));
}

function toggleKey() {
  keyVisible = !keyVisible;
  document.getElementById('key-display').textContent = keyVisible ? ADMIN_KEY : '••••••••••••••••';
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function badge(status) {
  const map = { active:'badge-active', expired:'badge-expired', revoked:'badge-revoked',
                lifetime:'badge-lifetime', temp:'badge-temp' };
  return `<span class="badge ${map[status] || ''}">${status}</span>`;
}

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function today() { return new Date().toISOString().slice(0,10); }

let toastTimer;
function toast(msg, isError = false) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = 'toast' + (isError ? ' error' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add('hidden'), 3500);
}

function openModal(id)  { document.getElementById(id).classList.remove('hidden'); }
function closeModal(id) { document.getElementById(id).classList.add('hidden'); }

// Close modal on overlay click
document.querySelectorAll('.modal-overlay').forEach(el => {
  el.addEventListener('click', e => { if (e.target === el) el.classList.add('hidden'); });
});
