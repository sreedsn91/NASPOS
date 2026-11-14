import { Component, ElementRef, Injectable, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { DmyhmPipe } from '../../core/format';
import { Ui } from '../../core/ui';
import { ModalComponent } from '../../shared/components';
import { IconComponent } from '../../shared/icon';

/* ================= shared ================= */

interface Suggestion { id: string; question: string; }
interface FaqReply {
  kind: 'answer' | 'maybe' | 'restricted' | 'unknown' | 'smalltalk';
  text: string; id?: string | null; question?: string | null; category?: string | null; link?: string | null;
  confidence: number; related: Suggestion[];
}

/** A run of answer text; bold runs come from **…** in the answer. */
interface Run { text: string; bold: boolean; }
/** Answers are rendered from data (never innerHTML): paragraphs, bullet lists and numbered steps. */
interface Block { kind: 'p' | 'ul' | 'ol'; lines: Run[][]; }

function runs(line: string): Run[] {
  return line.split(/(\*\*[^*]+\*\*)/g).filter(Boolean)
    .map(p => p.startsWith('**') && p.endsWith('**') && p.length > 4 ? { text: p.slice(2, -2), bold: true } : { text: p, bold: false });
}

export function toBlocks(text: string): Block[] {
  const blocks: Block[] = [];
  let current: Block | null = null;
  for (const raw of (text ?? '').replace(/\r\n/g, '\n').split('\n')) {
    const line = raw.trimEnd();
    const bullet = /^\s*[-•*]\s+(.*)$/.exec(line);
    const step = /^\s*\d+[.)]\s+(.*)$/.exec(line);
    const kind: Block['kind'] | null = bullet ? 'ul' : step ? 'ol' : line.trim() ? 'p' : null;
    if (!kind) { current = null; continue; }
    const content = runs((bullet ?? step)?.[1] ?? line);
    if (!current || current.kind !== kind) { current = { kind, lines: [] }; blocks.push(current); }
    current.lines.push(content);
  }
  return blocks;
}

/** Renders an answer's blocks. */
@Component({
  selector: 'app-answer',
  standalone: true,
  template: `
    @for (b of blocks(); track $index) {
      @switch (b.kind) {
        @case ('ul') { <ul>@for (l of b.lines; track $index) {<li>@for (r of l; track $index) {@if (r.bold) {<b>{{ r.text }}</b>} @else {<span>{{ r.text }}</span>}}</li>}</ul> }
        @case ('ol') { <ol>@for (l of b.lines; track $index) {<li>@for (r of l; track $index) {@if (r.bold) {<b>{{ r.text }}</b>} @else {<span>{{ r.text }}</span>}}</li>}</ol> }
        @default { <p>@for (l of b.lines; track $index; let last = $last) {@for (r of l; track $index) {@if (r.bold) {<b>{{ r.text }}</b>} @else {<span>{{ r.text }}</span>}}@if (!last) {<br />}}</p> }
      }
    }`,
  styles: [`:host { display: block; } p, ul, ol { margin: 0 0 8px; } ul, ol { padding-left: 20px; } li { margin: 2px 0; }
    :host > :last-child { margin-bottom: 0; }`],
  inputs: ['text'],
})
export class AnswerComponent {
  private _text = signal('');
  set text(v: string) { this._text.set(v ?? ''); }
  blocks = computed(() => toBlocks(this._text()));
}

/* ================= FAQ › Chatbot ================= */

interface ChatMessage { from: 'user' | 'bot'; text?: string; reply?: FaqReply; asked?: string; feedback?: 'yes' | 'no'; error?: boolean; }

/** Keeps the conversation while the user moves between screens (cleared on sign-out / reload). */
@Injectable({ providedIn: 'root' })
export class ChatState {
  readonly messages = signal<ChatMessage[]>([]);
  readonly started = signal(false);
}

