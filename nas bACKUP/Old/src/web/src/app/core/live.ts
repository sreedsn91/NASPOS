import { Injectable, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { Auth } from './auth';

export interface DocumentSavedEvent {
  id: number; documentNo: string; voucherType: string; billAmount?: number; paymentMode?: string; cashAmount?: number;
  cardAmount?: number; creditAmount?: number; counter?: string; action: string;
}

/** SignalR connection: live counter updates, stock/master invalidation and soft edit locks. */
@Injectable({ providedIn: 'root' })
export class Live {
  private auth = inject(Auth);
  private hub: HubConnection | null = null;
  readonly connected = signal(false);
  readonly documentSaved = new Subject<DocumentSavedEvent>();
  readonly stockChanged = new Subject<number[]>();
  readonly masterChanged = new Subject<string>();
  readonly locks = signal<Record<string, string>>({});

  async start(): Promise<void> {
    if (this.hub && this.hub.state !== HubConnectionState.Disconnected) return;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/live', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('documentSaved', (e: DocumentSavedEvent) => this.documentSaved.next(e));
    this.hub.on('stockChanged', (ids: number[]) => this.stockChanged.next(ids));
    this.hub.on('masterChanged', (k: string) => this.masterChanged.next(k));
    this.hub.on('locked', (key: string, user: string) => this.locks.update(l => ({ ...l, [key]: user })));
    this.hub.on('unlocked', (key: string) => this.locks.update(l => { const c = { ...l }; delete c[key]; return c; }));
    this.hub.onreconnected(() => this.connected.set(true));
    this.hub.onclose(() => this.connected.set(false));
    try {
      await this.hub.start();
      this.connected.set(true);
    } catch {
      this.connected.set(false);
    }
  }

  async stop(): Promise<void> {
    await this.hub?.stop();
    this.hub = null;
    this.connected.set(false);
  }

  /** Returns the user already editing this document, if any. */
  async open(key: string): Promise<string | null> {
    if (this.hub?.state !== HubConnectionState.Connected) return null;
    try { return await this.hub.invoke<string | null>('OpenDocument', key); } catch { return null; }
  }

  async close(key: string): Promise<void> {
    if (this.hub?.state === HubConnectionState.Connected) {
      try { await this.hub.invoke('CloseDocument', key); } catch { /* ignore */ }
    }
  }
}
