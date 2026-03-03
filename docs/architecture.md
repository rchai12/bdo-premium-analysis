# Architecture

## System Overview

BDO Market Tracker is a two-tier web application that collects and analyzes Black Desert Online Central Market data to help players identify the best costume preorder investments.

```
                                  ┌──────────────────────┐
                                  │   Neon PostgreSQL     │
                                  │   (Serverless)        │
                                  │                       │
                                  │  AspNetUsers (Auth)   │
                                  │  tracked_items        │
                                  │  trade_snapshots      │
                                  └──────────┬───────────┘
                                             │ EF Core / Npgsql
                                             │
┌────────────────────┐  HTTP     ┌───────────┴───────────┐  HTTP/WS    ┌──────────────────┐
│   Angular 18 SPA   │◄────────►│   .NET 9 Web API      │◄───────────►│  api.arsha.io    │
│                    │  :4200    │                       │   :5000     │  (BDO Market)    │
│  Login Page        │          │  Auth (Identity+JWT)  │             │                  │
│  Dashboard View    │          │  REST Controllers     │             │  /v2/na/item     │
│  Item Detail View  │          │  Background Service   │  WebSocket  │  /v2/na/orders   │
│  Auth Guard/Intcpt │          │  Velocity Calculator  │◄···········►│  /util/db        │
│  Chart.js Charts   │          │  Polly Retry Policies │  /events    │                  │
└────────────────────┘          └───────────────────────┘             └──────────────────┘
```

## Authentication Flow

```
1. User visits any route → Auth Guard checks localStorage for valid JWT
2. No token / expired → Redirect to /login
3. User submits credentials → POST /api/auth/login
4. Backend validates via ASP.NET Core Identity → Returns signed JWT (24h expiry)
5. Angular stores token in localStorage
6. HTTP Interceptor attaches "Authorization: Bearer <token>" to all API requests
7. 401 response → Interceptor triggers logout → Redirect to /login
```

- **Invite-only:** No public registration endpoint. Admin account seeded on startup from config.
- **Token expiry:** Configurable via `Jwt:ExpiresInHours` (default 24h).
- **Password policy:** Min 8 chars, requires uppercase, lowercase, and digit.

## Data Flow

### 1. Item Discovery (Startup)
```
MarketSyncService.SyncTrackedItemsAsync()
    │
    ├── GET api.arsha.io/util/db?lang=en    → Full BDO item database (~50k items)
    ├── Filter: name contains "Premium" AND "Set" (case-insensitive)
    └── Upsert into tracked_items table     → ~50-100 costume items
```

### 2. Snapshot Collection (Every ~30 min)
```
MarketSyncService (WebSocket event OR fallback timer)
    │
    ├── For each tracked item (with per-item error isolation):
    │   ├── GET /v2/{region}/item?id={id}         → totalTrades, stock, price
    │   ├── GET /v2/{region}/orders?id={id}&sid=0  → buyer/seller order book
    │   └── 150ms delay between requests
    │
    └── Batch INSERT into trade_snapshots
```

### 3. Velocity Calculation (On Request)
```
VelocityCalculator.GetVelocityAsync(itemId)
    │
    ├── Single query: all snapshots within 14d window (largest window)
    ├── Slice in memory per window [3h, 12h, 24h, 3d, 7d, 14d]:
    │   ├── salesCount = latest.totalTrades - earliest.totalTrades
    │   ├── salesPerHour = salesCount / hours_elapsed
    │   └── avgPreorders = average of snapshot preorder counts
    │
    └── Return VelocityDto with all windows
```

### 4. Dashboard Ranking
```
VelocityCalculator.GetDashboardAsync()
    │
    ├── Batch query 1: latest snapshot per item (GroupBy + OrderByDescending)
    ├── Batch query 2: all 24h snapshots for all items
    ├── Group and calculate in memory:
    │   ├── fulfillmentScore = salesPerHour / totalPreorders
    │   └── estimatedFillTime = totalPreorders / salesPerHour
    │
    └── Return sorted by fulfillmentScore DESC (best items first)
```

## Backend Architecture

### Dependency Injection

