'use strict';

// Copy buttons: data-copy="<id of a textarea/input>".
document.addEventListener('click', async e => {
  const btn = e.target.closest('[data-copy]');
  if (!btn) return;
  const src = document.getElementById(btn.dataset.copy);
  if (!src) return;
  const text = src.value ?? src.textContent;
  try { await navigator.clipboard.writeText(text); }
  catch { src.classList.remove('hidden'); src.select(); document.execCommand('copy'); }
  const label = btn.textContent;
  btn.textContent = 'Copied ✓';
  setTimeout(() => { btn.textContent = label; }, 1500);
});

// Whole table rows open their link.
document.addEventListener('click', e => {
  const row = e.target.closest('tr[data-href]');
  if (row && !e.target.closest('a, button, input')) location.href = row.dataset.href;
});

// Issue form: subscription length (years + months or lifetime), "valid until" preview, Unlimited users.
(() => {
  const start = document.getElementById('start');
  const out = document.getElementById('validUntil');
  const years = document.getElementById('years');
  const months = document.getElementById('months');
  const lifetime = document.getElementById('lifetime');
  const users = document.getElementById('users');
  const unlimited = document.getElementById('unlimited');
  if (!start || !out) return;
  const pad = n => String(n).padStart(2, '0');
  function update() {
    const life = lifetime?.checked;
    if (years) years.disabled = life;
    if (months) months.disabled = life;
    if (life) { out.textContent = 'Valid for life.'; return; }
    const total = (+years?.value || 0) * 12 + (+months?.value || 0);
    if (!start.value || total < 1) { out.textContent = 'Enter at least 1 month.'; return; }
    const [y, m, d] = start.value.split('-').map(Number);
    // Same day-of-month that many months later, minus one day (as the key does).
    const target = new Date(y, m - 1 + total, 1);
    const lastDay = new Date(target.getFullYear(), target.getMonth() + 1, 0).getDate();
    const end = new Date(target.getFullYear(), target.getMonth(), Math.min(d, lastDay));
    end.setDate(end.getDate() - 1);
    out.textContent = `Valid until ${pad(end.getDate())}-${pad(end.getMonth() + 1)}-${end.getFullYear()}.`;
  }
  document.querySelectorAll('[data-len]').forEach(b => b.addEventListener('click', () => {
    const [yy, mm] = b.dataset.len.split(',');
    if (lifetime) lifetime.checked = false;
    years.value = yy; months.value = mm;
    update();
  }));
  [start, years, months].forEach(el => el?.addEventListener('input', update));
  lifetime?.addEventListener('change', update);
  function syncUsers() { if (users && unlimited) users.disabled = unlimited.checked; }
  unlimited?.addEventListener('change', syncUsers);
  update();
  syncUsers();
})();