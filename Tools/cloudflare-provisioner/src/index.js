/**
 * Processor node provisioning Worker.
 *
 * Holds the Cloudflare API token so nodes never see it. A node proves who it is by signing each
 * request with its own P-256 key; the Worker derives the node's subdomain from that public key,
 * so a node can only ever create, change or remove its own tunnel and DNS record.
 *
 * Routes
 *   GET  /v1/health                       liveness, no auth
 *   POST /v1/provision   (node-signed)    create or reuse the node's tunnel, set ingress + CNAME, return the tunnel token
 *   POST /v1/release     (node-signed)    delete the node's tunnel and CNAME
 *   GET  /v1/admin/nodes (ADMIN_KEY)      list managed tunnels
 *   POST /v1/admin/evict (ADMIN_KEY)      block a node and delete its tunnel + CNAME
 *   POST /v1/admin/allow (ADMIN_KEY)      unblock a node / add it to the allowlist
 *
 * Environment
 *   CF_API_TOKEN       secret   Account · Cloudflare Tunnel · Edit + Zone · DNS · Edit (this zone only)
 *   ACCOUNT_ID         var
 *   ZONE_ID            var
 *   ZONE_NAME          var      e.g. processor-app.pp.ua; nodes get <label>.<ZONE_NAME>
 *   TUNNEL_PREFIX      var      optional, default "pnode"; tunnels are named <prefix>-<label>
 *   JOIN_KEY           secret   optional; if set, nodes must send it in X-Join-Key
 *   ADMIN_KEY          secret   optional; enables the /v1/admin routes (Authorization: Bearer <ADMIN_KEY>)
 *   REQUIRE_ALLOWLIST  var      optional "true"; only nodes with an allow:<label> KV key may provision
 *   NODES              KV       optional but recommended; replay protection, deny list, allowlist
 *   RATE_LIMITER       binding  optional Workers rate limiting binding, keyed by client IP
 *
 * Request signing (nodes)
 *   X-Node-Key   base64 DER SubjectPublicKeyInfo of an ECDSA P-256 key
 *   X-Timestamp  unix seconds
 *   X-Nonce      16-64 chars [A-Za-z0-9_-], unique per request
 *   X-Signature  base64 IEEE P1363 (r||s, 64 bytes) ECDSA-SHA256 signature over
 *                "PNODE-V1\n<METHOD>\n<path>\n<timestamp>\n<nonce>\n<hex sha256 of body>"
 */

const API = "https://api.cloudflare.com/client/v4";
const SIGNATURE_SCHEME = "PNODE-V1";
const MAX_SKEW_SECONDS = 120;
const NONCE_TTL_SECONDS = 300; // KV minimum is 60
const MAX_BODY_BYTES = 4096;
const P256_SPKI_LENGTH = 91;
const BASE32 = "abcdefghijklmnopqrstuvwxyz234567";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    try {
      if (request.method === "GET" && url.pathname === "/v1/health") {
        return json({ ok: true, zone: env.ZONE_NAME || null });
      }

      if (request.method === "POST" && url.pathname === "/v1/provision") {
        return await nodeRoute(request, env, url, provision);
      }
      if (request.method === "POST" && url.pathname === "/v1/release") {
        return await nodeRoute(request, env, url, release);
      }

      if (url.pathname.startsWith("/v1/admin/")) {
        return await adminRoute(request, env, url);
      }

      return json({ error: "Not found" }, 404);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.message }, e.status);
      console.error("Unhandled error", e && e.stack ? e.stack : e);
      return json({ error: "Internal error" }, 500);
    }
  },
};

// ---------------------------------------------------------------- node routes

async function nodeRoute(request, env, url, handler) {
  requireConfig(env);
  await rateLimit(request, env);

  const declared = Number(request.headers.get("Content-Length") || "0");
  if (declared > MAX_BODY_BYTES) throw new HttpError(413, "Request body too large");
  const bodyBytes = new Uint8Array(await request.arrayBuffer());
  if (bodyBytes.length > MAX_BODY_BYTES) throw new HttpError(413, "Request body too large");

  const node = await authenticateNode(request, env, url, bodyBytes);

  let body = {};
  if (bodyBytes.length > 0) {
    try {
      body = JSON.parse(new TextDecoder().decode(bodyBytes));
    } catch {
      throw new HttpError(400, "Body is not valid JSON");
    }
  }
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    throw new HttpError(400, "Body must be a JSON object");
  }

  return json(await handler(env, node, body));
}

