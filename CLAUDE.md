# BDO Market Tracker - Development Guide

## Project Overview

A web app that tracks sales velocity of Black Desert Online (BDO) Central Market costume items ("premium sets") and ranks them by preorder fulfillment speed. Helps players decide which costume to preorder for the quickest fill.

**Data source:** [arsha.io](https://api.arsha.io) API v2 — a public caching proxy for Pearl Abyss's unofficial BDO market API.

## Architecture

```
[Neon PostgreSQL] <-- EF Core --> [.NET 9 Web API :5000] <-- HTTP --> [Angular 18 SPA :4200]
                                         |                                    |
                                   Identity + JWT Auth              Auth Guard + Interceptor
                                   Polly Retry Policies             JWT Token Management
                                   Background Service
                                   (WebSocket + polling)
                                         |
                                    [api.arsha.io v2]
```

- **Backend:** ASP.NET Core 9, Entity Framework Core, Npgsql, ASP.NET Core Identity, JWT Bearer Auth, Polly
- **Frontend:** Angular 18 (standalone components), Angular Material, Chart.js/ng2-charts
- **Database:** Neon (serverless PostgreSQL)
- **External API:** arsha.io v2 (30-minute cache TTL)
- **Deployment:** Oracle Cloud VM (Docker) + Cloudflare Pages (SPA)

## Project Structure

```
bdo-market-tracker/
├── api/                          # .NET 9 Web API
│   ├── Controllers/
│   │   ├── AuthController.cs     # POST /api/auth/login (JWT issuance)
│   │   └── ItemsController.cs    # [Authorize] REST endpoints: /api/items, /api/items/{id}/velocity, /api/items/dashboard
│   ├── Data/
│   │   └── AppDbContext.cs       # IdentityDbContext<ApplicationUser>, EF Core indexes
│   ├── Dtos/
│   │   ├── DashboardItemDto.cs   # Dashboard response shape
│   │   └── VelocityDto.cs        # Velocity windows response shape
│   ├── Migrations/               # EF Core migrations (auto-applied on startup)
│   ├── Models/
│   │   ├── ApplicationUser.cs    # ASP.NET Core Identity user entity
│   │   ├── ArshaModels.cs        # Arsha API response DTOs (ArshaDbItem, ArshaMarketItem, etc.)
│   │   ├── TrackedItem.cs        # Item entity (id, name, grade)
│   │   └── TradeSnapshot.cs      # Snapshot entity (trades, stock, price, preorders, timestamp)
│   ├── Services/
│   │   ├── IArshaApiClient.cs    # Interface for Arsha HTTP client
│   │   ├── ArshaApiClient.cs     # HttpClient wrapper for arsha.io endpoints (with Polly retry)
│   │   ├── IVelocityCalculator.cs # Interface for velocity calculator
│   │   ├── VelocityCalculator.cs # Batch-optimized sales velocity & fulfillment calculations
│   │   └── MarketSyncService.cs  # Background service: WebSocket + polling with exponential backoff
│   ├── Program.cs                # DI, Identity, JWT, CORS, Polly, rate limiting, health checks, auto-migrate, admin seed
│   ├── Dockerfile                # Multi-stage .NET 9 build for Oracle Cloud VM
│   ├── .dockerignore               # Docker build exclusions
│   ├── appsettings.json          # Base config (placeholders for secrets)
│   ├── appsettings.Development.json  # Dev secrets (gitignored)
│   └── appsettings.Production.json   # Prod overrides (gitignored)
│
├── api.Tests/                    # xUnit test project
│   ├── BdoMarketTracker.Tests.csproj
│   └── VelocityCalculatorTests.cs  # 5 tests using EF Core InMemory provider
│
├── web/                          # Angular 18 SPA
│   └── src/app/
│       ├── core/
│       │   ├── guards/
│       │   │   └── auth.guard.ts       # Route guard — redirects to /login if unauthenticated
│       │   ├── interceptors/
│       │   │   └── auth.interceptor.ts  # Attaches Bearer token, handles 401 → logout
│       │   ├── models/
│       │   │   ├── auth.model.ts        # LoginRequest, AuthResponse interfaces
│       │   │   ├── dashboard-item.model.ts
│       │   │   └── velocity.model.ts
│       │   └── services/
│       │       ├── api.service.ts       # HttpClient wrapper for .NET backend
│       │       └── auth.service.ts      # Login, logout, JWT storage, expiry check
│       ├── features/
│       │   ├── dashboard/        # Main view: sortable Material table, summary cards
│       │   ├── item-detail/      # Detail view: Chart.js bar charts per time window
│       │   └── login/            # Login page: Material card with email/password form
│       ├── app.component.ts      # Root component with toolbar + logout button
│       ├── app.config.ts         # Providers: routing, HTTP with auth interceptor, animations
│       └── app.routes.ts         # Guarded routes: / -> dashboard, /item/:id -> detail, /login -> login
│
├── .gitignore                    # Ignores secrets, build artifacts, node_modules
└── CLAUDE.md                     # This file
```

## Authentication

**Type:** ASP.NET Core Identity + JWT Bearer tokens (invite-only, no public registration).

- Admin account is **seeded on startup** from `Admin:Email` + `Admin:Password` config values
- `POST /api/auth/login` validates credentials and returns a signed JWT
- All `/api/items/*` endpoints require `Authorization: Bearer <token>`
- Angular app stores the JWT in localStorage, attaches it via HTTP interceptor
- Auth guard redirects unauthenticated users to `/login`
- 401 responses auto-trigger logout

### Dev Credentials (in appsettings.Development.json)
- **Email:** `admin@bdo-tracker.local`
- **Password:** `Admin123!`

## Key Concepts

### Data Sync Strategy
arsha.io caches BDO market data for **30 minutes**. The backend syncs in lockstep:
- **Primary:** WebSocket connection to `wss://api.arsha.io/events` — listens for `ExpiredEvent` cache invalidations
- **Fallback:** 30-minute polling timer if WebSocket disconnects
- **Reconnect:** Exponential backoff on WebSocket failures (5s → 5min cap)
- **Resilience:** Polly retry policy (3 retries, exponential backoff) on all HTTP calls to arsha.io
- **Error isolation:** Per-item try/catch in snapshot collection — one failure doesn't lose the batch
- Snapshots are stored in `trade_snapshots` with a composite index on `(item_id, recorded_at DESC)`

### Tracked Items
On startup, `MarketSyncService` fetches the full item database from `/util/db?lang=en` and filters for items containing both "Premium" AND "Set" (case-insensitive). These are upserted into `tracked_items`.

### Velocity Calculation
- **Sales velocity** = `(latest_total_trades - earliest_total_trades) / hours_elapsed` for a given time window
- **Time windows:** 3h, 12h, 24h, 3d, 7d, 14d
- **Fulfillment score** = `sales_per_hour / total_preorders` (higher = faster fill)
- **Estimated fill time** = `total_preorders / sales_per_hour`
- **Query optimization:** Batch queries (3 total for dashboard, 2 for velocity) instead of N+1 per item

### API Endpoints
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/api/auth/login` | No | Authenticate and receive JWT (rate-limited: 5/min per IP) |
| GET | `/api/items` | Yes | All tracked items with latest snapshot |
| GET | `/api/items/{id}/velocity` | Yes | Velocity across all time windows |
| GET | `/api/items/dashboard?window=24h` | Yes | All items ranked by fulfillment score for given window |
| GET | `/health` | No | Health check (includes DB connectivity) |

### arsha.io Endpoints Used
| Endpoint | Purpose |
|----------|---------|
| `GET /util/db?lang=en` | Full item database (names, IDs, grades) |
| `GET /v2/{region}/item?id={id}` | Market data: totalTrades, stock, price |
| `GET /v2/{region}/orders?id={id}&sid=0` | Order book: buyer/seller counts per price |
| `WSS /events` | Cache expiry notifications |

## Running Locally

```bash
# Backend (from api/)
dotnet run                        # Runs on http://localhost:5000

# Frontend (from web/)
npm install                       # First time only
npx ng serve                      # Runs on http://localhost:4200

# Tests (from root)
dotnet test api.Tests/
```

Backend auto-migrates the database and seeds the admin user on startup. CORS is configured from `appsettings.json`.

Login at `http://localhost:4200` with the dev credentials above.

## Configuration

Configuration uses ASP.NET Core's layered approach: `appsettings.json` (base/placeholders) → `appsettings.Development.json` (local secrets, gitignored) → environment variables (production).

### Key config sections in appsettings.json:
```json
{
  "ConnectionStrings": { "DefaultConnection": "..." },
  "Jwt": { "Key": "...", "Issuer": "BdoMarketTracker", "Audience": "BdoMarketTracker", "ExpiresInHours": 24 },
  "Admin": { "Email": "...", "Password": "..." },
  "Arsha": { "BaseUrl": "https://api.arsha.io", "WebSocketUrl": "wss://api.arsha.io/events", "Region": "na" },
  "Cors": { "AllowedOrigins": ["http://localhost:4200"] }
}
```

**Real secrets** go in `appsettings.Development.json` (local) or Docker environment variables (production). Never commit secrets to `appsettings.json`.

**Angular environments** in `web/src/environments/`:
- `environment.ts` — dev: API at `http://localhost:5000`
- `environment.prod.ts` — prod: API URL injected at build time via Cloudflare Pages env var (`API_URL` secret + `sed` in build command)

## Database Schema

**AspNetUsers (Identity):** Standard ASP.NET Core Identity user table
**tracked_items:** `id` (PK, BDO item ID), `name`, `grade`
**trade_snapshots:** `id` (serial PK), `item_id` (FK), `recorded_at`, `total_trades`, `current_stock`, `base_price`, `last_sold_price`, `total_preorders`
- Index: `(item_id, recorded_at DESC)`

## Deployment

- **API:** Oracle Cloud VM running Docker container from `api/Dockerfile`.
  - SSH into VM, `git pull`, rebuild with `docker build -t bdo-api ./api && docker run -d ...`
  - Pass config via Docker environment variables (`-e` flags or `.env` file)
- **Frontend:** Cloudflare Pages. Root directory `web`, output `dist/web/browser`.
  - Build command: `sed -i "s|apiUrl: ''|apiUrl: '$API_URL'|" src/environments/environment.prod.ts && npm install && npx ng build`
  - `API_URL` is set as a **Secret** in Cloudflare Pages Variables & Secrets (keeps the server IP out of the repo)
- **API env vars (Docker):** `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Admin__Email`, `Admin__Password`, `Cors__AllowedOrigins__0`, `ASPNETCORE_ENVIRONMENT=Production`

## Common Tasks

### Add a new item filter
Edit `MarketSyncService.SyncTrackedItemsAsync()` — modify the `.Where()` LINQ predicate.

### Change polling interval
Edit `MarketSyncService.FallbackInterval` (currently 30 min). Don't go below 30 min — arsha.io cache TTL makes it pointless.

### Add a new time window
Edit `VelocityCalculator.WindowDefinitions` dictionary — add entry like `["30d"] = TimeSpan.FromDays(30)`.

### Add a new API endpoint
Add method to `ItemsController` (with `[Authorize]`), create DTO in `Dtos/` if needed.

### Change admin credentials
Update `Admin:Email` and `Admin:Password` in `appsettings.Development.json` (local) or Docker env vars (production). The seed runs on every startup and creates the user only if it doesn't exist — to change the password of an existing user, update it directly in the database.

### Run EF Core migrations
```bash
cd api
dotnet ef migrations add MigrationName
# Migrations auto-apply on startup via Program.cs
```

### Run tests
```bash
dotnet test api.Tests/
```
