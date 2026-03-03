export interface DashboardItem {
  itemId: number;
  name: string;
  grade: number;
  basePrice: number;
  currentStock: number;
  totalPreorders: number;
  salesPerHour: number;
  window: string;
  fulfillmentScore: number;
  estimatedFillTime: string;
}
