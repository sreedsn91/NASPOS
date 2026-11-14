import { HttpClient, HttpErrorResponse, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Query = Record<string, string | number | boolean | null | undefined>;

/** Thin typed wrapper over HttpClient; every call goes to /api (proxied in dev, same-origin in prod). */
@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);

  private params(q?: Query): HttpParams {
    let p = new HttpParams();
    if (q) for (const [k, v] of Object.entries(q)) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
    return p;
  }

  get<T>(url: string, q?: Query): Promise<T> {
    return firstValueFrom(this.http.get<T>('/api' + url, { params: this.params(q) }));
  }
  post<T>(url: string, body: unknown = {}, q?: Query): Promise<T> {
    return firstValueFrom(this.http.post<T>('/api' + url, body, { params: this.params(q) }));
  }
  put<T>(url: string, body: unknown = {}, q?: Query): Promise<T> {
    return firstValueFrom(this.http.put<T>('/api' + url, body, { params: this.params(q) }));
  }
  delete<T = void>(url: string, q?: Query): Promise<T> {
    return firstValueFrom(this.http.delete<T>('/api' + url, { params: this.params(q) }));
  }
  upload<T>(url: string, form: FormData, q?: Query, method: 'POST' | 'PUT' = 'POST'): Promise<T> {
    return firstValueFrom(method === 'POST'
      ? this.http.post<T>('/api' + url, form, { params: this.params(q) })
      : this.http.put<T>('/api' + url, form, { params: this.params(q) }));
  }

  /** Downloads a file (GET or POST) and hands it to the browser. */
  async download(url: string, body?: unknown, q?: Query, fallbackName = 'download'): Promise<void> {
    const res: HttpResponse<Blob> = await firstValueFrom(body === undefined
      ? this.http.get('/api' + url, { params: this.params(q), responseType: 'blob', observe: 'response' })
      : this.http.post('/api' + url, body, { params: this.params(q), responseType: 'blob', observe: 'response' }));
    const disposition = res.headers.get('Content-Disposition') ?? '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    saveBlob(res.body!, match ? decodeURIComponent(match[1]) : fallbackName);
  }

  /** Opens a PDF (GET or POST) in a new tab for viewing / printing. */
  async openPdf(url: string, body?: unknown, q?: Query): Promise<void> {
    const win = window.open('', '_blank');
    try {
      const blob = await firstValueFrom(body === undefined
        ? this.http.get('/api' + url, { params: this.params(q), responseType: 'blob' })
        : this.http.post('/api' + url, body, { params: this.params(q), responseType: 'blob' }));
      const objectUrl = URL.createObjectURL(blob);
      if (win) win.location.href = objectUrl; else window.open(objectUrl, '_blank');
      setTimeout(() => URL.revokeObjectURL(objectUrl), 60000);
    } catch (e) {
      win?.close();
      throw e;
    }
  }

  /**
   * Prints a PDF through a hidden iframe (print-on-save) as its own print job. Resolves once the print call has returned
   * (in Chrome / Edge that is after the print dialog closes), so callers can print several jobs one after another.
   */
  async printPdf(url: string, q?: Query): Promise<void> {
    const blob = await firstValueFrom(this.http.get('/api' + url, { params: this.params(q), responseType: 'blob' }));
    const objectUrl = URL.createObjectURL(blob);
    const frame = document.createElement('iframe');
    frame.style.position = 'fixed';
    frame.style.right = '0';
    frame.style.bottom = '0';
    frame.style.width = '0';
    frame.style.height = '0';
    frame.style.border = '0';
    setTimeout(() => { frame.remove(); URL.revokeObjectURL(objectUrl); }, 120000);
    await new Promise<void>(resolve => {
      frame.onload = () => {
        // The PDF viewer inside the frame needs a moment after load before print() picks up the document.
        setTimeout(() => {
          try { frame.contentWindow?.focus(); frame.contentWindow?.print(); } catch { window.open(objectUrl, '_blank'); resolve(); return; }
          // If the print dialog is still open (print() did not block), wait until it closes and the page has focus again;
          // with silent (kiosk) printing the page keeps focus and the next job goes straight away.
          setTimeout(() => {
            if (document.hasFocus()) { resolve(); return; }
            const done = () => { window.removeEventListener('focus', done); resolve(); };
            window.addEventListener('focus', done);
            setTimeout(done, 120000);
          }, 800);
        }, 300);
      };
      frame.src = objectUrl;
      document.body.appendChild(frame);
    });
  }

  async text(url: string, q?: Query): Promise<string> {
    return firstValueFrom(this.http.get('/api' + url, { params: this.params(q), responseType: 'text' }));
  }
}

export function saveBlob(blob: Blob, name: string): void {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 30000);
}

/** Human-readable message from a ProblemDetails / HTTP error. */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error;
    if (body && typeof body === 'object') {
      if (Array.isArray(body.warnings) && body.warnings.length) return body.warnings.join(' ');
      if (body.errors && typeof body.errors === 'object') {
        return Object.values(body.errors as Record<string, string[]>).flat().join(' ');
      }
      if (body.title) return body.title + (body.detail ? ` ${body.detail}` : '');
    }
    if (typeof body === 'string' && body.length < 300) return body;
    if (err.status === 0) return 'Cannot reach the server.';
    return `${err.status} ${err.statusText}`;
  }
  return err instanceof Error ? err.message : String(err);
}

export function errorCode(err: unknown): string | undefined {
  return err instanceof HttpErrorResponse && err.error && typeof err.error === 'object' ? err.error.code : undefined;
}
