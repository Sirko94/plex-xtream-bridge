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
    document.getElementById('bridge-livetv').checked    = cfg.bridge?.enableLiveTv ?? true;
    document.getElementById('bridge-strm').checked      = cfg.bridge?.enableStrmGeneration ?? true;
    document.getElementById('bridge-output-path').value = cfg.bridge?.outputPath ?? '/output';

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
    document.getElementById('sync-parallelism').value = s.syncParallelism ?? 3;
    document.getElementById('sync-threshold').value   = Math.round((s.orphanSafetyThreshold ?? 0.20) * 100);
    document.getElementById('sync-remove-terms').value = s.channelRemoveTerms ?? '';
    document.getElementById('sync-delay').value       = s.requestDelayMs ?? 800;
    document.getElementById('sync-retries').value     = s.maxRetries ?? 5;
    document.getElementById('sync-retry-delay').value = s.retryDelayMs ?? 10000;

    // Snapshot / TMDb
    document.getElementById('sync-snapshot').checked     = s.enableSnapshotSync ?? true;
    document.getElementById('sync-tmdb-enabled').checked = s.enableMetadataLookup ?? false;
    document.getElementById('sync-tmdb-key').value       = s.tmdbApiKey ?? '';

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
      outputPath:            document.getElementById('bridge-output-path').value.trim() || '/output',
      deviceId: ''
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
      channelOverrides:          document.getElementById('channel-overrides').value,
      enableSnapshotSync:        document.getElementById('sync-snapshot').checked,
      enableMetadataLookup:      document.getElementById('sync-tmdb-enabled').checked,
      tmdbApiKey:                document.getElementById('sync-tmdb-key').value.trim()
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

// ── Sync progress polling ─────────────────────────────────────────────────────
const PHASE_LABELS = {
  idle:    'En attente',
  auth:    'Authentification…',
  live:    'Live TV…',
  movies:  'Films…',
  series:  'Séries…',
  epg:     'EPG…',
  done:    'Terminé'
};

let _pollInterval = null;

async function loadSyncProgress() {
  try {
    const p = await api('GET', '/sync/status');
    const card = document.getElementById('sync-progress-card');

    if (p.isRunning) {
      card.classList.remove('hidden');

      const label = PHASE_LABELS[p.phase] ?? p.phase;
      document.getElementById('progress-phase-label').textContent = label;

      const pct = p.progressPercent ?? 0;
      document.getElementById('progress-bar').style.width = pct + '%';

      const total = p.totalItems > 0 ? p.totalItems : '?';
      document.getElementById('progress-count').textContent = `${p.itemsProcessed} / ${total}`;
      document.getElementById('progress-item').textContent = p.currentItem ?? '';

      // Start polling if not already polling
      if (!_pollInterval) {
        _pollInterval = setInterval(loadSyncProgress, 2000);
      }
    } else {
      // Sync finished
      card.classList.add('hidden');
      if (_pollInterval) {
        clearInterval(_pollInterval);
        _pollInterval = null;
      }

      // Update results
      document.getElementById('res-movies-created').textContent = `${p.moviesCreated} créés`;
      document.getElementById('res-movies-skipped').textContent = `${p.moviesSkipped} ignorés`;
      document.getElementById('res-movies-removed').textContent = `${p.moviesRemoved} supprimés`;
      document.getElementById('res-series-created').textContent = `${p.seriesCreated} créées`;
      document.getElementById('res-series-skipped').textContent = `${p.seriesSkipped} ignorées`;
      document.getElementById('res-series-removed').textContent = `${p.seriesRemoved} supprimées`;
      document.getElementById('res-live-channels').textContent = `${p.liveChannels} chaînes`;

      const errEl = document.getElementById('last-error-msg');
      if (p.lastError) {
        errEl.textContent = `Erreur : ${p.lastError}`;
        errEl.classList.remove('hidden');
      } else {
        errEl.classList.add('hidden');
      }

      // Refresh status counters
      loadStatus();
    }
  } catch (err) {
    console.debug('loadSyncProgress:', err);
  }
}

// ── Button handlers ───────────────────────────────────────────────────────────

// Sync (incremental)
document.getElementById('btn-sync').addEventListener('click', async () => {
  await triggerSync(false);
});

// Sync (full)
document.getElementById('btn-sync-full').addEventListener('click', async () => {
  await triggerSync(true);
});

async function triggerSync(full) {
  const btn = document.getElementById(full ? 'btn-sync-full' : 'btn-sync');
  btn.disabled = true;
  try {
    await api('POST', full ? '/sync/trigger?full=true' : '/sync/trigger');
    showMessage('sync-message', full ? 'Synchronisation complète démarrée.' : 'Synchronisation démarrée.', 'info');
    // Start polling immediately
    if (_pollInterval) clearInterval(_pollInterval);
    _pollInterval = setInterval(loadSyncProgress, 2000);
    loadSyncProgress();
  } catch (e) {
    showMessage('sync-message', 'Erreur : ' + e.message, 'error');
  } finally {
    btn.disabled = false;
  }
}

document.getElementById('btn-refresh-status').addEventListener('click', () => {
  loadStatus();
  loadSyncProgress();
});

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

// Save buttons
document.getElementById('btn-save-server').addEventListener('click', () => saveConfig('test-result'));
document.getElementById('btn-save-bridge').addEventListener('click', () => saveConfig('bridge-message'));
document.getElementById('btn-save-sync').addEventListener('click', () => saveConfig('sync-save-message'));
document.getElementById('btn-save-live').addEventListener('click', () => saveConfig('live-message'));

async function saveConfig(feedbackId) {
  try {
    await api('POST', '/config', collectConfig());
    showMessage(feedbackId, 'Configuration enregistrée ✓', 'success');
    loadConfig();
  } catch (e) {
    showMessage(feedbackId, 'Erreur : ' + e.message, 'error');
  }
}

// Delete snapshots
document.getElementById('btn-delete-snapshots').addEventListener('click', async () => {
  if (!confirm('Supprimer tous les snapshots ? La prochaine sync sera complète.')) return;
  try {
    await api('DELETE', '/snapshots');
    showMessage('snapshot-message', 'Snapshots supprimés ✓', 'success');
    loadSnapshots();
  } catch (e) {
    showMessage('snapshot-message', 'Erreur : ' + e.message, 'error');
  }
});

async function loadSnapshots() {
  try {
    const list = await api('GET', '/snapshots');
    const el = document.getElementById('snapshot-info');
    if (!list || list.length === 0) {
      el.textContent = 'Aucun snapshot — la prochaine sync sera complète.';
    } else {
      const latest = list[0];
      const date = new Date(latest.date).toLocaleString('fr-FR');
      el.textContent = `${list.length} snapshot(s) — dernier : ${date} (${(latest.sizeBytes / 1024).toFixed(0)} Ko)`;
    }
  } catch (e) {
    document.getElementById('snapshot-info').textContent = 'Impossible de charger les snapshots.';
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
loadSyncProgress();
loadSnapshots();

// Refresh status every 30s when idle; progress polling handled separately
setInterval(() => {
  if (!_pollInterval) loadStatus();
}, 30_000);
