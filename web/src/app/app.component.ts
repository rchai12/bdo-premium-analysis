import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet, Router } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from './core/services/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, RouterOutlet, MatToolbarModule, MatButtonModule, MatIconModule],
  template: `
    @if (auth.isLoggedIn$ | async) {
      <mat-toolbar color="primary">
        <span>BDO Market Tracker</span>
        <span style="flex: 1"></span>
        <span style="font-size: 14px; opacity: 0.8; margin-right: 16px">NA Region</span>
        <button mat-icon-button (click)="auth.logout()" aria-label="Logout">
          <mat-icon>logout</mat-icon>
        </button>
      </mat-toolbar>
    }
    <router-outlet />
  `,
  styles: [`
    mat-toolbar {
      position: sticky;
      top: 0;
      z-index: 100;
    }
  `]
})
export class AppComponent {
  constructor(public auth: AuthService) {}
}
