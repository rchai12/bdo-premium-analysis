export interface DashboardItem {
  itemId: number;
  name: string;
  grade: number;
  totalPreorders: number;
  salesCount: number;
  salesPerHour: number;
  rawSalesPerHour: number;
  correctionFactor: number;
  window: string;
  fulfillmentScore: number;
  estimatedFillTime: string;
  confidence: string;
}
