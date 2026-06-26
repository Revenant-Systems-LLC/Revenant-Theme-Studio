/**
 * Revenant Theme Studio — License Validation Worker
 *
 * Deploy:  wrangler deploy
 * Secret:  wrangler secret put RTS_LICENSE_SECRET
 *          Value: the 32-byte HMAC secret as a 64-char hex string
 *
 * POST /api/validate
 *   Body:   { "key": "RTSP-XXXXXXXX-XXXXXXXX-XXXXXXXX" }
 *   200 OK: { "valid": true,  "tier": "pro" }
 *        or { "valid": false, "tier": null }
 */
export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'OPTIONS') {
      return corsPreflightResponse();
    }

    if (request.method !== 'POST' || url.pathname !== '/api/validate') {
      return new Response('Not found', { status: 404 });
    }

    let body;
    try {
      body = await request.json();
    } catch {
      return jsonResponse({ valid: false, error: 'Invalid JSON' }, 400);
    }

    const { key } = body;
    if (!key || typeof key !== 'string') {
      return jsonResponse({ valid: false, tier: null });
    }

    const valid = await validateKey(key, env.RTS_LICENSE_SECRET);
    return jsonResponse({ valid, tier: valid ? 'pro' : null });
  },
};

async function validateKey(rawKey, secretHex) {
  try {
    let cleaned = rawKey.replace(/-/g, '').replace(/\s/g, '').toUpperCase();
    if (cleaned.startsWith('RTSP')) cleaned = cleaned.slice(4);
    if (cleaned.length !== 24) return false;

    const bytes = hexToBytes(cleaned);
    const payload  = bytes.slice(0, 6);
    const checksum = bytes.slice(6);

    if (bytes[0] !== 0x01) return false; // 0x01 = Pro tier

    const secretBytes = hexToBytes(secretHex);
    const cryptoKey = await crypto.subtle.importKey(
      'raw',
      secretBytes,
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['sign'],
    );
    const sig = new Uint8Array(await crypto.subtle.sign('HMAC', cryptoKey, payload));

    for (let i = 0; i < 6; i++) {
      if (sig[i] !== checksum[i]) return false;
    }
    return true;
  } catch {
    return false;
  }
}

function hexToBytes(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    bytes[i / 2] = parseInt(hex.slice(i, i + 2), 16);
  }
  return bytes;
}

function jsonResponse(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      'Content-Type': 'application/json',
      'Access-Control-Allow-Origin': '*',
    },
  });
}

function corsPreflightResponse() {
  return new Response(null, {
    headers: {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'POST, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type',
    },
  });
}
