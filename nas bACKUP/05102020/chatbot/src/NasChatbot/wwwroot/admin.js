'use strict';

const $ = id => document.getElementById(id);
const TOKEN_KEY = 'nas-chatbot-admin';

let token = null;
try { token = sessionStorage.getItem(TOKEN_KEY); } catch { /* storage unavailable */ }

let entries = [];
let current = null;          // entry being edited, or null for a new one
let fromUnanswered = null;   // unanswered id to dismiss once its answer is saved

function setToken(value) {
  token = value;
  try { value ? sessionStorage.setItem(TOKEN_KEY, value) : sessionStorage.removeItem(TOKEN_KEY); } catch { /* ignore */ }
}

/** API call as the signed-in admin; a 401 sends the user back to sign-in. */
async function adminApi(path, opts = {}) {
  try {
    return await api(path, { ...opts, token });
  } catch (e) {
    if (e.status === 401 && path !== '/api/admin/login') { setToken(null); await showAuth(); }
    throw e;
  }
}

// ---------------- sign-in / setup ----------------

let setupMode = false;

async function showAuth() {
  $('app').classList.add('hidden');
  $('logout').classList.add('hidden');
  $('auth').classList.remove('hidden');
  $('auth-error').textContent = '';
  try {
    const status = await api('/api/admin/status');
    setupMode = !status.configured;
    if (setupMode && !status.local) {
      $('auth-title').textContent = 'Admin password not set';
      $('auth-help').textContent = 'Open this page on the computer running NAS Chatbot (http://localhost:5090/admin.html) to set the admin password.';
      $('auth-form').classList.add('hidden');
      return;
    }
  } catch (e) {
    $('auth-error').textContent = e.message;
    return;
  }
  $('auth-form').classList.remove('hidden');
  $('auth-title').textContent = setupMode ? 'Set the admin password' : 'Sign in';
  $('auth-help').textContent = setupMode
    ? 'First time here. Choose a password (at least 8 characters with letters and digits). Only people with this password can change the answers.'
    : 'Enter the admin password to add and edit answers.';
  $('auth-confirm-wrap').classList.toggle('hidden', !setupMode);
  $('auth-confirm').required = setupMode;
  $('auth-password').autocomplete = setupMode ? 'new-password' : 'current-password';
  $('auth-submit').textContent = setupMode ? 'Set password' : 'Sign in';
  $('auth-password').value = '';
  $('auth-confirm').value = '';
  $('auth-password').focus();
}

$('auth-form').addEventListener('submit', async e => {
  e.preventDefault();
  const password = $('auth-password').value;
  if (setupMode && password !== $('auth-confirm').value) { $('auth-error').textContent = 'The two passwords do not match.'; return; }
  $('auth-submit').disabled = true;
  try {
    const res = await api(setupMode ? '/api/admin/setup' : '/api/admin/login', { method: 'POST', body: { password } });
    setToken(res.token);
    await showApp();
  } catch (err) {
    $('auth-error').textContent = err.message;
  } finally {
    $('auth-submit').disabled = false;
  }
});

$('logout').addEventListener('click', async () => {
  try { await adminApi('/api/admin/logout', { method: 'POST' }); } catch { /* already signed out */ }
  setToken(null);
  await showAuth();
});

async function showApp() {
  $('auth').classList.add('hidden');
  $('app').classList.remove('hidden');
  $('logout').classList.remove('hidden');
  await Promise.all([loadEntries(), loadUnanswered()]);
  if (!current) newEntry();
}

// ---------------- tabs ----------------

function openTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  document.querySelectorAll('[data-panel]').forEach(p => p.classList.toggle('hidden', p.dataset.panel !== name));
  if (name === 'unanswered') loadUnanswered();
}
document.querySelectorAll('.tab').forEach(t => t.addEventListener('click', () => openTab(t.dataset.tab)));

// ---------------- questions list ----------------

async function loadEntries() {
  const data = await adminApi('/api/admin/entries');
  entries = data.entries;
  $('count-questions').textContent = entries.length;
  const sel = $('filter-category');
  const chosen = sel.value;
  sel.replaceChildren(el('option', { value: '' }, 'All categories'), ...data.categories.map(c => el('option', { value: c }, c)));
  sel.value = data.categories.includes(chosen) ? chosen : '';
  $('categories').replaceChildren(...data.categories.map(c => el('option', { value: c })));
  renderList();
}

