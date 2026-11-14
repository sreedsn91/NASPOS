import { HttpClient } from '@angular/common/http';
import { Directive, ElementRef, Injectable, OnChanges, OnDestroy, inject, input } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/**
 * Image bytes from the API, fetched through HttpClient so the auth interceptor adds the bearer token
 * (a plain <img src="/api/..."> carries no token and gets 401). Kept as object URLs for the session.
 */
@Injectable({ providedIn: 'root' })
export class ImageCache {
  private http = inject(HttpClient);
  private entries = new Map<string, Promise<string | null>>();
  /** Bumped on invalidate so refetches skip the browser's HTTP cache (the API sends max-age=300). */
  private epoch = 0;
  private static readonly Max = 400;

  /** Object URL for the image, or null when there is none (404, empty body) or it can't be loaded. */
  get(url: string): Promise<string | null> {
    let p = this.entries.get(url);
    if (!p) {
      p = this.fetch(url);
      this.entries.set(url, p);
      this.trim();
    }
    return p;
  }

  /** Drops cached images whose URL starts with the prefix, e.g. after uploading or removing one. */
  invalidate(prefix: string): void {
    this.epoch++;
    for (const [url, p] of [...this.entries]) {
      if (!url.startsWith(prefix)) continue;
      this.entries.delete(url);
      p.then(o => o && URL.revokeObjectURL(o));
    }
  }

  private async fetch(url: string): Promise<string | null> {
    try {
      const bust = this.epoch ? `${url.includes('?') ? '&' : '?'}_=${this.epoch}` : '';
      const blob = await firstValueFrom(this.http.get(url + bust, { responseType: 'blob' }));
      return blob.size > 0 ? URL.createObjectURL(blob) : null;
    } catch {
      this.entries.delete(url); // let a later attempt retry (e.g. after a token refresh)
      return null;
    }
  }

  /** Oldest entries go first; an <img> already showing a revoked URL keeps its decoded picture. */
  private trim(): void {
    while (this.entries.size > ImageCache.Max) {
      const [url, p] = this.entries.entries().next().value!;
      this.entries.delete(url);
      p.then(o => o && URL.revokeObjectURL(o));
    }
  }
}

/**
 * `<img [appAuthSrc]="'/api/products/' + id + '/image'">` — loads an authenticated API image. When there is no image
 * the element fires a normal `error` event, so existing `(error)` handlers keep working. With `loading="lazy"` the
 * fetch waits until the image scrolls into view.
 */
@Directive({ selector: 'img[appAuthSrc]', standalone: true })
export class AuthImageDirective implements OnChanges, OnDestroy {
  url = input<string | null | undefined>(null, { alias: 'appAuthSrc' });
  private el: HTMLImageElement = inject(ElementRef).nativeElement;
  private cache = inject(ImageCache);
  private seq = 0;
  private observer?: IntersectionObserver;

  ngOnChanges(): void {
    const url = this.url();
    const seq = ++this.seq;
    this.observer?.disconnect();
    this.el.removeAttribute('src');
    if (!url) return;
    const load = () => this.cache.get(url).then(src => {
      if (seq !== this.seq) return; // a newer URL replaced this one meanwhile
      if (src) this.el.src = src;
      else this.el.dispatchEvent(new Event('error'));
    });
    if (this.el.getAttribute('loading') === 'lazy' && 'IntersectionObserver' in window) {
      this.observer = new IntersectionObserver(es => {
        if (es.some(e => e.isIntersecting)) { this.observer?.disconnect(); load(); }
      }, { rootMargin: '200px' });
      this.observer.observe(this.el);
    } else load();
  }

  ngOnDestroy(): void { this.seq++; this.observer?.disconnect(); }
}
