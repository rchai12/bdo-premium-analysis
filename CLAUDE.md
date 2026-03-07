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
│   │   ├── DashboardItemDto.cs   # Dashboard response shape (salesCount, salesPerHour, fulfillmentScore, etc.)
│   │   └── VelocityDto.cs        # Velocity windows response shape
│   ├── Migrations/               # EF Core migrations (auto-applied on startup)
│   ├── Models/
│   │   ├── ApplicationUser.cs    # ASP.NET Core Identity user entity
│   │   ├── ArshaModels.cs        # Arsha API response DTOs (ArshaDbItem, ArshaMarketItem, etc.)
│   │   ├── CorrectionFactor.cs   # Per-item velocity calibration factor (EMA-updated)
│   │   ├── DailySummary.cs       # Compacted daily aggregate (sales, avg price, avg preorders per item per day)
│   │   ├── TrackedItem.cs        # Item entity (id, name, grade)
│   │   ├── TradeSnapshot.cs      # Snapshot entity (trades, stock, price, preorders, timestamp)
│   │   └── VelocityPrediction.cs # Prediction log for feedback loop evaluation
│   ├── Services/
│   │   ├── IArshaApiClient.cs    # Interface for Arsha HTTP client
│   │   ├── ArshaApiClient.cs     # HttpClient wrapper for arsha.io endpoints (120s timeout, Polly retry)
│   │   ├── ICorrectionFactorProvider.cs # Interface for correction factor loading
│   │   ├── CorrectionFactorProvider.cs  # DB-backed correction factors with 5-min cache
│   │   ├── IVelocityCalculator.cs # Interface for velocity calculator
│   │   ├── VelocityCalculator.cs # Weighted mean velocity, day-of-week weighting, correction factor application
│   │   └── MarketSyncService.cs  # Background service: WebSocket + polling, prediction logging/evaluation, compaction
│   ├── Program.cs                # DI, Identity, JWT, CORS, Polly, rate limiting, health checks, auto-migrate, admin seed
│   ├── Dockerfile                # Multi-stage .NET 9 build for Oracle Cloud VM
│   ├── .dockerignore               # Docker build exclusions
│   ├── appsettings.json          # Base config (placeholders for secrets)
│   ├── appsettings.Development.json  # Dev secrets (gitignored)
│   └── appsettings.Production.json   # Prod overrides (gitignored)
│
├── api.Tests/                    # xUnit test project
│   ├── BdoMarketTracker.Tests.csproj
│   └── VelocityCalculatorTests.cs  # 11 tests using EF Core InMemory provider
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
├── deploy/                       # Docker Compose + Nginx reverse proxy
│   ├── docker-compose.yml        # Nginx + bdo-api (+ future apps)
│   ├── nginx/conf.d/default.conf # Domain-based routing rules
│   ├── .env.example              # Template for production secrets
│   └── .env                      # Real secrets (gitignored)
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
- **Resilience:** Polly retry policy (3 retries, exponential backoff) on all HTTP calls to arsha.io (120s timeout)
- **Error isolation:** Per-item try/catch in snapshot collection — one failure doesn't lose the batch
- Snapshots are stored in `trade_snapshots` with a composite index on `(item_id, recorded_at DESC)`