function renderList() {
  const q = $('filter').value.trim().toLowerCase();
  const cat = $('filter-category').value;
  const shown = entries
    .filter(e => !cat || e.category === cat)
    .filter(e => !q || [e.question, e.answer, e.category, ...e.variants, ...e.tags].join(' ').toLowerCase().includes(q))
    .sort((a, b) => a.category.localeCompare(b.category) || a.question.localeCompare(b.question));
  $('list').replaceChildren(...(shown.length ? shown.map(e =>
    el('button', { type: 'button', class: 'list-item' + (current && current.id === e.id ? ' active' : ''), onclick: () => edit(e) },
      el('div', { class: 'q' }, e.question),
      el('div', { class: 'c' }, e.category + (e.variants.length ? ` · ${e.variants.length} other wording(s)` : '')))
  ) : [el('p', { class: 'muted' }, 'No questions match.')]));
}

$('filter').addEventListener('input', renderList);
$('filter-category').addEventListener('change', renderList);

// ---------------- editor ----------------

const splitLines = text => text.split('\n').map(s => s.trim()).filter(Boolean);
const splitCommas = text => text.split(',').map(s => s.trim()).filter(Boolean);

function fill(entry) {
  $('f-question').value = entry.question || '';
  $('f-variants').value = (entry.variants || []).join('\n');
  $('f-category').value = entry.category || '';
  $('f-tags').value = (entry.tags || []).join(', ');
  $('f-answer').value = entry.answer || '';
  $('editor-error').textContent = '';
  $('editor-notice').textContent = '';
  updatePreview();
}

function edit(entry) {
  current = entry;
  fromUnanswered = null;
  $('editor-title').textContent = 'Edit question';
  $('delete').classList.remove('hidden');
  fill(entry);
  renderList();
}

function newEntry(prefill = {}) {
  current = null;
  $('editor-title').textContent = 'New question';
  $('delete').classList.add('hidden');
  fill(prefill);
  renderList();
  $('f-question').focus();
}

$('new').addEventListener('click', () => { fromUnanswered = null; newEntry(); });

function updatePreview() {
  const text = $('f-answer').value.trim();
  $('preview').innerHTML = text ? formatAnswer(text) : '<span class="muted">The answer preview appears here.</span>';
}
$('f-answer').addEventListener('input', updatePreview);

$('editor').addEventListener('submit', async e => {
  e.preventDefault();
  const body = {
    question: $('f-question').value,
    variants: splitLines($('f-variants').value),
    category: $('f-category').value,
    tags: splitCommas($('f-tags').value),
    answer: $('f-answer').value,
  };
  $('save').disabled = true;
  $('editor-error').textContent = '';
  try {
    const saved = current
      ? await adminApi('/api/admin/entries/' + encodeURIComponent(current.id), { method: 'PUT', body })
      : await adminApi('/api/admin/entries', { method: 'POST', body });
    if (fromUnanswered) {
      try { await adminApi('/api/admin/unanswered/' + encodeURIComponent(fromUnanswered), { method: 'DELETE' }); } catch { /* already gone */ }
      fromUnanswered = null;
      loadUnanswered();
    }
    await loadEntries();
    edit(entries.find(x => x.id === saved.id) || saved);
    $('editor-notice').textContent = 'Saved. The bot uses it right away.';
  } catch (err) {
    $('editor-error').textContent = err.message;
  } finally {
    $('save').disabled = false;
  }
});

$('delete').addEventListener('click', async () => {
  if (!current || !confirm(`Delete this question?\n\n“${current.question}”`)) return;
  try {
    await adminApi('/api/admin/entries/' + encodeURIComponent(current.id), { method: 'DELETE' });
    await loadEntries();
    newEntry();
    $('editor-notice').textContent = 'Deleted.';
  } catch (err) {
    $('editor-error').textContent = err.message;
  }
});

$('try').addEventListener('click', () => {
  const q = $('f-question').value.trim();
  openTab('test');
  $('test-q').value = q;
  if (q) runTest(q);
});

// ---------------- unanswered ----------------

async function loadUnanswered() {
  let rows;
  try { rows = await adminApi('/api/admin/unanswered'); } catch { return; }
  $('count-unanswered').textContent = rows.length;
  $('unanswered-empty').classList.toggle('hidden', rows.length > 0);
  $('unanswered-rows').replaceChildren(...rows.map(u => el('tr', {},
    el('td', {}, u.text),
    el('td', { class: 'score' }, u.count),
    el('td', {}, u.reason === 'not-helpful' ? '👎 answer not helpful' : 'no answer found'),
    el('td', {}, new Date(u.lastAskedUtc).toLocaleString()),
    el('td', { class: 'actions' },
      el('button', { class: 'btn small primary', type: 'button', onclick: () => answer(u) }, 'Add answer'),
      el('button', { class: 'btn small', type: 'button', onclick: () => dismiss(u) }, 'Dismiss')))));
}

