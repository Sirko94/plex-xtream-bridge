/* ── XtreamBridge Web UI ───────────────────────────────────────────────────── */

// ── Navigation ────────────────────────────────────────────────────────────────
document.querySelectorAll('.nav-links a').forEach(link => {
  link.addEventListener('click', e => {
    e.preventDefault();
    const target = link.dataset.section;
    document.querySelectorAll('.section').forEach(s => s.classList.remove('active'));
    document.querySelectorAll('.nav-links a').forEach(l => l.classList.remove('active'));
    document.getElementById(target)?.classList.add('active');
    link.classList.add('active');
  });
});

// ── API helpers ───────────────────────────────────────────────────────────────
async function api(method, path, body) {
  const opts = {
    method,
    headers: { 'Content-Type': 'application/json' }
  };
  if (body) opts.body = JSON.stringify(body);
  const res = await fetch('/api' + path, opts);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

function showMessage(id, text, type = 'info') {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = text;
  el.className = `message ${type}`;
  el.classList.remove('hidden');
  setTimeout(() => el.classList.add('hidden'), 5000);
}

// ── Load config into UI ───────────────────────────────────────────────────────
async function loadConfig() {
  try {
    const cfg = await api('GET', '/config');

    // Server
    document.getElementById('server-url').value  = cfg.server?.baseUrl  ?? '';
    document.getElementById('server-user').value = cfg.server?.username ?? '';
    document.getElementById('server-pass').value = cfg.server?.password ?? '';
    document.getElementById('server-ua').value   = cfg.bridge?.userAgent ?? '';

    // Bridge
    document.getElementById('bridge-url').value    = cfg.bridge?.publicBaseUrl ?? '';
    document.getElementById('bridge-name').value   = cfg.bridge?.deviceName    ?? 'XtreamBridge';
    document.getElementById('bridge-tuners').value = cfg.bridge?.tunerCount    ?? 6;
    document.getElementById('bridge-livetv').checked = cfg.bridge?.enableLiveTv ?? true;
    document.getElementById('bridge-strm').checked   = cfg.bridge?.enableStrmGeneration ?? true;

    // Sync
    const s = cfg.sync ?? {};
    document.getElementById('sync-schedule').value    = s.scheduleType ?? 'Interval';
    document.getElementById('sync-interval').value    = s.refreshIntervalHours ?? 6;
    document.getElementById('sync-hour').value        = s.dailyHour ?? 3;
    document.getElementById('sync-minute').value      = s.dailyMinute ?? 0;
    document.getElementById('sync-nfo').checked       = s.generateNfoFiles ?? true;
    document.getElementById('sync-adult').checked     = s.includeAdultChannels ?? false;
    document.getElementById('sync-cleanup').checked   = s.cleanupOrphans ?? true;
    document.getElementById('sync-clean-names').checked = s.enableChannelNameCleaning ?? true;
    document.getElementById('sync-ext').value         = s.liveStreamExtension ?? 'ts';
    document.getElementById('sync-parallelism').value = s.syncParallelism ?? 10;
    document.getElementById('sync-threshold').value   = Math.round((s.orphanSafetyThreshold ?? 0.20) * 100);
    document.getElementById('sync-remove-terms').value = s.channelRemoveTerms ?? '';
    document.getElementById('sync-delay').value       = s.requestDelayMs ?? 50;
    document.getElementById('sync-retries').value     = s.maxRetries ?? 3;
    document.getElementById('sync-retry-delay').value = s.retryDelayMs ?? 1000;

    // Live — category filters
    _selectedLive   = s.liveCategoryFilter   ?? [];
    _selectedVod    = s.vodCategoryFilter    ?? [];
    _selectedSeries = s.seriesCategoryFilter ?? [];
    document.getElementById('channel-overrides').value = s.channelOverrides ?? '';

    // Update Plex URLs
    const base = cfg.bridge?.publicBaseUrl?.replace(/\/$/, '') || window.location.origin;
    document.getElementById('plex-discover-url').textContent = `${base}/discover.json`;
    document.getElementById('plex-epg-url').textContent      = `${base}/epg.xml`;

    updateScheduleUI();
  } catch (err) {
    console.error('loadConfig:', err);
  }
}

// ── Collect config from UI ────────────────────────────────────────────────────
function collectConfig() {
  return {
    server: {
      baseUrl:  document.getElementById('server-url').value.trim(),
      username: document.getElementById('server-user').value.trim(),
      password: document.getElementById('server-pass').value
    },
    bridge: {
      publicBaseUrl:         document.getElementById('bridge-url').value.trim(),
      deviceName:            document.getElementById('bridge-name').value.trim(),
      tunerCount:            parseInt(document.getElementById('bridge-tuners').value, 10),
      userAgent:             document.getElementById('server-ua').value.trim(),
      enableLiveTv:          document.getElementById('bridge-livetv').checked,
      enableStrmGeneration:  document.getElementById('bridge-strm').checked,
      deviceId: ''  // preserved server-side
    },
    sync: {
      scheduleType:              document.getElementById('sync-schedule').value,
      refreshIntervalHours:      parseInt(document.getElementById('sync-interval').value, 10),
      dailyHour:                 parseInt(document.getElementById('sync-hour').value, 10),
      dailyMinute:               parseInt(document.getElementById('sync-minute').value, 10),
      generateNfoFiles:          document.getElementById('sync-nfo').checked,
      includeAdultChannels:      document.getElementById('sync-adult').checked,
      cleanupOrphans:            document.getElementById('sync-cleanup').checked,
      enableChannelNameCleaning: document.getElementById('sync-clean-names').checked,
      liveStreamExtension:       document.getElementById('sync-ext').value,
      syncParallelism:           parseInt(document.getElementById('sync-parallelism').value, 10),
      orphanSafetyThreshold:     parseInt(document.getElementById('sync-threshold').value, 10) / 100,
      channelRemoveTerms:        document.getElementById('sync-remove-terms').value,
      requestDelayMs:            parseInt(document.getElementById('sync-delay').value, 10),
      maxRetries:                parseInt(document.getElementById('sync-retries').value, 10),
      retryDelayMs:              parseInt(document.getElementById('sync-retry-delay').value, 10),
      liveCategoryFilter:        _selectedLive,
      vodCategoryFilter:         _selectedVod,
      seriesCategoryFilter:      _selectedSeries,
      channelOverrides:          document.getElementById('channel-overrides').value
    }
  };
}

// ── Status ────────────────────────────────────────────────────────────────────
async function loadStatus() {
  try {
    const s = await api('GET', '/status');
    document.getElementById('last-sync').textContent =
      s.lastSync ? new Date(s.lastSync).toLocaleString('fr-FR') : 'Jamais';
    document.getElementById('stat-live').textContent   = s.liveChannels ?? 0;
    document.getElementById('stat-movies').textContent = s.syncedMovies ?? 0;
    document.getElementById('stat-series').textContent = s.syncedSeries ?? 0;
  } catch (err) {
    document.getElementById('last-sync').textContent = 'Erreur';
  }
}

// ── Button handlers ───────────────────────────────────────────────────────────

// Sync now
document.getElementById('btn-sync').addEventListener('click', async () => {
  const btn = document.getElementById('btn-sync');
  btn.disabled = true;
  btn.innerHTML = '<span class="spinning">↻</span> Synchronisation…';
  try {
    await api('POST', '/sync/trigger');
    showMessage('sync-message', 'Synchronisation démarrée en arrière-plan.', 'info');
    setTimeout(loadStatus, 3000);
  } catch (e) {
    showMessage('sync-message', 'Erreur : ' + e.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
      <polyline points="23 4 23 10 17 10"/><path d="M1 14l4.64 4.36A9 9 0 0020.49 15"/>
    </svg> Synchroniser maintenant`;
  }
});

document.getElementById('btn-refresh-status').addEventListener('click', loadStatus);

// Test connection
document.getElementById('btn-test').addEventListener('click', async () => {
  const btn = document.getElementById('btn-test');
  btn.disabled = true;
  btn.textContent = 'Test en cours…';
  try {
    const creds = {
      baseUrl:  document.getElementById('server-url').value.trim(),
      username: document.getElementById('server-user').value.trim(),
      password: document.getElementById('server-pass').value
    };
    const res = await api('POST', '/config/test', creds);
    showMessage('test-result', res.message ?? res.error, res.success ? 'success' : 'error');
  } catch (e) {
    showMessage('test-result', 'Erreur réseau : ' + e.message, 'error');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Tester la connexion';
  }
});

// Save server section
document.getElementById('btn-save-server').addEventListener('click', () => saveConfig('test-result'));

// Save bridge section
document.getElementById('btn-save-bridge').addEventListener('click', () => saveConfig('bridge-message'));

// Save sync section
document.getElementById('btn-save-sync').addEventListener('click', () => saveConfig('sync-save-message'));

// Save live TV section
document.getElementById('btn-save-live').addEventListener('click', () => saveConfig('live-message'));

async function saveConfig(feedbackId) {
  try {
    await api('POST', '/config', collectConfig());
    showMessage(feedbackId, 'Configuration enregistrée ✓', 'success');
    loadConfig(); // reload to confirm
  } catch (e) {
    showMessage(feedbackId, 'Erreur : ' + e.message, 'error');
  }
}

// ── Show/hide password ────────────────────────────────────────────────────────
document.querySelector('.toggle-pass').addEventListener('click', () => {
  const inp = document.getElementById('server-pass');
  inp.type = inp.type === 'password' ? 'text' : 'password';
});

// ── Schedule UI toggle ────────────────────────────────────────────────────────
function updateScheduleUI() {
  const mode = document.getElementById('sync-schedule').value;
  document.getElementById('interval-group').classList.toggle('hidden', mode === 'Daily');
  document.getElementById('daily-group').classList.toggle('hidden', mode !== 'Daily');
}
document.getElementById('sync-schedule').addEventListener('change', updateScheduleUI);

// ── Copy buttons ──────────────────────────────────────────────────────────────
document.querySelectorAll('.copy-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    const text = document.getElementById(btn.dataset.target)?.textContent;
    if (text) navigator.clipboard.writeText(text).then(() => {
      const orig = btn.textContent;
      btn.textContent = '✓ Copié';
      setTimeout(() => btn.textContent = orig, 1500);
    });
  });
});

// ── Category filters ──────────────────────────────────────────────────────────
let _selectedLive   = [];
let _selectedVod    = [];
let _selectedSeries = [];

function renderCategories(containerId, cats, selected, onToggle) {
  const el = document.getElementById(containerId);
  if (!cats || cats.length === 0) {
    el.innerHTML = '<em>Aucune catégorie trouvée.</em>';
    return;
  }
  el.innerHTML = '';
  cats.forEach(cat => {
    const chip = document.createElement('span');
    chip.className = 'cat-chip' + (selected.includes(cat.categoryId) ? ' selected' : '');
    chip.innerHTML = `${cat.categoryName}`;
    chip.addEventListener('click', () => {
      const idx = selected.indexOf(cat.categoryId);
      if (idx === -1) selected.push(cat.categoryId);
      else selected.splice(idx, 1);
      chip.classList.toggle('selected', selected.includes(cat.categoryId));
      onToggle(selected);
    });
    el.appendChild(chip);
  });
}

document.getElementById('btn-load-live').addEventListener('click', async () => {
  try {
    const cats = await api('GET', '/categories/live');
    renderCategories('cat-live-list', cats, _selectedLive, v => _selectedLive = v);
  } catch (e) { document.getElementById('cat-live-list').innerHTML = '<em class="error">Erreur : ' + e.message + '</em>'; }
});

document.getElementById('btn-load-vod').addEventListener('click', async () => {
  try {
    const cats = await api('GET', '/categories/vod');
    renderCategories('cat-vod-list', cats, _selectedVod, v => _selectedVod = v);
  } catch (e) { document.getElementById('cat-vod-list').innerHTML = '<em class="error">Erreur : ' + e.message + '</em>'; }
});

document.getElementById('btn-load-series').addEventListener('click', async () => {
  try {
    const cats = await api('GET', '/categories/series');
    renderCategories('cat-series-list', cats, _selectedSeries, v => _selectedSeries = v);
  } catch (e) { document.getElementById('cat-series-list').innerHTML = '<em class="error">Erreur : ' + e.message + '</em>'; }
});

// ── Init ──────────────────────────────────────────────────────────────────────
loadConfig();
loadStatus();
setInterval(loadStatus, 30_000); // auto-refresh status every 30s
