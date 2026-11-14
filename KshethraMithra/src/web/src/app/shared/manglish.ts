import { Directive, ElementRef, HostBinding, HostListener, OnInit, inject } from '@angular/core';

/*
 * Manglish typing: Malayalam written with English letters, converted as you type ("karthika" → കാർത്തിക).
 * Works offline, with no Windows keyboard set up. Scheme (close to Mozhi):
 *   vowels   a ആ=aa/A  i  ഈ=ii/ee/I  u  ഊ=uu/oo/U  e  ഏ=E  ai  o  ഓ=O  au
 *   t ട  th ത  T ട  Th ഠ  d ദ  D ഡ  n ന  N ണ  l ല  L ള  r ര  R റ  zh ഴ  s സ  S ശ  sh ഷ (ശ at the start)
 *   nt ന്റ  nth ന്ത  ng ങ്ങ  nj ഞ  ch ച  f ഫ  w വ  x ക്സ;  a word ending in n l L r m becomes ൻ ൽ ൾ ർ ം.
 * Common temple words (stars, deities, offerings) come from a small dictionary so casual spellings are right.
 */

const VIRAMA = '്';

/** [latin, independent vowel, vowel sign], longest first. */
const VOWELS: [string, string, string][] = [
  ['aa', 'ആ', 'ാ'], ['ai', 'ഐ', 'ൈ'], ['au', 'ഔ', 'ൗ'], ['ii', 'ഈ', 'ീ'], ['ee', 'ഈ', 'ീ'], ['uu', 'ഊ', 'ൂ'], ['oo', 'ഊ', 'ൂ'],
  ['A', 'ആ', 'ാ'], ['I', 'ഈ', 'ീ'], ['U', 'ഊ', 'ൂ'], ['E', 'ഏ', 'േ'], ['O', 'ഓ', 'ോ'],
  ['a', 'അ', ''], ['i', 'ഇ', 'ി'], ['u', 'ഉ', 'ു'], ['e', 'എ', 'െ'], ['o', 'ഒ', 'ൊ'],
];

/** [latin, consonant or conjunct], longest first. Case matters (t/T, n/N, l/L, r/R, s/S). */
const CONSONANTS: [string, string][] = [
  ['nth', 'ന്ത'], ['shr', 'ശ്ര'], ['chh', 'ഛ'], ['thh', 'ഥ'], ['tth', 'ത്ത'], ['cch', 'ച്ച'],
  ['kh', 'ഖ'], ['gh', 'ഘ'], ['ng', 'ങ്ങ'], ['nk', 'ങ്ക'], ['ch', 'ച'], ['jh', 'ഝ'], ['nj', 'ഞ'],
  ['Th', 'ഠ'], ['Dh', 'ഢ'], ['th', 'ത'], ['dh', 'ധ'], ['nt', 'ന്റ'], ['ph', 'ഫ'], ['bh', 'ഭ'],
  ['zh', 'ഴ'], ['Sh', 'ഷ'], ['sh', 'ഷ'], ['sr', 'ശ്ര'],
  ['k', 'ക'], ['c', 'ക'], ['q', 'ക'], ['g', 'ഗ'], ['j', 'ജ'], ['T', 'ട'], ['t', 'ട'], ['D', 'ഡ'], ['N', 'ണ'],
  ['d', 'ദ'], ['n', 'ന'], ['p', 'പ'], ['f', 'ഫ'], ['b', 'ബ'], ['m', 'മ'], ['y', 'യ'], ['r', 'ര'], ['R', 'റ'],
  ['l', 'ല'], ['L', 'ള'], ['v', 'വ'], ['w', 'വ'], ['S', 'ശ'], ['s', 'സ'], ['z', 'സ'], ['h', 'ഹ'], ['x', 'ക്സ'],
];

/** A word ending in one of these consonants takes the chillu (or anusvara) form. */
const WORD_END: Record<string, string> = { n: 'ൻ', N: 'ൺ', l: 'ൽ', L: 'ൾ', r: 'ർ', R: 'ർ', m: 'ം' };

