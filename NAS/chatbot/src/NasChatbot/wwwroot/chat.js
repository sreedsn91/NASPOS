'use strict';

const messages = document.getElementById('messages');
const form = document.getElementById('ask');
const input = document.getElementById('input');
const send = document.getElementById('send');

function scrollDown() { messages.scrollTop = messages.scrollHeight; }

function addUser(text) {
  messages.append(el('div', { class: 'msg user' }, el('div', { class: 'bubble' }, text)));
  scrollDown();
}

function addBot(contentNodes) {
  const bubble = el('div', { class: 'bubble' }, contentNodes);
  const row = el('div', { class: 'msg bot' }, el('div', { class: 'avatar', 'aria-hidden': 'true' }, 'NB'), bubble);
  messages.append(row);
  scrollDown();
  return bubble;
}

function chips(suggestions) {
  if (!suggestions || !suggestions.length) return null;
  return el('div', { class: 'chips' }, suggestions.map(s =>
    el('button', { class: 'chip', type: 'button', onclick: () => pick(s) }, s.question)));
}

function feedback(question, reply) {
  const box = el('div', { class: 'feedback' }, 'Was this helpful?');
  const done = text => { box.replaceChildren(text); };
  box.append(
    el('button', { type: 'button', onclick: () => done('Thanks for the feedback!') }, '👍 Yes'),
    el('button', {
      type: 'button',
      onclick: async () => {
        done('Thanks — the admin will improve this answer.');
        try { await api('/api/chat/feedback', { method: 'POST', body: { question, entryId: reply.entryId, helpful: false } }); } catch { /* ignore */ }
      },
    }, '👎 No'));
  return box;
}

function renderReply(reply, question) {
  const nodes = [];
  if (reply.kind === 'maybe') nodes.push(el('div', { class: 'unsure' }, 'I think you are asking: “' + reply.question + '”'));
  else if (reply.kind === 'answer' && reply.question && question !== reply.question)
    nodes.push(el('div', { class: 'about' }, reply.category + ' · ' + reply.question));
  nodes.push(el('div', { html: formatAnswer(reply.text) }));
  if (reply.related && reply.related.length) {
    if (reply.kind !== 'unknown') nodes.push(el('div', { class: 'about related-label' }, 'Related questions'));
    nodes.push(chips(reply.related));
  }
  if (reply.kind === 'answer' || reply.kind === 'maybe') nodes.push(feedback(question, reply));
  addBot(nodes);
}

function typing() {
  return addBot([el('div', { class: 'typing', 'aria-label': 'Thinking' }, el('span'), el('span'), el('span'))]).parentElement;
}

async function ask(text) {
  addUser(text);
  const wait = typing();
  setBusy(true);
  try {
    const reply = await api('/api/chat', { method: 'POST', body: { message: text } });
    wait.remove();
    renderReply(reply, text);
  } catch (e) {
    wait.remove();
    addBot([el('div', { class: 'unsure' }, e.message)]);
  } finally {
    setBusy(false);
    input.focus();
  }
}

async function pick(suggestion) {
  addUser(suggestion.question);
  try {
    renderReply(await api('/api/chat/answer/' + encodeURIComponent(suggestion.id)), suggestion.question);
  } catch {
    await ask(suggestion.question);
  }
}

function setBusy(busy) { send.disabled = busy; }

async function start() {
  messages.replaceChildren();
  try {
    const s = await api('/api/chat/start');
    addBot([
      el('div', { html: formatAnswer(s.greeting + '\n\nI know answers to **' + s.count + '** questions. Try one of these, or type your own:') }),
      chips(s.suggestions),
    ]);
  } catch (e) {
    addBot([el('div', { class: 'unsure' }, e.message)]);
  }
  input.focus();
}

form.addEventListener('submit', e => {
  e.preventDefault();
  const text = input.value.trim();
  if (!text || send.disabled) return;
  input.value = '';
  autoSize();
  ask(text);
});

input.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); }
});

function autoSize() { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 140) + 'px'; }
input.addEventListener('input', autoSize);

document.getElementById('clear').addEventListener('click', start);

start();