@Component({
  selector: 'app-chatbot',
  standalone: true,
  imports: [FormsModule, RouterLink, IconComponent, AnswerComponent],
  template: `
    <div class="page chat-page">
      <div class="page-header">
        <h1><app-icon name="chat" [size]="20" /> FAQ chatbot <span class="muted">works offline · answers for your role</span></h1>
        <button class="btn sm" (click)="restart()">New chat</button>
        <a class="btn sm" routerLink="/faq/email"><app-icon name="mail" [size]="15" /> Email us</a>
      </div>

      <div class="card chat-card">
        <div class="messages" #scroller aria-live="polite">
          @for (m of state.messages(); track $index) {
            @if (m.from === 'user') {
              <div class="msg user"><div class="bubble">{{ m.text }}</div></div>
            } @else {
              <div class="msg bot">
                <div class="bot-avatar" aria-hidden="true"><app-icon name="help" [size]="18" /></div>
                <div class="bubble" [class.restricted]="m.reply?.kind === 'restricted'" [class.error]="m.error">
                  @if (m.reply; as r) {
                    @if (r.kind === 'maybe') { <div class="unsure">I think you are asking: “{{ r.question }}”</div> }
                    @else if (r.kind === 'answer' && r.question && r.question !== m.asked) { <div class="about">{{ r.category }} · {{ r.question }}</div> }
                    @if (r.kind === 'restricted') { <div class="about"><app-icon name="shield" [size]="14" /> Not available for your role</div> }
                    <app-answer [text]="r.text" />
                    @if (r.link && (r.kind === 'answer' || r.kind === 'maybe')) {
                      <a class="btn sm primary open-link" [routerLink]="r.link">Open this screen <app-icon name="chevron" [size]="14" /></a>
                    }
                    @if (r.related.length) {
                      @if (r.kind === 'answer' || r.kind === 'maybe') { <div class="about related">Related questions</div> }
                      <div class="chips">@for (s of r.related; track s.id) { <button class="chip" (click)="pick(s)">{{ s.question }}</button> }</div>
                    }
                    @if (r.kind === 'answer' || r.kind === 'maybe') {
                      <div class="feedback">
                        @if (!m.feedback) {
                          Was this helpful?
                          <button (click)="rate(m, true)">👍 Yes</button><button (click)="rate(m, false)">👎 No</button>
                        } @else if (m.feedback === 'yes') { Thanks for the feedback! }
                        @else { Thanks — your administrator will see this question. <a routerLink="/faq/email">Email us</a> if you need help now. }
                      </div>
                    }
                  } @else {
                    <app-answer [text]="m.text ?? ''" />
                  }
                </div>
              </div>
            }
          }
          @if (busy()) {
            <div class="msg bot"><div class="bot-avatar"><app-icon name="help" [size]="18" /></div>
              <div class="bubble"><span class="typing"><i></i><i></i><i></i></span></div></div>
          }
        </div>
        <form class="composer" (ngSubmit)="send()">
          <textarea #box rows="1" [(ngModel)]="draft" name="q" maxlength="300" (keydown.enter)="enter($any($event))"
            placeholder="Ask about NAS POS Pro… e.g. How do I make a credit bill?" aria-label="Your question"></textarea>
          <button class="btn primary" type="submit" [disabled]="busy() || !draft.trim()"><app-icon name="send" [size]="16" /> Send</button>
        </form>
        <div class="hint muted small">Enter to send · Shift+Enter for a new line · type <b>help</b> to see topics</div>
      </div>
    </div>`,
  styles: [`
    .chat-page { max-width: 900px; }
    .chat-card { display: flex; flex-direction: column; height: calc(100vh - 160px); min-height: 420px; }
    .messages { flex: 1; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 12px; }
    .msg { display: flex; gap: 10px; }
    .msg.user { justify-content: flex-end; }
    .bubble { max-width: min(640px, 86%); padding: 10px 14px; border-radius: 14px; line-height: 1.55; overflow-wrap: anywhere; }
    .msg.user .bubble { background: var(--primary); color: var(--primary-ink); border-top-right-radius: 4px; white-space: pre-wrap; }
    .msg.bot .bubble { background: var(--surface-2); border: 1px solid var(--line); border-top-left-radius: 4px; }
    .bubble.restricted { background: var(--warn-soft); border-color: color-mix(in srgb, var(--warn) 35%, transparent); }
    .bubble.error { background: var(--danger-soft); color: var(--danger); }
    .bot-avatar { flex-shrink: 0; width: 30px; height: 30px; border-radius: 50%; display: grid; place-items: center; background: var(--primary-soft); color: var(--primary); }
    .about { display: flex; align-items: center; gap: 5px; font-size: 12px; color: var(--muted); margin-bottom: 6px; }
    .about.related { margin: 10px 0 0; }
    .unsure { display: inline-block; font-size: 12.5px; padding: 3px 8px; border-radius: 8px; background: var(--warn-soft); color: var(--warn); margin-bottom: 8px; }
    .open-link { margin-top: 10px; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .chip { border: 1px solid var(--line); background: var(--surface); color: var(--text); border-radius: 999px; padding: 5px 12px; font: inherit; font-size: 12.5px; cursor: pointer; text-align: left; }
    .chip:hover { border-color: var(--primary); color: var(--primary); }
    .feedback { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 10px; font-size: 12px; color: var(--muted); }
    .feedback button { border: 1px solid var(--line); background: var(--surface); color: var(--text); border-radius: 8px; padding: 2px 9px; font: inherit; cursor: pointer; }
    .feedback button:hover { background: var(--surface-3); }
    .composer { display: flex; gap: 8px; align-items: flex-end; padding: 12px 16px 6px; border-top: 1px solid var(--line); }
    .composer textarea { flex: 1; min-height: 38px; max-height: 140px; resize: none; }
    .hint { padding: 0 16px 10px; }
    .typing i { display: inline-block; width: 7px; height: 7px; margin: 0 2px; border-radius: 50%; background: var(--muted); animation: blink 1.2s infinite both; }
    .typing i:nth-child(2) { animation-delay: .2s; } .typing i:nth-child(3) { animation-delay: .4s; }
    @keyframes blink { 0%, 80%, 100% { opacity: .25; } 40% { opacity: 1; } }
    @media (max-width: 640px) { .chat-card { height: calc(100vh - 130px); } .bubble { max-width: 92%; } }
  `],
})
export class ChatbotComponent implements OnInit {
  state = inject(ChatState);
  private api = inject(Api);
  busy = signal(false);
  draft = '';
  @ViewChild('scroller') scroller?: ElementRef<HTMLElement>;
  @ViewChild('box') box?: ElementRef<HTMLTextAreaElement>;