async function provision(env, node, body) {
  const scheme = body.scheme === undefined ? "https" : body.scheme;
  if (scheme !== "http" && scheme !== "https") throw new HttpError(400, "scheme must be http or https");

  const port = body.port;
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new HttpError(400, "port must be 1-65535");

  const rotate = body.rotate === true;

  let tunnel = await findTunnel(env, node.tunnelName);
  let rotated = false;
  if (tunnel && rotate) {
    await deleteTunnel(env, tunnel.id);
    tunnel = null;
    rotated = true;
  }

  let token = null;
  if (!tunnel) {
    tunnel = await cf(env, "POST", `/accounts/${env.ACCOUNT_ID}/cfd_tunnel`, {
      name: node.tunnelName,
      config_src: "cloudflare",
    });
    token = tunnel.token || null;
  }
  if (!token) {
    token = await cf(env, "GET", `/accounts/${env.ACCOUNT_ID}/cfd_tunnel/${tunnel.id}/token`);
  }
  if (typeof token !== "string" || token.length === 0) {
    throw new HttpError(502, "Cloudflare did not return a tunnel token");
  }

  await cf(env, "PUT", `/accounts/${env.ACCOUNT_ID}/cfd_tunnel/${tunnel.id}/configurations`, {
    config: {
      ingress: [
        { hostname: node.hostname, service: `${scheme}://localhost:${port}` },
        { service: "http_status:404" },
      ],
    },
  });

  await upsertCname(env, node, tunnel.id);

  console.log(`provisioned ${node.label} -> ${tunnel.id}${rotated ? " (rotated)" : ""}`);
  return {
    label: node.label,
    hostname: node.hostname,
    url: `https://${node.hostname}`,
    tunnelId: tunnel.id,
    tunnelName: node.tunnelName,
    tunnelToken: token,
    rotated,
  };
}

async function release(env, node) {
  const removed = await removeNode(env, node.label);
  console.log(`released ${node.label}`);
  return { released: true, hostname: node.hostname, ...removed };
}

// ---------------------------------------------------------------- admin routes

async function adminRoute(request, env, url) {
  requireConfig(env);
  if (!env.ADMIN_KEY) throw new HttpError(404, "Not found");
  await rateLimit(request, env);

  const auth = request.headers.get("Authorization") || "";
  const given = auth.startsWith("Bearer ") ? auth.slice(7) : "";
  if (!(await safeEqual(given, env.ADMIN_KEY))) throw new HttpError(401, "Unauthorized");

  if (request.method === "GET" && url.pathname === "/v1/admin/nodes") {
    const prefix = tunnelPrefix(env) + "-";
    const tunnels = await listTunnels(env);
    const nodes = tunnels
      .filter((t) => t.name.startsWith(prefix))
      .map((t) => {
        const label = t.name.slice(prefix.length);
        return {
          label,
          hostname: `${label}.${env.ZONE_NAME}`,
          tunnelId: t.id,
          status: t.status || null,
          createdAt: t.created_at || null,
          connections: Array.isArray(t.connections) ? t.connections.length : null,
        };
      });
    return json({ nodes });
  }

  if (request.method === "POST" && (url.pathname === "/v1/admin/evict" || url.pathname === "/v1/admin/allow")) {
    let body;
    try {
      body = await request.json();
    } catch {
      throw new HttpError(400, "Body is not valid JSON");
    }
    const label = typeof body?.label === "string" ? body.label.toLowerCase() : "";
    if (!/^[a-z2-7]{26}$/.test(label)) throw new HttpError(400, "label must be a 26-character node label");

    if (url.pathname === "/v1/admin/evict") {
      if (env.NODES) {
        const reason = typeof body.reason === "string" ? body.reason.slice(0, 200) : "evicted";
        await env.NODES.put(`deny:${label}`, `${new Date().toISOString()} ${reason}`);
        await env.NODES.delete(`allow:${label}`);
      }
      const removed = await removeNode(env, label);
      console.log(`evicted ${label}`);
      return json({ evicted: true, label, blocked: !!env.NODES, ...removed });
    }

    if (!env.NODES) throw new HttpError(400, "The NODES KV binding is required for allow/deny lists");
    await env.NODES.delete(`deny:${label}`);
    await env.NODES.put(`allow:${label}`, new Date().toISOString());
    return json({ allowed: true, label });
  }

  throw new HttpError(404, "Not found");
}

