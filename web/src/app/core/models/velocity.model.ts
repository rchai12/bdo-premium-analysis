export interface Velocity {
  itemId: number;
  name: string;
  windows: VelocityWindow[];
}

export interface VelocityWindow {
  window: string;
  salesCount: number;
  salesPerHour: number;
  avgPreorders: number;
  confidence: string;
}