```
Program.cs registers:
  ├── AppDbContext                        (Scoped)    → IdentityDbContext with Npgsql
  ├── Identity<ApplicationUser, Role>    (Scoped)    → User management, password hashing
  ├── JwtBearer Authentication           (Singleton) → Token validation middleware
  ├── IArshaApiClient → ArshaApiClient   (Transient) → HttpClient via IHttpClientFactory + Polly
  ├── IVelocityCalculator → VelocityCalc (Scoped)    → Depends on AppDbContext
  └── MarketSyncService                  (Singleton) → IHostedService, depends on IServiceScopeFactory
```

### Service Responsibilities

**ArshaApiClient** (`IArshaApiClient`) — Pure HTTP client wrapper. No business logic. Handles:
- Single vs array response deserialization (arsha.io returns object for 1 item, array for many)
- Batched item queries (up to 100 IDs per request)
- 404 handling for order book (returns null) vs other errors (propagates through Polly)

**MarketSyncService** — Background hosted service. Manages:
- WebSocket connection lifecycle (connect, reconnect with exponential backoff 5s → 5min cap)
- Cache expiry event detection and debouncing (min 5 min between snapshots)
- Fallback polling when WebSocket is unavailable (protected by try/catch)
- Per-item error isolation in snapshot collection
- Item discovery on startup and periodic refresh
- Region is configurable via `Arsha:Region` config

**VelocityCalculator** (`IVelocityCalculator`) — Stateless query service. Computes:
- Sales velocity across configurable time windows
- Fulfillment scores (sales rate vs competition)
- Human-readable estimated fill times
- Uses batch queries to avoid N+1 performance issues

**AuthController** — Handles login. Validates credentials via Identity UserManager, issues signed JWTs.

### Error Handling & Resilience

| Layer | Strategy |
|-------|----------|
| HTTP client (Polly) | 3 retries with exponential backoff (2s, 4s, 8s) for transient errors |
| ArshaApiClient | Returns null on 404, lets other errors propagate for Polly to handle |
| MarketSyncService (WebSocket) | Exponential backoff on reconnect (5s → 5min cap), snapshot taken during wait |
| MarketSyncService (snapshot) | Per-item try/catch — one item failure doesn't lose the batch |
| MarketSyncService (polling) | Try/catch inside polling loop — failures don't kill the fallback |
| Database migration | Auto-applied on startup — failure is fatal (fail-fast) |

## Frontend Architecture

### Component Tree
```
AppComponent (toolbar with logout button, router-outlet)
  │
  ├── LoginComponent (at /login — no toolbar shown)
  │   └── Material card: email + password form, error display
  │
  ├── DashboardComponent (lazy-loaded at /, guarded)
  │   ├── Summary cards (items tracked, fastest seller, best fill time)
  │   ├── MatTable with MatSort (sortable columns)
  │   └── Score badges (color-coded fulfillment)
  │
  └── ItemDetailComponent (lazy-loaded at /item/:id, guarded)
      ├── Back navigation link
      ├── Velocity bar chart (Chart.js — sales/hr by window)
      ├── Preorder bar chart (Chart.js — avg preorders by window)
      └── Detail breakdown table
```

### Auth Infrastructure
```
AuthService
  ├── login(email, password) → POST /api/auth/login → store JWT in localStorage
  ├── logout() → clear token → navigate to /login
  ├── getToken() → retrieve from localStorage
  ├── isAuthenticated() → decode JWT, check exp claim
  └── isLoggedIn$ → BehaviorSubject<boolean> for reactive UI binding

AuthInterceptor (functional HttpInterceptorFn)
  ├── Attaches "Authorization: Bearer <token>" to outgoing requests
  └── On 401 response → calls authService.logout()

AuthGuard (functional CanActivateFn)
  └── Checks authService.isAuthenticated() → true = allow, false = redirect to /login
```

### Data Flow
```
AuthService.login(credentials)
  → HTTP POST /api/auth/login
  → Store JWT → Navigate to /

ApiService.getDashboard()
  → HTTP GET /api/items/dashboard (with Bearer token via interceptor)
  → DashboardComponent populates MatTableDataSource
  → User clicks row → router navigates to /item/:id

ApiService.getVelocity(id)
  → HTTP GET /api/items/{id}/velocity (with Bearer token via interceptor)
  → ItemDetailComponent builds Chart.js datasets
```