  async ngOnInit(): Promise<void> {
    if (!this.state.started()) await this.restart();
    this.scrollDown();
    setTimeout(() => this.box?.nativeElement.focus());
  }

  async restart(): Promise<void> {
    this.state.messages.set([]);
    this.state.started.set(true);
    try {
      const s = await this.api.get<{ greeting: string; suggestions: Suggestion[]; count: number; categories: string[] }>('/faq/start');
      this.push({ from: 'bot', reply: { kind: 'smalltalk', text: `${s.greeting}\n\nI know **${s.count}** answers for your role. Try one of these, or type your own question:`, confidence: 1, related: s.suggestions } });
    } catch (e) {
      this.push({ from: 'bot', text: errorMessage(e), error: true });
    }
  }

  enter(e: KeyboardEvent): void {
    if (e.shiftKey) return;
    e.preventDefault();
    this.send();
  }

  async send(): Promise<void> {
    const text = this.draft.trim();
    if (!text || this.busy()) return;
    this.draft = '';
    await this.ask(text, () => this.api.post<FaqReply>('/faq/ask', { message: text }));
  }

  async pick(s: Suggestion): Promise<void> {
    if (this.busy()) return;
    await this.ask(s.question, () => this.api.get<FaqReply>('/faq/answer/' + encodeURIComponent(s.id)));
  }

  private async ask(text: string, call: () => Promise<FaqReply>): Promise<void> {
    this.push({ from: 'user', text });
    this.busy.set(true);
    try { this.push({ from: 'bot', reply: await call(), asked: text }); }
    catch (e) { this.push({ from: 'bot', text: errorMessage(e), error: true }); }
    finally { this.busy.set(false); this.scrollDown(); this.box?.nativeElement.focus(); }
  }

  async rate(m: ChatMessage, helpful: boolean): Promise<void> {
    this.state.messages.update(list => list.map(x => x === m ? { ...x, feedback: helpful ? 'yes' : 'no' } : x));
    if (!helpful) { try { await this.api.post('/faq/feedback', { question: m.asked, helpful }); } catch { /* best effort */ } }
  }

