export interface DashboardItem {
  itemId: number;
  name: string;
  grade: number;
  totalPreorders: number;
  salesCount: number;
  salesPerHour: number;
  window: string;
  fulfillmentScore: number;
  estimatedFillTime: string;
  confidence: string;
}
