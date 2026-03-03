import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DashboardItem } from '../models/dashboard-item.model';
import { Velocity } from '../models/velocity.model';
import { environment } from '../../../environments/environment';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getDashboard(window: string = '24h'): Observable<DashboardItem[]> {
    return this.http.get<DashboardItem[]>(`${this.baseUrl}/api/items/dashboard`, {
      params: { window }
    });
  }

  getVelocity(itemId: number): Observable<Velocity> {
    return this.http.get<Velocity>(`${this.baseUrl}/api/items/${itemId}/velocity`);
  }
}
