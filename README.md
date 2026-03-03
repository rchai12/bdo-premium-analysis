# BDO Market Tracker

A web app that tracks sales velocity of Black Desert Online Central Market premium costumes and ranks them by preorder fulfillment speed. Helps players decide which costume to preorder for the quickest fill.

## How It Works

The backend continuously collects market snapshots from [arsha.io](https://api.arsha.io) (every ~30 minutes via WebSocket events) and stores them in a PostgreSQL database. When you open the dashboard, it calculates sales velocity and fulfillment scores across configurable time windows (3h to 14d) to show which costumes are selling fastest relative to their preorder queue.

**Key metric: Fulfillment Score** = `sales_per_hour / total_preorders` — higher means your preorder fills faster.

## Screenshots

The dashboard displays a sortable table of all premium costume sets with:
- Sales per hour for the selected time window
- Estimated fill time (how long until your preorder would be fulfilled)
- Fulfillment score (color-coded: green = fast, yellow = moderate, red = slow)
- Time window selector to compare short-term vs long-term trends

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 9, Entity Framework Core, Polly |
| Frontend | Angular 18, Angular Material, Chart.js |
| Database | PostgreSQL (Neon serverless) |
| Auth | ASP.NET Core Identity + JWT Bearer |
| Data Source | arsha.io v2 API + WebSocket |
| Hosting | Oracle Cloud VM (Docker) + Cloudflare Pages |

## Quick Start

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Node.js 18+](https://nodejs.org/)
- A [Neon](https://neon.tech) PostgreSQL database (free tier)

### Setup

1. Create `api/appsettings.Development.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "your-neon-connection-string"
  },
  "Jwt": {
    "Key": "YourDevSecretKeyAtLeast32CharactersLong!"
  },
  "Admin": {
    "Email": "admin@bdo-tracker.local",
    "Password": "Admin123!"
  }
}
```

2. Start the backend:
```bash
cd api
dotnet run
```

3. Start the frontend:
```bash
cd web
npm install
npx ng serve
```

4. Open `http://localhost:4200` and log in with your admin credentials.

The backend auto-migrates the database, seeds the admin user, discovers ~90 premium costume items, and begins collecting snapshots immediately. Velocity data becomes meaningful after 2+ snapshots (~1 hour).

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/auth/login` | Authenticate (rate-limited: 5/min per IP) |
| GET | `/api/items` | All tracked items with latest snapshot |
| GET | `/api/items/{id}/velocity` | Velocity across all time windows |
| GET | `/api/items/dashboard?window=24h` | Items ranked by fulfillment score |
| GET | `/health` | Health check with DB connectivity |

## Documentation

- [Setup Guide](docs/setup.md) — Full setup, configuration, and deployment instructions
- [Architecture](docs/architecture.md) — System design, data flow, and security details
- [API Reference](docs/api-reference.md) — Complete endpoint documentation with examples
- [CLAUDE.md](CLAUDE.md) — Development guide and common tasks

## License

Private project — not licensed for redistribution.