// ---------------------------------------------------------------- authentication

async function authenticateNode(request, env, url, bodyBytes) {
  const keyB64 = request.headers.get("X-Node-Key");
  const ts = request.headers.get("X-Timestamp");
  const nonce = request.headers.get("X-Nonce");
  const sigB64 = request.headers.get("X-Signature");
  if (!keyB64 || !ts || !nonce || !sigB64) throw new HttpError(401, "Missing signature headers");

  const spki = decodeBase64(keyB64);
  if (!spki || spki.length !== P256_SPKI_LENGTH) throw new HttpError(401, "Unsupported node key");

  const sig = decodeBase64(sigB64);
  if (!sig || sig.length !== 64) throw new HttpError(401, "Malformed signature");

  if (!/^[A-Za-z0-9_-]{16,64}$/.test(nonce)) throw new HttpError(401, "Malformed nonce");

  const t = Number(ts);
  const now = Math.floor(Date.now() / 1000);
  if (!/^\d{1,12}$/.test(ts) || Math.abs(now - t) > MAX_SKEW_SECONDS) {
    throw new HttpError(401, "Request expired or node clock is off by more than 2 minutes");
  }

  let key;
  try {
    key = await crypto.subtle.importKey("spki", spki, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  } catch {
    throw new HttpError(401, "Unsupported node key");
  }

  const bodyHash = toHex(await sha256(bodyBytes));
  const canonical = [SIGNATURE_SCHEME, request.method, url.pathname, ts, nonce, bodyHash].join("\n");
  const ok = await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig, new TextEncoder().encode(canonical));
  if (!ok) throw new HttpError(401, "Bad signature");

  const digest = await sha256(spki);
  const label = base32(digest.slice(0, 16));
  const fingerprint = toHex(digest).slice(0, 16);

  if (env.JOIN_KEY) {
    const given = request.headers.get("X-Join-Key") || "";
    if (!(await safeEqual(given, env.JOIN_KEY))) throw new HttpError(403, "Join key missing or wrong");
  }

  if (env.NODES) {
    if ((await env.NODES.get(`deny:${label}`)) !== null) throw new HttpError(403, "This node is blocked");
    if (env.REQUIRE_ALLOWLIST === "true" && (await env.NODES.get(`allow:${label}`)) === null) {
      throw new HttpError(403, `This node is not on the allowlist (label ${label})`);
    }
    const nonceKey = `nonce:${label}:${nonce}`;
    if ((await env.NODES.get(nonceKey)) !== null) throw new HttpError(401, "Replayed request");
    await env.NODES.put(nonceKey, "1", { expirationTtl: NONCE_TTL_SECONDS });
  } else if (env.REQUIRE_ALLOWLIST === "true") {
    throw new HttpError(500, "REQUIRE_ALLOWLIST needs the NODES KV binding");
  }

  return {
    label,
    fingerprint,
    hostname: `${label}.${env.ZONE_NAME}`,
    tunnelName: `${tunnelPrefix(env)}-${label}`,
  };
}

async function rateLimit(request, env) {
  if (!env.RATE_LIMITER) return;
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  const { success } = await env.RATE_LIMITER.limit({ key: ip });
  if (!success) throw new HttpError(429, "Too many requests");
}

// ---------------------------------------------------------------- Cloudflare API

async function findTunnel(env, name) {
  const list = await cf(
    env,
    "GET",
    `/accounts/${env.ACCOUNT_ID}/cfd_tunnel?name=${encodeURIComponent(name)}&is_deleted=false`
  );
  return (list || []).find((t) => t.name === name && !t.deleted_at) || null;
}

async function listTunnels(env) {
  const all = [];
  for (let page = 1; page <= 50; page++) {
    const batch = await cf(
      env,
      "GET",
      `/accounts/${env.ACCOUNT_ID}/cfd_tunnel?is_deleted=false&per_page=100&page=${page}`
    );
    if (!batch || batch.length === 0) break;
    all.push(...batch);
    if (batch.length < 100) break;
  }
  return all;
}

async function deleteTunnel(env, tunnelId) {
  // A tunnel with live connections can't be deleted; drop them first.
  try {
    await cf(env, "DELETE", `/accounts/${env.ACCOUNT_ID}/cfd_tunnel/${tunnelId}/connections`);
  } catch (e) {
    console.warn(`connection cleanup for ${tunnelId} failed: ${e.message}`);
  }
  await cf(env, "DELETE", `/accounts/${env.ACCOUNT_ID}/cfd_tunnel/${tunnelId}`);
}