### Data Retention
- **Raw snapshots** are kept for **30 days** (the velocity calculator's longest window is 14d)
- **Daily compaction** runs on startup and once per day: aggregates old snapshots into `daily_summaries` (sales count, avg price, avg preorders per item per day) then deletes the raw rows
- Keeps Neon free tier (512MB) usage sustainable (~120MB for 30 days of raw snapshots + negligible daily summaries)

### Tracked Items
On startup, `MarketSyncService`:
1. Fetches the full item database from `/util/db?lang=en` and filters for items containing both "Premium" AND "Set" (case-insensitive), **excluding** time-limited rentals (names containing "Days)" or "Day)")
2. Upserts matches into `tracked_items`
3. **Validates** each tracked item individually against the market API — items that return 404 are removed (arsha.io's `/util/db` includes ~130 legacy/duplicate IDs that don't exist on the live market)
4. Result: ~284 valid tradeable premium sets across 32 BDO classes

**Note:** arsha.io's batch `/v2/na/item` endpoint returns 404 for the **entire batch** if any single ID is invalid. The validation step prevents this from poisoning snapshot collection.

### Velocity Calculation
- **Sales velocity** uses a **weighted mean** of consecutive snapshot segment rates
  - Consecutive snapshot pairs form "segments" — **including zero-sale segments** (so rates decay when no sales occur)
  - Each segment gets an **exponential decay weight** (half-life = half the window duration) — recent activity matters more
  - **Day-of-week weighting** multiplied into each segment's weight:
    - Thursday: 1.5× (maintenance/pearl shop refresh)
    - Monday & Friday: 1.3× (limit resets / payday)
    - Saturday: 1.2× (weekend activity)
    - Other days: 1.0×
  - The **weighted mean** of segment rates becomes `rawSalesPerHour`
  - A **correction factor** is applied: `salesPerHour = rawSalesPerHour × correctionFactor`
- **Time windows:** 3h, 12h, 24h, 3d, 7d, 14d
- **Confidence indicator:** "low" (<3 segments or <3 sales), "medium" (3–10 segments), "high" (>10 segments and ≥10 sales)
- **Fulfillment score** = `sales_per_hour / total_preorders` (higher = faster fill)
- **Estimated fill time** = `total_preorders / sales_per_hour`
- **Query optimization:** Batch queries (3 total for dashboard, 2 for velocity) instead of N+1 per item

### Adaptive Prediction Calibration
The system learns from its own prediction accuracy and self-corrects over time:
- **Prediction logging:** After each snapshot, logs the current `salesPerHour` prediction for each item (24h, 3d, 7d windows only, medium/high confidence)
- **Evaluation:** After an evaluation horizon passes (6h for 24h window, 12h for 3d, 24h for 7d), compares predicted vs actual velocity from snapshot data
- **Correction factors:** `accuracy_ratio = actual / predicted`, blended into a per-item correction factor via **exponential moving average** (EMA, alpha=0.15, warmup alpha=0.3 for first 10 samples)
- **Application:** `VelocityCalculator` multiplies raw velocity by the correction factor; both `salesPerHour` (corrected) and `rawSalesPerHour` (uncorrected) are returned in API responses
- **Cold start:** All factors default to 1.0 (no change); system converges within ~1 week
- **Throttling:** Only one pending prediction per (item, window) at a time — caps storage at ~10 MB over 30 days
- **Cleanup:** Evaluated predictions older than 30 days are deleted during daily compaction
- **Clamping:** Correction factors clamped to [0.2, 3.0] to prevent runaway corrections; accuracy ratios clamped to [0.1, 3.0]
- **Frontend:** Dashboard shows a "tune" icon with tooltip when correction is active; item detail shows raw vs calibrated columns and side-by-side chart bars

### Dashboard Columns
`name`, `totalPreorders`, `salesCount` (trades in selected window), `salesPerHour`, `rawSalesPerHour`, `correctionFactor`, `estimatedFillTime`, `fulfillmentScore`, `confidence`

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

Backend auto-migrates the database and seeds the admin user on startup. The background service startup sequence is: sync tracked items → validate against market API → take first snapshot → evaluate pending predictions → log new predictions → compact old snapshots → enter WebSocket loop. CORS is configured from `appsettings.json`.

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
- Retained for 30 days, then compacted into daily summaries

**daily_summaries:** `id` (serial PK), `item_id` (FK), `date`, `sales_count`, `avg_base_price`, `avg_preorders`, `snapshot_count`
- Unique index: `(item_id, date)`
- Created by compaction of old snapshots — lightweight long-term historical data

**velocity_predictions:** `id` (serial PK), `item_id` (FK), `window`, `predicted_at`, `predicted_sales_per_hour`, `predicted_preorders`, `evaluation_due_at`, `actual_sales_per_hour` (nullable), `actual_preorders` (nullable), `accuracy_ratio` (nullable), `evaluated_at` (nullable)
- Index: `(evaluation_due_at) WHERE evaluated_at IS NULL` for pending evaluations
- Index: `(item_id, window, predicted_at DESC)`
- Evaluated predictions cleaned up after 30 days

**correction_factors:** `id` (serial PK), `item_id` (FK), `window`, `factor` (default 1.0), `sample_count`, `last_updated`
- Unique index: `(item_id, window)`
- Max ~852 rows (284 items × 3 tracked windows)

## Deployment

### Infrastructure
Oracle Cloud VM runs a **shared Nginx reverse proxy** in front of multiple Docker containers via `deploy/docker-compose.yml`:

```
Internet :80/:443
       │
   Cloudflare (SSL termination, proxy)
       │
   Nginx (reverse proxy, port 80)
       ├── bdo-api.yourdomain.com  →  bdo-api :8080
       └── stocks.yourdomain.com   →  stock-predictor-nginx :80 (via proxy_net)
```

- Cloudflare handles SSL (proxied A records) — origin is HTTP-only
- Each app runs on an internal port (`expose`, not `ports`) — only Nginx is public
- Nginx routes by `server_name` (domain/subdomain) and returns 444 for unknown hosts
- Apps across separate Docker Compose projects share the `proxy_net` external network
- Config: `deploy/nginx/conf.d/default.conf`

### Deploying the API
```bash
# On the Oracle Cloud VM (first time):
docker network create proxy_net   # Shared network for cross-compose routing
cd deploy
cp .env.example .env              # Fill in real secrets
docker compose up -d --build      # Build and start all services
docker compose logs -f bdo-api    # Watch logs
```

To redeploy after code changes:
```bash
cd deploy
git pull
docker compose up -d --build bdo-api
```

### Frontend
Cloudflare Pages. Root directory `web`, output `dist/web/browser`.
- Build command: `sed -i "s|apiUrl: ''|apiUrl: '$API_URL'|" src/environments/environment.prod.ts && npm install && npx ng build`
- `API_URL` is set as a **Secret** in Cloudflare Pages Variables & Secrets (keeps the server IP out of the repo)

### Adding a new app to the VM
1. Add the app's Docker Compose with `proxy_net` as an external network
2. Add a `server {}` block in `deploy/nginx/conf.d/default.conf` with the new domain
3. Add a Cloudflare DNS A record (proxied) pointing the subdomain to the VM IP
4. `docker compose up -d --build` in both projects, restart nginx if config changed

### API env vars
Stored in `deploy/.env` (gitignored). See `deploy/.env.example` for the template:
`ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Admin__Email`, `Admin__Password`, `Cors__AllowedOrigins__0`

## Common Tasks

### Add a new item filter
Edit `MarketSyncService.SyncTrackedItemsAsync()` — modify the `.Where()` LINQ predicate.

### Change polling interval
Edit `MarketSyncService.FallbackInterval` (currently 30 min). Don't go below 30 min — arsha.io cache TTL makes it pointless.

### Change data retention period
Edit `MarketSyncService.RetentionPeriod` (currently 30 days). Must be ≥14 days (the longest velocity window).

### Add a new time window
Edit `VelocityCalculator.WindowDefinitions` dictionary — add entry like `["30d"] = TimeSpan.FromDays(30)`.

### Tune calibration parameters
Edit `MarketSyncService`: `EmaAlpha` (smoothing, default 0.15), `EmaAlphaWarmup` (first 10 samples, default 0.3), `EvaluationHorizons` (which windows to track and how long to wait before evaluating).

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
