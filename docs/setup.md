# Setup Guide

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (LTS recommended, v18+)
- A [Neon](https://neon.tech) account (free tier)

## 1. Database Setup (Neon)

1. Go to [neon.tech](https://neon.tech) and create a free account
2. Create a new project:
   - **Name:** bdo-market-tracker (or anything)
   - **Region:** Closest to you (e.g., AWS US East)
   - **Postgres version:** 17 (latest)
3. Copy the connection string from the dashboard

## 2. Backend Configuration

Create `api/appsettings.Development.json` (this file is gitignored — never commit secrets):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "postgresql://neondb_owner:YOUR_PASSWORD@ep-xxxx-123456.us-east-2.aws.neon.tech/neondb?sslmode=require"
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

The Neon dashboard gives you a connection URL — use it directly as the `DefaultConnection` value. EF Core/Npgsql supports both URL and key-value formats.

The base `appsettings.json` contains placeholder values and non-secret config (arsha.io URLs, CORS origins, JWT issuer/audience). Real secrets go in the Development file above.

### Admin Account

The admin account is **seeded automatically on startup** from the `Admin:Email` and `Admin:Password` config values. Change these to whatever you prefer. Password requirements: min 8 characters, uppercase, lowercase, and digit.

## 3. Start the Backend

```bash
cd api
dotnet run
```

On first run, the app will:
1. Auto-apply database migrations (create Identity + app tables)
2. Seed the admin user
3. Fetch the BDO item database from arsha.io
4. Filter and store "Premium Set" items
5. Take the first market snapshot
6. Connect to the arsha.io WebSocket for ongoing sync

You should see log output like:
```
info: BdoMarketTracker[0]
      Admin user admin@bdo-tracker.local created
info: BdoMarketTracker.Services.MarketSyncService[0]
      Found 87 premium set items in database
info: BdoMarketTracker.Services.MarketSyncService[0]
      Added 87 new tracked items
info: BdoMarketTracker.Services.MarketSyncService[0]
      Taking snapshot for 87 items
info: BdoMarketTracker.Services.MarketSyncService[0]
      Saved 87 snapshots
info: BdoMarketTracker.Services.MarketSyncService[0]
      Connecting to arsha.io WebSocket...
```

API will be available at `http://localhost:5000`.

## 4. Start the Frontend

```bash
cd web
npm install    # first time only
npx ng serve
```

Open `http://localhost:4200` in your browser. You'll be redirected to the login page.

**Login with your admin credentials** (default: `admin@bdo-tracker.local` / `Admin123!`).

## 5. Initial Data

The dashboard will show items immediately after the first snapshot, but velocity calculations need at least **2 snapshots** (spaced ~30 min apart) to compute sales rates. Until then, all sales/hr values will show 0.

After a few hours of data collection, the dashboard becomes increasingly useful. After 24 hours, all time windows up to 24h will have data. After 14 days, all windows will be fully populated.

## 6. Running Tests

```bash
dotnet test api.Tests/
```

Runs 5 unit tests for VelocityCalculator using EF Core InMemory provider.

## Deployment

### Oracle Cloud VM (API Backend)

1. Provision an Oracle Cloud VM (free tier eligible — ARM Ampere A1)
2. Install Docker on the VM
3. Clone the repo and build:
   ```bash
   git clone <repo-url>
   cd bdo-market-tracker/api
   docker build -t bdo-api .
   ```
4. Run with environment variables:
   ```bash
   docker run -d --name bdo-api -p 5000:8080 \
     -e ConnectionStrings__DefaultConnection="YOUR_NEON_CONNECTION_STRING" \
     -e Jwt__Key="YOUR_STRONG_SECRET_KEY_32CHARS" \
     -e Admin__Email="your@email.com" \
     -e Admin__Password="YourStrongPassword1" \
     -e Cors__AllowedOrigins__0="https://your-project.pages.dev" \
     -e ASPNETCORE_ENVIRONMENT=Production \
     bdo-api
   ```
5. Open port 5000 in the VM's security list / firewall

### Cloudflare Pages (Angular Frontend)

1. Go to [dash.cloudflare.com](https://dash.cloudflare.com) → Workers & Pages → Create → Pages → Connect to Git
2. Configure build:
   - **Root directory:** `web`
   - **Build command:** `sed -i "s|apiUrl: ''|apiUrl: '$API_URL'|" src/environments/environment.prod.ts && npm install && npx ng build`
   - **Output directory:** `dist/web/browser`
3. Go to **Settings → Variables and Secrets** → Add a **Secret** named `API_URL` with the value `http://<your-vm-ip>:5000`
   - Using a Secret (not a plain variable) keeps the server IP out of build logs
4. Trigger a deploy

### Post-Deployment Checklist

- [ ] Set `API_URL` secret in Cloudflare Pages to your Oracle Cloud VM API URL
- [ ] Set `Cors__AllowedOrigins__0` in Docker env to your Cloudflare Pages URL (e.g., `https://your-project.pages.dev`)
- [ ] Verify login works at your Cloudflare Pages URL
- [ ] Confirm market data is syncing (check Docker logs: `docker logs bdo-api`)
- [ ] Verify health endpoint: `curl http://<vm-ip>:5000/health`

## Troubleshooting

### "Failed to load dashboard data. Is the API running?"
The Angular frontend can't reach the .NET backend. Verify:
- Backend is running on port 5000 (`http://localhost:5000/api/items/dashboard` should return 401 if not authenticated)
- Check `web/src/environments/environment.ts` has `apiUrl: 'http://localhost:5000'`

### Login fails with "Invalid email or password"
- Verify admin credentials match what's in `appsettings.Development.json`
- Check backend logs for "Admin user ... created" on startup
- If the user already exists with a different password, you'll need to update it in the database directly

### Backend starts but no items are tracked
- Check internet connectivity — the backend needs to reach `https://api.arsha.io`
- Look for error logs about "Failed to sync tracked items"
- Verify: `curl https://api.arsha.io/util/db?lang=en` returns JSON

### Database migration errors
- Verify the Neon connection string is correct in `appsettings.Development.json`
- Ensure the Neon project is active (free tier pauses after inactivity)

### WebSocket connection fails
This is non-fatal. The service reconnects with exponential backoff (5s → 5min cap) and takes a snapshot during each backoff wait. Check logs for:
```
WebSocket sync failed, retrying in Xs
```

### 401 Unauthorized on API calls
- Token may have expired (default 24h) — log out and log back in
- Check browser dev tools → Network tab → verify `Authorization` header is present
- If testing with curl: `curl -H "Authorization: Bearer <token>" http://localhost:5000/api/items/dashboard`