/** Whole words whose usual English spelling the rules can't get right. Keys are lower case. */
const WORDS: Record<string, string> = {
  // the 27 stars
  aswathi: 'അശ്വതി', ashwathi: 'അശ്വതി', aswini: 'അശ്വതി', bharani: 'ഭരണി', karthika: 'കാർത്തിക', karthik: 'കാർത്തിക', karthiga: 'കാർത്തിക',
  rohini: 'രോഹിണി', makayiram: 'മകയിരം', makeeryam: 'മകയിരം', thiruvathira: 'തിരുവാതിര', thiruvaathira: 'തിരുവാതിര', aathira: 'ആതിര', athira: 'ആതിര',
  punartham: 'പുണർതം', pooyam: 'പൂയം', puyam: 'പൂയം', ayilyam: 'ആയില്യം', aayilyam: 'ആയില്യം', makam: 'മകം', pooram: 'പൂരം', uthram: 'ഉത്രം',
  atham: 'അത്തം', attham: 'അത്തം', chithira: 'ചിത്തിര', chithra: 'ചിത്തിര', chothi: 'ചോതി', vishakham: 'വിശാഖം', visakham: 'വിശാഖം',
  anizham: 'അനിഴം', anizhom: 'അനിഴം', thrikketta: 'തൃക്കേട്ട', kettah: 'തൃക്കേട്ട', ketta: 'തൃക്കേട്ട', moolam: 'മൂലം', pooradam: 'പൂരാടം',
  uthradam: 'ഉത്രാടം', thiruvonam: 'തിരുവോണം', onam: 'ഓണം', avittam: 'അവിട്ടം', chathayam: 'ചതയം', pururuttathi: 'പൂരുരുട്ടാതി',
  pooruruttathi: 'പൂരുരുട്ടാതി', uthrattathi: 'ഉത്രട്ടാതി', uthruttathi: 'ഉത്രട്ടാതി', revathi: 'രേവതി',
  // deities and names
  sree: 'ശ്രീ', sri: 'ശ്രീ', shri: 'ശ്രീ', krishna: 'കൃഷ്ണ', krishnan: 'കൃഷ്ണൻ', devi: 'ദേവി', deva: 'ദേവ', devan: 'ദേവൻ',
  bhagavathi: 'ഭഗവതി', bhagavathy: 'ഭഗവതി', shiva: 'ശിവ', siva: 'ശിവ', shivan: 'ശിവൻ', sivan: 'ശിവൻ', vishnu: 'വിഷ്ണു',
  ganapathi: 'ഗണപതി', ganapathy: 'ഗണപതി', ayyappan: 'അയ്യപ്പൻ', sastha: 'ശാസ്താ', shastha: 'ശാസ്താ', murugan: 'മുരുകൻ',
  subramanian: 'സുബ്രഹ്മണ്യൻ', subrahmanyan: 'സുബ്രഹ്മണ്യൻ', durga: 'ദുർഗ്ഗ', guruvayurappan: 'ഗുരുവായൂരപ്പൻ', lakshmi: 'ലക്ഷ്മി',
  kumar: 'കുമാർ', kumari: 'കുമാരി', rajan: 'രാജൻ', raj: 'രാജ്', nair: 'നായർ', menon: 'മേനോൻ', pillai: 'പിള്ള', varma: 'വർമ്മ',
  namboothiri: 'നമ്പൂതിരി', potti: 'പോറ്റി', njan: 'ഞാൻ', amma: 'അമ്മ', thrissur: 'തൃശ്ശൂർ', malayalam: 'മലയാളം', kerala: 'കേരളം',
  saraswathi: 'സരസ്വതി', hanuman: 'ഹനുമാൻ', narayanan: 'നാരായണൻ', bhadrakali: 'ഭദ്രകാളി', kali: 'കാളി', nagaraja: 'നാഗരാജ',
  // offerings
  pooja: 'പൂജ', puja: 'പൂജ', archana: 'അർച്ചന', pushpanjali: 'പുഷ്പാഞ്ജലി', payasam: 'പായസം', nivedyam: 'നിവേദ്യം',
  homam: 'ഹോമം', vilakku: 'വിളക്ക്', neyvilakku: 'നെയ്‌വിളക്ക്', abhishekam: 'അഭിഷേകം', thulabharam: 'തുലാഭാരം', mala: 'മാല',
  kshethram: 'ക്ഷേത്രം', temple: 'ക്ഷേത്രം', nadayil: 'നടയിൽ', ganapathihomam: 'ഗണപതിഹോമം', bhagavathiseva: 'ഭഗവതിസേവ',
};

function match<T extends [string, ...string[]]>(table: T[], word: string, i: number): T | undefined {
  return table.find(([latin]) => word.startsWith(latin, i));
}