## Database Design

### AspNetUsers (Identity)
Standard ASP.NET Core Identity tables (AspNetUsers, AspNetRoles, AspNetUserClaims, etc.) managed by `IdentityDbContext<ApplicationUser>`. Admin account seeded on startup.

### tracked_items
| Column | Type | Notes |
|--------|------|-------|
| id | int (PK) | BDO item ID — NOT auto-generated, matches arsha.io |
| name | varchar(255) | English item name |
| grade | int | Rarity: 0=white, 1=green, 2=blue, 3=gold, 4=orange |

### trade_snapshots
| Column | Type | Notes |
|--------|------|-------|
| id | serial (PK) | Auto-increment |
| item_id | int (FK) | References tracked_items.id, CASCADE delete |
| recorded_at | timestamp | UTC snapshot time |
| total_trades | bigint | Cumulative all-time trades from BDO market |
| current_stock | bigint | Units currently listed for sale |
| base_price | bigint | Current market price in silver |
| last_sold_price | bigint | Price of most recent sale |
| total_preorders | bigint | Sum of all buyer orders (competition) |

**Index:** `(item_id, recorded_at DESC)` — optimizes time-range queries per item.

### Storage Estimates
- ~100 items x 48 snapshots/day (every 30 min) x 100 bytes = ~0.5 MB/day
- Neon free tier (512 MB) supports ~3 years of raw data
- Optional: aggregate old data into daily summaries to extend indefinitely

## External API Reference

### arsha.io v2 (Data Source)

**Base URL:** `https://api.arsha.io`
**Cache TTL:** 30 minutes (confirmed from source code)
**Rate limiting:** No published limits, but we add 150ms delays between requests

| Endpoint | Method | Params | Returns |
|----------|--------|--------|---------|
| `/util/db` | GET | `lang=en` | Full item database: `[{id, name, grade}]` |
| `/v2/{region}/item` | GET | `id` (repeatable, max 100) | `[{name, id, sid, basePrice, currentStock, totalTrades, lastSoldPrice, lastSoldTime}]` |
| `/v2/{region}/orders` | GET | `id`, `sid` | `{name, id, sid, orders: [{price, sellers, buyers}]}` |
| `/events` | WebSocket | — | Heartbeats every 30s, `ExpiredEvent` on cache invalidation |

**Regions:** na, eu, sea, mena, kr, ru, jp, th, tw, sa, gl, console_na, console_eu, console_asia

## Deployment

### Railway (API Backend)
- Deploys from `api/Dockerfile` (multi-stage .NET 9 build)
- Set root directory to `/api` in Railway service settings
- Environment variables: `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Admin__Email`, `Admin__Password`, `Cors__AllowedOrigins__0`, `ASPNETCORE_ENVIRONMENT=Production`
- Always-on service (needed for WebSocket background sync)

### Cloudflare Pages (Angular Frontend)
- Root directory: `web`
- Build command: `npm install && npx ng build`
- Output directory: `dist/web/browser`
- Update `environment.prod.ts` with Railway URL after first deploy

## Security Considerations

- **Secrets management:** Connection strings, JWT keys, and admin credentials are in gitignored `appsettings.Development.json` (local) and Railway env vars (production). `appsettings.json` contains only placeholders.
- **Authentication:** All data endpoints require JWT Bearer token. No public registration — invite-only admin.
- **Password hashing:** Handled by ASP.NET Core Identity (PBKDF2 with HMAC-SHA256).
- **CORS:** Config-driven from `Cors:AllowedOrigins` — restricts cross-origin access to specific domains.
- **HTTP resilience:** Polly retries with exponential backoff prevent cascading failures from arsha.io outages.
- **arsha.io:** Public API, no API keys required.

## Testing

- **Unit tests:** `api.Tests/` project using xUnit + EF Core InMemory provider
- **5 tests** covering VelocityCalculator: unknown item handling, single/multiple snapshot velocity, empty dashboard, fulfillment score calculation
- Run with: `dotnet test api.Tests/`