function answer(u) {
  openTab('questions');
  newEntry({ question: u.text });
  fromUnanswered = u.id;
  $('f-answer').focus();
}

async function dismiss(u) {
  try { await adminApi('/api/admin/unanswered/' + encodeURIComponent(u.id), { method: 'DELETE' }); } catch { /* ignore */ }
  loadUnanswered();
}

// ---------------- test ----------------

async function runTest(q) {
  const out = $('test-out');
  try {
    const res = await adminApi('/api/admin/test?q=' + encodeURIComponent(q));
    const verdict = c => c >= res.answerAt ? 'answers' : c >= res.maybeAt ? 'answers (not sure)' : 'too weak';
    out.classList.remove('muted');
    out.replaceChildren(
      el('p', { class: 'tokens' }, 'Words the bot understood: ', ...(res.understood.length ? res.understood.map(t => el('code', {}, t)) : [el('i', {}, 'none')])),
      res.matches.length
        ? el('table', { class: 'grid' },
            el('thead', {}, el('tr', {}, el('th', {}, 'Matching question'), el('th', {}, 'Category'), el('th', {}, 'Confidence'), el('th', {}, ''))),
            el('tbody', {}, res.matches.map(m => {
              const bar = el('i');
              bar.style.width = Math.round(m.confidence * 100) + '%';
              return el('tr', {},
                el('td', {}, m.question),
                el('td', {}, m.category),
                el('td', { class: 'score' }, el('span', { class: 'meter' }, bar), Math.round(m.confidence * 100) + '% · ' + verdict(m.confidence)),
                el('td', {}, el('button', { class: 'btn small', type: 'button', onclick: () => { openTab('questions'); const e = entries.find(x => x.id === m.id); if (e) edit(e); } }, 'Edit')));
            })))
        : el('p', { class: 'muted' }, 'No question matches. Add one, or add these words as another wording of an existing question.'));
  } catch (err) {
    out.textContent = err.message;
  }
}

$('test-form').addEventListener('submit', e => {
  e.preventDefault();
  const q = $('test-q').value.trim();
  if (q) runTest(q);
});

// ---------------- import / export / password ----------------

$('export').addEventListener('click', async () => {
  try {
    const res = await fetch('/api/admin/export', { headers: { Authorization: 'Bearer ' + token } });
    if (res.status === 401) { setToken(null); return showAuth(); }
    if (!res.ok) throw new Error('Export failed.');
    const blob = await res.blob();
    const name = /filename="?([^";]+)/.exec(res.headers.get('Content-Disposition') || '')?.[1] || 'nas-chatbot-questions.json';
    const a = el('a', { href: URL.createObjectURL(blob), download: name });
    document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  } catch (err) {
    $('import-error').textContent = err.message;
  }
});

$('import').addEventListener('click', async () => {
  $('import-error').textContent = '';
  $('import-notice').textContent = '';
  const file = $('import-file').files[0];
  if (!file) { $('import-error').textContent = 'Choose a .json file first.'; return; }
  let data;
  try { data = JSON.parse(await file.text()); } catch { $('import-error').textContent = 'That file is not valid JSON.'; return; }
  const list = Array.isArray(data) ? data : data.entries;
  if (!Array.isArray(list)) { $('import-error').textContent = 'The file must contain an "entries" list.'; return; }
  const replace = $('import-replace').checked;
  if (replace && !confirm(`Replace all ${entries.length} existing questions with ${list.length} from the file?`)) return;
  try {
    const res = await adminApi('/api/admin/import', { method: 'POST', body: { entries: list, replace } });
    $('import-notice').textContent = `Imported ${res.imported} question(s).`;
    await loadEntries();
    newEntry();
  } catch (err) {
    $('import-error').textContent = err.message;
  }
});

$('pw-form').addEventListener('submit', async e => {
  e.preventDefault();
  $('pw-error').textContent = '';
  if ($('pw-next').value !== $('pw-confirm').value) { $('pw-error').textContent = 'The new passwords do not match.'; return; }
  try {
    await adminApi('/api/admin/password', { method: 'POST', body: { current: $('pw-current').value, next: $('pw-next').value } });
    alert('Password changed. Please sign in with the new password.');
    setToken(null);
    await showAuth();
  } catch (err) {
    $('pw-error').textContent = err.message;
  }
});

// ---------------- start ----------------

(async () => {
  if (token) {
    try { await showApp(); return; } catch { /* token expired → sign in */ }
  }
  await showAuth();
})();