/** Converts one Manglish word to Malayalam. Non-letters are kept as they are. */
export function manglishWord(word: string): string {
  if (!word) return '';
  const lower = word.toLowerCase();
  const known = WORDS[lower];
  if (known) return known;
  // Sree/Sri joined to a name: sreekumar → ശ്രീകുമാർ.
  const prefix = ['shree', 'sree', 'shri', 'sri'].find(p => lower.startsWith(p) && lower.length > p.length);
  if (prefix) return 'ശ്രീ' + manglishWord(word.slice(prefix.length));
  // Phones and some keyboards capitalise the first letter; Malayalam words rarely start with ട ഡ ണ ള റ.
  const w = word[0].toLowerCase() + word.slice(1);
  let out = '';
  let pending: string | null = null; // latin of a consonant still waiting for its vowel
  for (let i = 0; i < w.length;) {
    const v = match(VOWELS, w, i) ?? match(VOWELS, w.toLowerCase(), i);
    if (v) {
      out += pending !== null ? v[2] : v[1];
      pending = null;
      i += v[0].length;
      continue;
    }
    let c = match(CONSONANTS, w, i) ?? match(CONSONANTS, w.toLowerCase(), i);
    if (c) {
      if (i === 0 && (c[0] === 'sh' || c[0] === 'Sh')) c = ['sh', 'ശ'];
      if (pending !== null) {
        // ര before another consonant is written as the chillu: karthika → കാർത്തിക, sarva → സർവ (but ര്യ stays).
        out = pending === 'r' && c[0] !== 'y' ? out.slice(0, -1) + 'ർ' : out + VIRAMA;
      }
      out += c[1];
      pending = c[0];
      i += c[0].length;
      continue;
    }
    if (w[i] === 'H') { out += 'ഃ'; pending = null; i++; continue; }
    out += w[i];
    pending = null;
    i++;
  }
  if (pending !== null) out = WORD_END[pending] ? out.slice(0, -1) + WORD_END[pending] : out + VIRAMA;
  return out;
}

/** Converts Manglish text word by word; anything that isn't a Latin letter (spaces, digits, Malayalam) is kept. */
export function manglish(text: string): string {
  return text.replace(/[A-Za-z]+/g, manglishWord);
}

/**
 * Turns an input into a Malayalam field you can type in Manglish. The word being typed is kept in English letters
 * behind the scenes and re-converted on every key, so Backspace removes one English letter. Ctrl+M switches
 * Manglish off and on (for English text in the field). A Windows Malayalam keyboard still works as usual.
 */
@Directive({ selector: 'input[appManglish]', standalone: true })
export class ManglishDirective implements OnInit {
  private el = inject<ElementRef<HTMLInputElement>>(ElementRef).nativeElement;
  /** The word being typed: where it starts, its English letters and what is shown for it. */
  private word: { start: number; latin: string; shown: string } | null = null;
  @HostBinding('class.manglish') on = true;
  @HostBinding('attr.lang') lang = 'ml';
  @HostBinding('attr.title') get title(): string {
    return this.on ? 'Type Malayalam in English letters (e.g. karthika). Ctrl+M: type English.' : 'English typing. Ctrl+M: type Malayalam.';
  }

  ngOnInit(): void {
    if (!this.el.placeholder) this.el.placeholder = 'Type in English letters, e.g. karthika';
  }

  @HostListener('keydown', ['$event'])
  key(e: KeyboardEvent): void {
    if (e.ctrlKey && !e.altKey && (e.key === 'm' || e.key === 'M')) {
      e.preventDefault();
      this.on = !this.on;
      this.word = null;
    }
  }

  @HostListener('beforeinput', ['$event'])
  beforeInput(e: InputEvent): void {
    if (!this.on || e.isComposing) { this.word = null; return; }
    const el = this.el, at = el.selectionStart ?? el.value.length, collapsed = at === el.selectionEnd;
    const w = this.word;
    const continuing = !!w && collapsed && at === w.start + w.shown.length && el.value.slice(w.start, at) === w.shown;

    if (e.inputType === 'insertText' && e.data && /^[A-Za-z]$/.test(e.data)) {
      e.preventDefault();
      if (continuing) this.render(w!.start, w!.shown.length, w!.latin + e.data);
      else this.render(Math.min(at, el.selectionEnd ?? at), Math.abs((el.selectionEnd ?? at) - at), e.data);
      return;
    }
    if (e.inputType === 'deleteContentBackward' && continuing) {
      e.preventDefault();
      this.render(w!.start, w!.shown.length, w!.latin.slice(0, -1));
      return;
    }
    this.word = null; // space, digits, paste, a click elsewhere: the word is finished
  }

  @HostListener('blur') blur(): void { this.word = null; }

  /** Replaces `length` characters at `start` with the Malayalam for `latin` and puts the caret after it. */
  private render(start: number, length: number, latin: string): void {
    const el = this.el, shown = manglishWord(latin);
    el.value = el.value.slice(0, start) + shown + el.value.slice(start + length);
    const caret = start + shown.length;
    el.setSelectionRange(caret, caret);
    this.word = latin ? { start, latin, shown } : null;
    el.dispatchEvent(new Event('input', { bubbles: true })); // lets ngModel see the new value
  }
}
