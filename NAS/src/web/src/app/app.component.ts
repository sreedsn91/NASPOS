import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { UiHostComponent } from './core/ui';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, UiHostComponent],
  template: `<router-outlet /><app-ui-host />`,
})
export class AppComponent {}
