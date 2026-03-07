export interface Velocity {
  itemId: number;
  name: string;
  windows: VelocityWindow[];
}

export interface VelocityWindow {
  window: string;
  salesCount: number;
  salesPerHour: number;
  rawSalesPerHour: number;
  correctionFactor: number;
  avgPreorders: number;
  confidence: string;
}
