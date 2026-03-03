import { Component, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MatTableModule, MatTableDataSource } from '@angular/material/table';
import { MatSortModule, MatSort } from '@angular/material/sort';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { ApiService } from '../../core/services/api.service';
import { DashboardItem } from '../../core/models/dashboard-item.model';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatSortModule,
    MatCardModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    MatIconModule,
    MatButtonToggleModule,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit {
  displayedColumns = [
    'name',
    'basePrice',
    'currentStock',
    'totalPreorders',
    'salesPerHour',
    'estimatedFillTime',
    'fulfillmentScore',
  ];

  dataSource = new MatTableDataSource<DashboardItem>([]);
  loading = true;
  error = '';

  totalItems = 0;
  fastestSeller = '';
  bestFillTime = '';

  timeWindows = ['3h', '12h', '24h', '3d', '7d', '14d'];
  selectedWindow = '24h';

  @ViewChild(MatSort) set matSort(sort: MatSort) {
    if (sort) {
      this.dataSource.sort = sort;
    }
  }

  constructor(
    private api: ApiService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.loadDashboard();
  }

  onWindowChange(window: string): void {
    this.selectedWindow = window;
    this.loadDashboard();
  }

  loadDashboard(): void {
    this.loading = true;
    this.error = '';
    this.api.getDashboard(this.selectedWindow).subscribe({
      next: (items) => {
        this.dataSource.data = items;
        this.totalItems = items.length;

        if (items.length > 0) {
          const fastest = items.reduce((a, b) =>
            a.fulfillmentScore > b.fulfillmentScore ? a : b
          );
          this.fastestSeller = fastest.name;

          const validItems = items.filter(
            (i) => i.estimatedFillTime !== 'N/A'
          );
          this.bestFillTime =
            validItems.length > 0
              ? validItems[0].estimatedFillTime
              : 'N/A';
        }

        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to load dashboard data. Is the API running?';
        this.loading = false;
        console.error(err);
      },
    });
  }

  onRowClick(item: DashboardItem): void {
    this.router.navigate(['/item', item.itemId]);
  }

  formatPrice(price: number): string {
    if (price >= 1_000_000_000) return (price / 1_000_000_000).toFixed(1) + 'B';
    if (price >= 1_000_000) return (price / 1_000_000).toFixed(0) + 'M';
    if (price >= 1_000) return (price / 1_000).toFixed(0) + 'K';
    return price.toString();
  }

  getFulfillmentColor(score: number): string {
    if (score >= 0.05) return 'good';
    if (score >= 0.01) return 'moderate';
    return 'slow';
  }
}
