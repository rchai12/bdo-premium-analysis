import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration, ChartData } from 'chart.js';
import { ApiService } from '../../core/services/api.service';
import { Velocity } from '../../core/models/velocity.model';

@Component({
  selector: 'app-item-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    BaseChartDirective,
  ],
  templateUrl: './item-detail.component.html',
  styleUrl: './item-detail.component.scss',
})
export class ItemDetailComponent implements OnInit {
  velocity: Velocity | null = null;
  loading = true;
  error = '';
  itemId = 0;

  // Sales velocity bar chart
  velocityChartData: ChartData<'bar'> = {
    labels: [],
    datasets: [],
  };

  velocityChartOptions: ChartConfiguration<'bar'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { display: false },
    },
    scales: {
      y: {
        beginAtZero: true,
        title: { display: true, text: 'Sales / Hour' },
      },
    },
  };

  // Preorders bar chart
  preorderChartData: ChartData<'bar'> = {
    labels: [],
    datasets: [],
  };

  preorderChartOptions: ChartConfiguration<'bar'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { display: false },
    },
    scales: {
      y: {
        beginAtZero: true,
        title: { display: true, text: 'Avg Preorders' },
      },
    },
  };

  constructor(
    private route: ActivatedRoute,
    private api: ApiService
  ) {}

  ngOnInit(): void {
    this.itemId = Number(this.route.snapshot.paramMap.get('id'));
    this.loadVelocity();
  }

  loadVelocity(): void {
    this.loading = true;
    this.api.getVelocity(this.itemId).subscribe({
      next: (data) => {
        this.velocity = data;
        this.buildCharts(data);
        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to load item data.';
        this.loading = false;
        console.error(err);
      },
    });
  }

  private buildCharts(data: Velocity): void {
    const labels = data.windows.map((w) => w.window);

    this.velocityChartData = {
      labels,
      datasets: [
        {
          data: data.windows.map((w) => w.salesPerHour),
          backgroundColor: '#42a5f5',
          borderRadius: 4,
          label: 'Sales/Hr',
        },
      ],
    };

    this.preorderChartData = {
      labels,
      datasets: [
        {
          data: data.windows.map((w) => w.avgPreorders),
          backgroundColor: '#ef5350',
          borderRadius: 4,
          label: 'Avg Preorders',
        },
      ],
    };
  }
}