  private push(m: ChatMessage): void {
    this.state.messages.update(list => [...list, m]);
    this.scrollDown();
  }

  private scrollDown(): void {
    setTimeout(() => { const el = this.scroller?.nativeElement; if (el) el.scrollTop = el.scrollHeight; });
  }
}

/* ================= FAQ › Email Us ================= */

interface SupportMessage { id: number; subject?: string; status: 'Queued' | 'Sent' | 'Failed'; createdAt: string; sentAt?: string; lastError?: string; }

@Component({
  selector: 'app-email-us',
  standalone: true,
  imports: [FormsModule, RouterLink, IconComponent, DmyhmPipe],
  template: `
    <div class="page" style="max-width:1000px">
      <div class="page-header"><h1><app-icon name="mail" [size]="20" /> Email us</h1>
        <a class="btn sm" routerLink="/faq/chat"><app-icon name="chat" [size]="15" /> Ask the chatbot first</a></div>

      @if (loaded() && !email()) {
        <div class="warn-box" style="margin-bottom:12px">
          <div>The support e-mail address hasn't been set yet, so messages can't be sent.
            @if (auth.can('Settings', 'Edit')) { Set it in <a routerLink="/settings/features">Settings › Features › Help &amp; support</a>. }
            @else { Please ask your administrator to set it in Settings › Features › Help &amp; support. }</div>
        </div>
      }

      <div class="cols">
        <div class="card"><header><h2>Send a message</h2></header>
          <section class="stack">
            <label class="field"><span>Topic</span>
              <select [(ngModel)]="topic">@for (t of topics; track t) {<option [value]="t">{{ t }}</option>}</select></label>
            <label class="field req"><span>Subject</span><input [(ngModel)]="subject" maxlength="150" placeholder="e.g. Thermal printer cuts the bill short" /></label>
            <label class="field req"><span>Message</span><textarea rows="7" [(ngModel)]="message" maxlength="4000"
              placeholder="What were you doing, what did you expect, and what happened? Include any error message."></textarea>
              <small class="muted">{{ message.length }} / 4000</small></label>
            <label class="field"><span>Your e-mail for the reply (optional)</span><input type="email" [(ngModel)]="replyTo" maxlength="200" /></label>
            <div class="toolbar">
              <button class="btn primary" (click)="send()" [disabled]="!email() || !subject.trim() || message.trim().length < 5"><app-icon name="send" [size]="16" /> Send</button>
              @if (email()) { <a class="btn" [href]="mailto()">Open in my e-mail app</a> }
            </div>
            <p class="muted small">Your user name, company and counter are added to the message so support can help you faster.
              If the internet or e-mail server is down, the message waits in the outbox — use <b>Send again</b> below once you're back online.</p>
          </section>
        </div>
        <div class="card"><header><h2>Contact</h2></header>
          <section class="stack">
            @if (email()) { <div><div class="muted small">E-mail</div><b>{{ email() }}</b></div> }
            @if (phone()) { <div><div class="muted small">Phone</div><b>{{ phone() }}</b></div> }
            @if (!email() && !phone()) { <div class="muted">No contact details set.</div> }
            <div class="info-box"><div>Most “how do I…” questions are answered instantly by the <a routerLink="/faq/chat">FAQ chatbot</a>, even without internet.</div></div>
          </section>
        </div>
      </div>

      <div class="card" style="margin-top:16px"><header><h2>My messages</h2><button class="btn sm ghost" (click)="loadMine()">Refresh</button></header>
        <table class="grid">
          <thead><tr><th>Sent</th><th>Subject</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (m of mine(); track m.id) {
              <tr><td class="nowrap">{{ m.createdAt | dmyhm }}</td><td>{{ m.subject }}</td>
                <td><span class="badge" [class.ok]="m.status === 'Sent'" [class.danger]="m.status === 'Failed'" [class.warn]="m.status === 'Queued'">
                  {{ m.status === 'Queued' ? 'Waiting to send' : m.status }}</span>
                  @if (m.status === 'Failed' && m.lastError) { <div class="danger-text small">{{ m.lastError }}</div> }</td>
                <td class="actions">@if (m.status === 'Failed') { <button class="btn sm" (click)="retry(m)">Send again</button> }</td></tr>
            } @empty { <tr><td colspan="4" class="empty">You haven't sent any messages.</td></tr> }
          </tbody>
        </table>
      </div>
    </div>`,
})
export class EmailUsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  topics = ['Question', 'Problem / error', 'Suggestion', 'Other'];
  email = signal<string | null>(null);
  phone = signal<string | null>(null);
  loaded = signal(false);
  mine = signal<SupportMessage[]>([]);
  topic = 'Question';
  subject = '';
  message = '';
  replyTo = '';

  async ngOnInit(): Promise<void> {
    try {
      const info = await this.api.get<{ email?: string; phone?: string }>('/support');
      this.email.set(info.email ?? null);
      this.phone.set(info.phone ?? null);
    } catch (e) { this.ui.error(errorMessage(e)); }
    this.loaded.set(true);
    await this.loadMine();
  }

  async loadMine(): Promise<void> {
    try { this.mine.set(await this.api.get<SupportMessage[]>('/support/mine')); } catch { /* list is optional */ }
  }

  mailto(): string {
    const subject = `[NAS POS] ${this.topic}: ${this.subject}`;
    return `mailto:${encodeURIComponent(this.email() ?? '')}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(this.message)}`;
  }

  async send(): Promise<void> {
    try {
      await this.ui.run(() => this.api.post('/support', { topic: this.topic, subject: this.subject, message: this.message, replyTo: this.replyTo || null }));
      this.ui.ok('Message queued. It is sent as soon as the e-mail server can be reached.');
      this.subject = '';
      this.message = '';
      await this.loadMine();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async retry(m: SupportMessage): Promise<void> {
    try { await this.api.post(`/support/${m.id}/retry`); this.ui.ok('Queued again.'); await this.loadMine(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= FAQ › Manage FAQ ================= */

type RightName = 'Read' | 'Add' | 'Edit' | 'Cancel' | 'Delete' | 'Print' | 'Export' | 'Email' | 'Sms';
interface FaqItem {
  id: string; entryId?: number | null; question: string; variants: string[]; answer: string; category: string;
  tags: string[]; screens: string[]; right: RightName; link?: string | null; builtIn: boolean;
}
interface FaqEntry {
  id: number; question: string; variants: string[]; answer: string; category: string; tags: string[];
  screens: string[]; right: RightName; link?: string | null; isActive: boolean;
}
interface EntryForm { id: number; question: string; variantsText: string; answer: string; category: string; tagsText: string;
  screens: Record<string, boolean>; right: RightName; link: string; isActive: boolean; fromLog?: number; }
interface FaqLog { id: number; text: string; reason: 'no-match' | 'not-helpful' | 'no-access'; count: number; lastUserName?: string; lastAskedAt: string; }
interface TestResult { understood: string[]; answerAt: number; maybeAt: number;
  matches: { id: string; question: string; category: string; confidence: number; builtIn: boolean; screens: string[] }[]; }

@Component({
  selector: 'app-manage-faq',
  standalone: true,
  imports: [FormsModule, IconComponent, ModalComponent, AnswerComponent, DmyhmPipe],
  template: `
    <div class="page">
      <div class="page-header"><h1><app-icon name="help" [size]="20" /> Manage FAQ chatbot <span class="muted">{{ items().length }} answers</span></h1>
        @if (auth.can('Faq', 'Add')) { <button class="btn primary" (click)="newEntry()">+ New question</button> }</div>

      <div class="tabs">
        <button [class.active]="tab() === 'items'" (click)="tab.set('items')">Questions</button>
        <button [class.active]="tab() === 'log'" (click)="tab.set('log'); loadLogs()">Unanswered <span class="badge info">{{ logs().length }}</span></button>
        <button [class.active]="tab() === 'test'" (click)="tab.set('test')">Test</button>
      </div>

      @if (tab() === 'items') {
        <div class="toolbar" style="margin-bottom:10px">
          <input [(ngModel)]="q" placeholder="Search questions and answers…" style="max-width:320px" />
          <select [(ngModel)]="cat" style="width:auto"><option value="">All categories</option>@for (c of categories(); track c) {<option [value]="c">{{ c }}</option>}</select>
          <select [(ngModel)]="source" style="width:auto"><option value="">All answers</option><option value="own">Our answers</option><option value="built">Built-in</option></select>
          <span class="muted small">Built-in answers ship with the app; make a copy to change one.</span>
        </div>
        <div class="table-wrap" style="max-height:calc(100vh - 260px)">
          <table class="grid">
            <thead><tr><th>Question</th><th>Category</th><th>Who can see it</th><th>Source</th></tr></thead>
            <tbody>
              @for (i of shown(); track i.id) {
                <tr class="clickable" (click)="open(i)"><td>{{ i.question }}@if (i.variants.length) { <span class="muted small"> · +{{ i.variants.length }} wordings</span> }</td>
                  <td>{{ i.category }}</td><td class="small">{{ who(i) }}</td>
                  <td>@if (i.builtIn) { <span class="badge">Built-in</span> } @else { <span class="badge info">Ours</span> }</td></tr>
              } @empty { <tr><td colspan="4" class="empty">Nothing matches.</td></tr> }
            </tbody>
          </table>
        </div>
      }

      @if (tab() === 'log') {
        <p class="muted">Questions the chatbot couldn't answer, answers marked 👎, and topics people asked about that their role can't see. Add an answer and the bot knows it from then on.</p>
        <div class="table-wrap">
          <table class="grid">
            <thead><tr><th>Question asked</th><th>Why</th><th class="num">Times</th><th>Last asked</th><th></th></tr></thead>
            <tbody>
              @for (l of logs(); track l.id) {
                <tr><td>{{ l.text }}</td>
                  <td><span class="badge" [class.warn]="l.reason === 'no-access'" [class.danger]="l.reason === 'not-helpful'">{{ reasons[l.reason] }}</span></td>
                  <td class="num">{{ l.count }}</td><td class="nowrap small">{{ l.lastAskedAt | dmyhm }} · {{ l.lastUserName }}</td>
                  <td class="actions">
                    @if (auth.can('Faq', 'Add')) { <button class="btn sm primary" (click)="answerLog(l)">Add answer</button> }
                    <button class="btn sm" (click)="test(l.text)">Test</button>
                    @if (auth.can('Faq', 'Edit')) { <button class="btn sm ghost" (click)="dismiss(l)">Dismiss</button> }</td></tr>
              } @empty { <tr><td colspan="5" class="empty">Nothing here — the chatbot answered everything.</td></tr> }
            </tbody>
          </table>
        </div>
      }

      @if (tab() === 'test') {
        <div class="card"><section class="stack">
          <div class="toolbar"><input [(ngModel)]="testQ" (keydown.enter)="test(testQ)" placeholder="Type a question to see which answers match…" style="max-width:480px" />
            <button class="btn primary" (click)="test(testQ)">Test</button></div>
          @if (testResult(); as t) {
            <div class="small">Words understood: @for (w of t.understood; track w) {<code>{{ w }}</code> } @empty {<i>none</i>}</div>
            <table class="grid">
              <thead><tr><th>Matching question</th><th>Who can see it</th><th>Confidence</th></tr></thead>
              <tbody>@for (m of t.matches; track m.id) {
                <tr><td>{{ m.question }} <span class="muted small">· {{ m.category }}</span></td><td class="small">{{ screensText(m.screens) }}</td>
                  <td class="nowrap"><span class="meter"><i [style.width.%]="m.confidence * 100"></i></span>
                    {{ (m.confidence * 100).toFixed(0) }}% · {{ m.confidence >= t.answerAt ? 'answers' : m.confidence >= t.maybeAt ? 'answers (not sure)' : 'too weak' }}</td></tr>
              } @empty { <tr><td colspan="3" class="empty">No answer matches. Add one, or add these words as another wording of an existing question.</td></tr> }</tbody>
            </table>
            <p class="muted small">The bot answers at {{ t.answerAt * 100 }}% or more and says “I think you are asking…” from {{ t.maybeAt * 100 }}%. Each user only gets answers their role can see.</p>
          }
        </section></div>
      }
    </div>

    @if (view(); as v) {
      <app-modal [title]="v.question" size="lg" (closed)="view.set(null)">
        <div class="stack">
          <div class="muted small">{{ v.category }} · {{ who(v) }}@if (v.link) { · opens {{ v.link }} }</div>
          <div class="preview"><app-answer [text]="v.answer" /></div>
          @if (v.variants.length) { <div class="small"><b>Also matches:</b> {{ v.variants.join(' · ') }}</div> }
        </div>
        @if (auth.can('Faq', 'Add')) { <button footer class="btn" (click)="copy(v)">Make a copy to edit</button> }
      </app-modal>
    }

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit question' : 'New question'" size="lg" (closed)="form.set(null)" [dismissible]="false">
        <div class="form-grid cols-2">
          <label class="field req span-2"><span>Question</span><input [(ngModel)]="f.question" maxlength="300" placeholder="How do I …?" /></label>
          <label class="field span-2"><span>Other ways people ask it (one per line)</span>
            <textarea rows="3" [(ngModel)]="f.variantsText" placeholder="Where can I …&#10;Add a …"></textarea></label>
          <label class="field"><span>Category</span><input [(ngModel)]="f.category" list="faq-categories" maxlength="60" />
            <datalist id="faq-categories">@for (c of categories(); track c) {<option [value]="c"></option>}</datalist></label>
          <label class="field"><span>Keywords (comma separated)</span><input [(ngModel)]="f.tagsText" /></label>
          <label class="field req span-2"><span>Answer</span>
            <textarea rows="7" [(ngModel)]="f.answer" maxlength="6000" placeholder="Go to **Settings › …**&#10;1. Click …&#10;2. …"></textarea>
            <small class="muted">**bold**, lines starting with “- ” become bullets, “1. ” numbered steps.</small></label>
          @if (f.answer.trim()) { <div class="span-2 preview"><app-answer [text]="f.answer" /></div> }
          <div class="field span-2"><span>Who can see it — users whose role has the right below on any ticked screen (none ticked = everyone)</span>
            <div class="screen-picks">@for (s of screens(); track s.key) {
              <label class="check"><input type="checkbox" [(ngModel)]="f.screens[s.key]" /> {{ s.name }}</label>
            }</div></div>
          <label class="field"><span>Right needed</span><select [(ngModel)]="f.right">@for (r of rights; track r) {<option [value]="r">{{ r }}</option>}</select></label>
          <label class="field"><span>Open-screen link (optional)</span><input [(ngModel)]="f.link" placeholder="/settings/roles" maxlength="200" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        @if (f.id && auth.can('Faq', 'Delete')) { <button footer class="btn danger" (click)="remove(f)">Delete</button> }
        <button footer class="btn" (click)="test(f.question)">Test</button>
        <button footer class="btn primary" (click)="save(f)" [disabled]="!auth.can('Faq', f.id ? 'Edit' : 'Add')">Save</button>
      </app-modal>
    }`,
  styles: [`
    .preview { background: var(--surface-2); border: 1px solid var(--line); border-radius: var(--radius); padding: 10px 12px; }
    .screen-picks { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 6px 12px; max-height: 190px; overflow: auto;
      padding: 8px 10px; border: 1px solid var(--line); border-radius: var(--radius); }
    .meter { display: inline-block; width: 90px; height: 6px; border-radius: 3px; background: var(--surface-3); overflow: hidden; vertical-align: middle; margin-right: 6px; }
    .meter i { display: block; height: 100%; background: var(--primary); }
    code { margin-right: 4px; }
  `],
})
export class ManageFaqComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  tab = signal<'items' | 'log' | 'test'>('items');
  items = signal<FaqItem[]>([]);
  logs = signal<FaqLog[]>([]);
  screens = signal<{ key: string; name: string }[]>([]);
  view = signal<FaqItem | null>(null);
  form = signal<EntryForm | null>(null);
  testResult = signal<TestResult | null>(null);
  rights: RightName[] = ['Read', 'Add', 'Edit', 'Cancel', 'Delete', 'Print', 'Export', 'Email', 'Sms'];
  reasons: Record<FaqLog['reason'], string> = { 'no-match': 'No answer found', 'not-helpful': '👎 Not helpful', 'no-access': 'Outside their role' };
  q = '';
  cat = '';
  source = '';
  testQ = '';

  categories = computed(() => [...new Set(this.items().map(i => i.category))].sort());

  async ngOnInit(): Promise<void> {
    try {
      const [items, screens] = await Promise.all([
        this.api.get<FaqItem[]>('/faq/items'),
        this.api.get<{ key: string; name: string }[]>('/faq/screens'),
      ]);
      this.items.set(items);
      this.screens.set(screens);
    } catch (e) { this.ui.error(errorMessage(e)); }
    await this.loadLogs();
  }

  shown(): FaqItem[] {
    const q = this.q.trim().toLowerCase();
    return this.items()
      .filter(i => !this.cat || i.category === this.cat)
      .filter(i => !this.source || (this.source === 'own') === !i.builtIn)
      .filter(i => !q || [i.question, i.answer, i.category, ...i.variants, ...i.tags].join(' ').toLowerCase().includes(q));
  }

  screenName(key: string): string { return this.screens().find(s => s.key === key)?.name ?? key; }
  screensText(screens: string[]): string { return screens.length ? screens.map(s => this.screenName(s)).join(', ') : 'Everyone'; }
  who(i: FaqItem): string { return i.screens.length ? `${this.screensText(i.screens)}${i.right !== 'Read' ? ` (${i.right})` : ''}` : 'Everyone'; }

  async loadItems(): Promise<void> { this.items.set(await this.api.get<FaqItem[]>('/faq/items')); }
  async loadLogs(): Promise<void> { try { this.logs.set(await this.api.get<FaqLog[]>('/faq/unanswered')); } catch { /* optional */ } }

  private blank(question = ''): EntryForm {
    return { id: 0, question, variantsText: '', answer: '', category: '', tagsText: '', screens: {}, right: 'Read', link: '', isActive: true };
  }

  newEntry(): void { this.form.set(this.blank()); }

  async open(i: FaqItem): Promise<void> {
    if (i.builtIn || !i.entryId) { this.view.set(i); return; }
    try {
      const e = await this.api.get<FaqEntry>(`/faq/entries/${i.entryId}`);
      this.form.set({
        id: e.id, question: e.question, variantsText: e.variants.join('\n'), answer: e.answer, category: e.category,
        tagsText: e.tags.join(', '), screens: Object.fromEntries(e.screens.map(s => [s, true])), right: e.right, link: e.link ?? '', isActive: e.isActive,
      });
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  copy(i: FaqItem): void {
    this.view.set(null);
    this.form.set({
      id: 0, question: i.question, variantsText: i.variants.join('\n'), answer: i.answer, category: i.category, tagsText: i.tags.join(', '),
      screens: Object.fromEntries(i.screens.map(s => [s, true])), right: i.right, link: i.link ?? '', isActive: true,
    });
  }

  answerLog(l: FaqLog): void { this.form.set({ ...this.blank(l.text), fromLog: l.id }); }

  async save(f: EntryForm): Promise<void> {
    const body = {
      question: f.question, variants: f.variantsText.split('\n').map(s => s.trim()).filter(Boolean), answer: f.answer,
      category: f.category, tags: f.tagsText.split(',').map(s => s.trim()).filter(Boolean),
      screens: Object.entries(f.screens).filter(([, on]) => on).map(([k]) => k), right: f.right, link: f.link || null, isActive: f.isActive,
    };
    try {
      await this.ui.run(() => f.id ? this.api.put(`/faq/entries/${f.id}`, body) : this.api.post('/faq/entries', body));
      if (f.fromLog) { try { await this.api.delete(`/faq/unanswered/${f.fromLog}`); } catch { /* already gone */ } }
      this.form.set(null);
      this.ui.ok('Saved. The chatbot uses it right away.');
      await Promise.all([this.loadItems(), this.loadLogs()]);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async remove(f: EntryForm): Promise<void> {
    if (!(await this.ui.confirm('Delete question', `Delete “${f.question}”?`, { danger: true, okText: 'Delete' }))) return;
    try { await this.api.delete(`/faq/entries/${f.id}`); this.form.set(null); await this.loadItems(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  async dismiss(l: FaqLog): Promise<void> {
    try { await this.api.delete(`/faq/unanswered/${l.id}`); await this.loadLogs(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  async test(q: string): Promise<void> {
    q = (q ?? '').trim();
    if (!q) return;
    this.testQ = q;
    this.form.set(null);
    this.tab.set('test');
    try { this.testResult.set(await this.api.get<TestResult>('/faq/test', { q })); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}
