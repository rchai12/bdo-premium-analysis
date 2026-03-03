# API Reference

## Authentication

All endpoints under `/api/items` require a valid JWT token in the `Authorization` header:
```
Authorization: Bearer <token>
```

Obtain a token via the login endpoint below.

---

## Backend REST Endpoints

Base URL: `http://localhost:5000`

---

### POST /api/auth/login

Authenticate with email and password to receive a JWT. **Rate limited:** 5 requests per minute per IP address.

**Request body:**
```json
{
  "email": "admin@bdo-tracker.local",
  "password": "Admin123!"
}
```

**Response:** `200 OK`
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresIn": 86400
}
```

**Response:** `401 Unauthorized`
```json
{
  "message": "Invalid email or password"
}
```

**Notes:**
- `expiresIn` is in seconds (default 86400 = 24 hours)
- Token contains `sub` (user ID), `email`, and `jti` (unique token ID) claims
- No public registration endpoint — admin account is seeded on startup from config
- Returns `429 Too Many Requests` if rate limit is exceeded

---

### GET /api/items

Returns all tracked items with their most recent snapshot data. **Requires authentication.**

**Response:** `200 OK`
```json
[
  {
    "id": 15363,
    "name": "Premium Lahn Set",
    "grade": 3,
    "latestSnapshot": {
      "recordedAt": "2026-03-03T01:30:00Z",
      "totalTrades": 45230,
      "currentStock": 0,
      "basePrice": 349000000,
      "lastSoldPrice": 349000000,
      "totalPreorders": 85
    }
  }
]
```

`latestSnapshot` is `null` if no snapshots have been collected yet.

---

### GET /api/items/{id}/velocity

Returns sales velocity metrics across all time windows for a specific item. **Requires authentication.**

**Path Parameters:**
- `id` (int) — BDO item ID

**Response:** `200 OK`
```json
{
  "itemId": 15363,
  "name": "Premium Lahn Set",
  "windows": [
    {
      "window": "3h",
      "salesCount": 5,
      "salesPerHour": 1.67,
      "avgPreorders": 85,
      "confidence": "low"
    },
    {
      "window": "12h",
      "salesCount": 18,
      "salesPerHour": 1.50,
      "avgPreorders": 82,
      "confidence": "medium"
    },
    {
      "window": "24h",
      "salesCount": 34,
      "salesPerHour": 1.42,
      "avgPreorders": 80,
      "confidence": "medium"
    },
    {
      "window": "3d",
      "salesCount": 95,
      "salesPerHour": 1.32,
      "avgPreorders": 78,
      "confidence": "high"
    },
    {
      "window": "7d",
      "salesCount": 210,
      "salesPerHour": 1.25,
      "avgPreorders": 75,
      "confidence": "high"
    },
    {
      "window": "14d",
      "salesCount": 400,
      "salesPerHour": 1.19,
      "avgPreorders": 72,
      "confidence": "high"
    }
  ]
}
```

**Response:** `404 Not Found` if item ID is not tracked.

**Notes:**
- `salesPerHour` is computed using a **weighted median** of consecutive segment rates with exponential recency decay (half-life = half the window duration). This is robust to single-sale spikes and favors recent activity.
- `salesCount` is the raw total (latest - earliest `totalTrades`) and will be 0 if fewer than 2 snapshots exist in the window.
- `avgPreorders` is averaged across all snapshots in the window.
- `confidence` indicates data quality: `"low"` (<3 segments or <3 sales), `"medium"` (3–10 segments), `"high"` (>10 segments and ≥10 sales).
- Internally uses a single query to fetch all 14-day snapshots, then slices per window in memory.

---

### GET /api/items/dashboard

Returns all tracked items ranked by fulfillment score (best first). **Requires authentication.**

**Query Parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| `window` | string | `24h` | Time window for velocity calculation. Valid: `3h`, `12h`, `24h`, `3d`, `7d`, `14d` |

**Example:** `GET /api/items/dashboard?window=7d`

**Response:** `200 OK`
```json
[
  {
    "itemId": 15363,
    "name": "Premium Lahn Set",
    "grade": 3,
    "basePrice": 349000000,
    "currentStock": 0,
    "totalPreorders": 85,
    "salesPerHour": 1.42,
    "window": "24h",
    "fulfillmentScore": 0.0167,
    "estimatedFillTime": "~2.5 days",
    "confidence": "medium"
  }
]
```

**Response:** `400 Bad Request` (invalid window value)
```json
{
  "message": "Invalid window 'foo'. Valid values: 3h, 12h, 24h, 3d, 7d, 14d"
}
```

**Field Definitions:**

| Field | Type | Description |
|-------|------|-------------|
| itemId | int | BDO item ID |
| name | string | English item name |
| grade | int | Rarity (0=white, 1=green, 2=blue, 3=gold, 4=orange) |
| basePrice | long | Current market price in silver |
| currentStock | long | Units listed for sale right now |
| totalPreorders | long | Number of competing buyer orders |
| salesPerHour | double | Weighted median sales per hour (recency-weighted, spike-resistant) |
| window | string | The time window used for this calculation |
| fulfillmentScore | double | `salesPerHour / totalPreorders` — higher is better |
| estimatedFillTime | string | Human-readable estimate: "~X.X hrs", "~X.X days", or "N/A" |
| confidence | string | Data quality: `"low"`, `"medium"`, or `"high"` |

**Sorting:** Results are pre-sorted by `fulfillmentScore` descending.

---

### GET /health

Health check endpoint. **No authentication required.**

**Response:** `200 OK` — `Healthy` (includes database connectivity check)

**Response:** `503 Service Unavailable` — `Unhealthy`

---

## Error Responses

| Status | Meaning |
|--------|---------|
| 400 Bad Request | Invalid query parameter (e.g., invalid window value) |
| 401 Unauthorized | Missing or invalid JWT token |
| 404 Not Found | Item ID not tracked |
| 429 Too Many Requests | Rate limit exceeded (login endpoint) |
| 500 Internal Server Error | Unexpected server error |

---

## arsha.io Endpoints Used

These are the external endpoints the backend calls. Full docs: [Postman Collection](https://documenter.getpostman.com/view/4028519/2s9Y5YRhp4)

### GET /util/db

Item database lookup.

```
GET https://api.arsha.io/util/db?lang=en
```

**Response:** Array of all BDO items
```json
[
  { "id": 702, "name": "Elixir of Will", "grade": 1 },
  { "id": 15363, "name": "Premium Lahn Set", "grade": 3 }
]
```

### GET /v2/{region}/item

Market data for one or more items.

```
GET https://api.arsha.io/v2/na/item?id=15363&lang=en
```

**Response:** Single item (no enhancement levels)
```json
{
  "name": "Premium Lahn Set",
  "id": 15363,
  "sid": 0,
  "basePrice": 349000000,
  "currentStock": 0,
  "totalTrades": 45230,
  "lastSoldPrice": 349000000,
  "lastSoldTime": 1772460959
}
```

Supports multiple IDs (batched in groups of 20): `?id=15363&id=15364&id=15365`

### GET /v2/{region}/orders

Order book for a specific item and enhancement level.

```
GET https://api.arsha.io/v2/na/orders?id=15363&sid=0&lang=en
```

**Response:**
```json
{
  "name": "Premium Lahn Set",
  "id": 15363,
  "sid": 0,
  "orders": [
    { "price": 340000000, "sellers": 0, "buyers": 45 },
    { "price": 349000000, "sellers": 0, "buyers": 40 },
    { "price": 358000000, "sellers": 2, "buyers": 0 }
  ]
}
```

### WSS /events

WebSocket endpoint for real-time cache invalidation events.

```
wss://api.arsha.io/events
```

**Heartbeat** (every 30 seconds):
```json
{ "type": "heartbeat", "host": "api-server-1" }
```

**Cache expiry** (when data refreshes):
```json
{ "type": "ExpiredEvent", ... }
```
