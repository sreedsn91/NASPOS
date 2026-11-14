import { Component, input } from '@angular/core';

/** Kshethra Mithra logo (receipt with a tick). The tick uses --logo-accent, so it picks up the active theme. */
@Component({
  selector: 'app-logo',
  standalone: true,
  host: { class: 'logo', '[style.width.px]': 'size()', '[style.height.px]': 'size()' },
  template: `
    <svg viewBox="0 0 64 64" [attr.width]="size()" [attr.height]="size()" role="img" aria-label="Kshethra Mithra">
      <rect x="2" y="2" width="60" height="60" rx="15" fill="#0F4C5C" />
      <path d="M19 12 H45 V51 L41.6 48.4 L38.3 51 L35 48.4 L31.7 51 L28.4 48.4 L25.1 51 L21.8 48.4 L19 51 Z" fill="#FAF7F0" />
      <rect x="24" y="18" width="16" height="2.6" rx="1.3" fill="#0F4C5C" opacity="0.35" />
      <rect x="24" y="23.5" width="11" height="2.6" rx="1.3" fill="#0F4C5C" opacity="0.35" />
      <path d="M24.5 36 L29.5 41 L40 30" fill="none" style="stroke: var(--logo-accent, #E89B2E)" stroke-width="4.2" stroke-linecap="round" stroke-linejoin="round" />
    </svg>`,
})
export class LogoComponent {
  size = input(32);
}