async function findDnsRecords(env, hostname) {
  return (await cf(env, "GET", `/zones/${env.ZONE_ID}/dns_records?name=${encodeURIComponent(hostname)}`)) || [];
}

async function upsertCname(env, node, tunnelId) {
  const target = `${tunnelId}.cfargotunnel.com`;
  const records = (await findDnsRecords(env, node.hostname)).filter((r) => r.name === node.hostname);
  const record = {
    type: "CNAME",
    name: node.hostname,
    content: target,
    proxied: true,
    ttl: 1,
    comment: `pnode:${node.fingerprint} ${new Date().toISOString().slice(0, 19)}Z`,
  };

  const [first, ...extra] = records;
  for (const r of extra) {
    await cf(env, "DELETE", `/zones/${env.ZONE_ID}/dns_records/${r.id}`);
  }

  if (!first) {
    await cf(env, "POST", `/zones/${env.ZONE_ID}/dns_records`, record);
  } else if (first.type !== "CNAME" || first.content !== target || first.proxied !== true) {
    await cf(env, "PUT", `/zones/${env.ZONE_ID}/dns_records/${first.id}`, record);
  }
}

async function removeNode(env, label) {
  const hostname = `${label}.${env.ZONE_NAME}`;
  const tunnel = await findTunnel(env, `${tunnelPrefix(env)}-${label}`);
  if (tunnel) await deleteTunnel(env, tunnel.id);

  const records = (await findDnsRecords(env, hostname)).filter((r) => r.name === hostname);
  for (const r of records) {
    await cf(env, "DELETE", `/zones/${env.ZONE_ID}/dns_records/${r.id}`);
  }
  return { tunnelDeleted: !!tunnel, dnsRecordsDeleted: records.length };
}

async function cf(env, method, path, body) {
  const headers = { Authorization: `Bearer ${env.CF_API_TOKEN}` };
  const init = { method, headers };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }

  let res;
  try {
    res = await fetch(API + path, init);
  } catch (e) {
    console.error(`Cloudflare API ${method} ${path} unreachable: ${e && e.message}`);
    throw new HttpError(502, "Cloudflare API unreachable");
  }
  let data = null;
  try {
    data = await res.json();
  } catch {
    data = null;
  }

  if (!res.ok || !data || data.success === false) {
    const detail =
      data && Array.isArray(data.errors) && data.errors.length
        ? data.errors.map((e) => `${e.code}: ${e.message}`).join("; ")
        : `HTTP ${res.status}`;
    console.error(`Cloudflare API ${method} ${path} failed: ${detail}`);
    throw new HttpError(502, `Cloudflare API ${method} failed: ${detail}`);
  }
  return data.result;
}

// ---------------------------------------------------------------- helpers

function requireConfig(env) {
  const missing = ["CF_API_TOKEN", "ACCOUNT_ID", "ZONE_ID", "ZONE_NAME"].filter((k) => !env[k]);
  if (missing.length) {
    console.error(`Worker is missing configuration: ${missing.join(", ")}`);
    throw new HttpError(500, "Provisioning service is not configured");
  }
}

function tunnelPrefix(env) {
  const p = (env.TUNNEL_PREFIX || "pnode").toLowerCase();
  return /^[a-z0-9-]{1,20}$/.test(p) ? p : "pnode";
}

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}

async function sha256(bytes) {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}

async function safeEqual(a, b) {
  const enc = new TextEncoder();
  const [ha, hb] = await Promise.all([sha256(enc.encode(a)), sha256(enc.encode(b))]);
  if (crypto.subtle.timingSafeEqual) return crypto.subtle.timingSafeEqual(ha, hb);
  let diff = 0;
  for (let i = 0; i < ha.length; i++) diff |= ha[i] ^ hb[i];
  return diff === 0;
}

function decodeBase64(s) {
  try {
    const bin = atob(s.trim());
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  } catch {
    return null;
  }
}

function toHex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

// RFC 4648 base32, lowercase, no padding (DNS-safe)
function base32(bytes) {
  let bits = 0;
  let value = 0;
  let out = "";
  for (const b of bytes) {
    value = (value << 8) | b;
    bits += 8;
    while (bits >= 5) {
      out += BASE32[(value >>> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits > 0) out += BASE32[(value << (5 - bits)) & 31];
  return out;
}
