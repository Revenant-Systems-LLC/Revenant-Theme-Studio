# RTS License Validation Worker

Cloudflare Worker that validates Revenant Theme Studio Pro license keys server-side. The HMAC secret never leaves the worker environment.

## Deploy

```bash
npm install -g wrangler
wrangler login

# Set the HMAC secret (64-char hex of the 32-byte key)
wrangler secret put RTS_LICENSE_SECRET

# Deploy
wrangler deploy
```

## Generating keys

Use the `GenerateProKey` utility in `tools/KeyGen/` (internal, not shipped with the app).
Or generate from the Cloudflare Worker admin page using the `/api/generate` endpoint
(not exposed publicly — requires a `ADMIN_TOKEN` header).

## API

### POST /api/validate

**Request**
```json
{ "key": "RTSP-XXXXXXXX-XXXXXXXX-XXXXXXXX" }
```

**Response**
```json
{ "valid": true, "tier": "pro" }
```
or
```json
{ "valid": false, "tier": null }
```
