// Runs the Worker against an in-memory fake of the Cloudflare API.
// Usage: node --test test/worker.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import worker from "../src/index.js";

const ACCOUNT = "acc123";
const ZONE = "zone123";
const ZONE_NAME = "processor-app.pp.ua";

// ------------------------------------------------------------ fake Cloudflare API
function fakeCloudflare() {
  const state = { tunnels: [], dns: [], calls: [], fail: null, seq: 0 };
  const ok = (result) => new Response(JSON.stringify({ success: true, errors: [], result }), { status: 200 });
  const err = (status, message) =>
    new Response(JSON.stringify({ success: false, errors: [{ code: 1000, message }], result: null }), { status });

  globalThis.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === "string" ? input : input.url);
    const method = init.method || "GET";
    const path = url.pathname.replace("/client/v4", "");
    state.calls.push(`${method} ${path}`);
    assert.equal(init.headers.Authorization, "Bearer api-token", "API token must be sent");
    if (state.fail && state.fail(method, path)) return err(403, "Authentication error");
    const body = init.body ? JSON.parse(init.body) : null;

    let m;
    if ((m = path.match(/^\/accounts\/(\w+)\/cfd_tunnel$/))) {
      assert.equal(m[1], ACCOUNT);
      if (method === "GET") {
        const name = url.searchParams.get("name");
        let list = state.tunnels.filter((t) => !t.deleted_at);
        if (name) list = list.filter((t) => t.name === name);
        const per = Number(url.searchParams.get("per_page") || 20);
        const page = Number(url.searchParams.get("page") || 1);
        return ok(list.slice((page - 1) * per, page * per));
      }
      if (method === "POST") {
        if (state.tunnels.some((t) => t.name === body.name && !t.deleted_at)) return err(409, "duplicate name");
        const id = `tun-${++state.seq}`;
        const t = { id, name: body.name, config_src: body.config_src, token: `token-${id}`, config: null, connections: [], status: "inactive", created_at: "2026-10-28T00:00:00Z" };
        state.tunnels.push(t);
        return ok({ ...t, config: undefined });
      }
    }
    if ((m = path.match(/^\/accounts\/\w+\/cfd_tunnel\/([\w-]+)(\/\w+)?$/))) {
      const t = state.tunnels.find((x) => x.id === m[1] && !x.deleted_at);
      if (!t) return err(404, "tunnel not found");
      const sub = m[2];
      if (!sub && method === "DELETE") {
        if (t.connections.length) return err(400, "tunnel has active connections");
        t.deleted_at = "now";
        return ok(t);
      }
      if (sub === "/token" && method === "GET") return ok(t.token);
      if (sub === "/configurations" && method === "PUT") { t.config = body.config; return ok({}); }
      if (sub === "/configurations" && method === "GET") return ok({ config: t.config });
      if (sub === "/connections" && method === "DELETE") { t.connections = []; return ok(null); }
    }
    if ((m = path.match(/^\/zones\/(\w+)\/dns_records(?:\/([\w-]+))?$/))) {
      assert.equal(m[1], ZONE);
      const id = m[2];
      if (!id && method === "GET") return ok(state.dns.filter((r) => r.name === url.searchParams.get("name")));
      if (!id && method === "POST") {
        if (state.dns.some((r) => r.name === body.name)) return err(400, "record exists");
        const r = { id: `dns-${++state.seq}`, ...body };
        state.dns.push(r);
        return ok(r);
      }
      const r = state.dns.find((x) => x.id === id);
      if (!r) return err(404, "record not found");
      if (method === "PUT") { Object.assign(r, body); return ok(r); }
      if (method === "DELETE") { state.dns = state.dns.filter((x) => x !== r); return ok({ id }); }
    }
    return err(404, `unexpected ${method} ${path}`);
  };
  return state;
}

function fakeKv() {
  const map = new Map();
  return {
    map,
    async get(k) { return map.has(k) ? map.get(k) : null; },
    async put(k, v) { map.set(k, v); },
    async delete(k) { map.delete(k); },
  };
}

function makeEnv(extra = {}) {
  return { CF_API_TOKEN: "api-token", ACCOUNT_ID: ACCOUNT, ZONE_ID: ZONE, ZONE_NAME, NODES: fakeKv(), ...extra };
}

// ------------------------------------------------------------ node client (same format as the .NET client)
const b64 = (buf) => Buffer.from(buf).toString("base64");
const hex = (buf) => Buffer.from(buf).toString("hex");

async function makeNode(curve = "P-256") {
  const keys = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: curve }, true, ["sign", "verify"]);
  const spki = new Uint8Array(await crypto.subtle.exportKey("spki", keys.publicKey));
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", spki));
  return { keys, spki, label: base32(digest.slice(0, 16)) };
}

function base32(bytes) {
  const A = "abcdefghijklmnopqrstuvwxyz234567";
  let bits = 0, value = 0, out = "";
  for (const b of bytes) { value = (value << 8) | b; bits += 8; while (bits >= 5) { out += A[(value >>> (bits - 5)) & 31]; bits -= 5; } }
  if (bits > 0) out += A[(value << (5 - bits)) & 31];
  return out;
}

let nonceSeq = 0;
async function signed(node, path, body, opts = {}) {
  const bodyText = body === undefined ? "" : JSON.stringify(body);
  const bodyBytes = new TextEncoder().encode(bodyText);
  const ts = String(opts.ts ?? Math.floor(Date.now() / 1000));
  const nonce = opts.nonce ?? `nonce${String(++nonceSeq).padStart(12, "0")}`;
  const bodyHash = hex(await crypto.subtle.digest("SHA-256", bodyBytes));
  const canonical = ["PNODE-V1", "POST", path, ts, nonce, bodyHash].join("\n");
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, node.keys.privateKey, new TextEncoder().encode(canonical));
  const headers = {
    "Content-Type": "application/json",
    "X-Node-Key": b64(node.spki),
    "X-Timestamp": ts,
    "X-Nonce": nonce,
    "X-Signature": b64(sig),
    ...(opts.headers || {}),
  };
  const sentBody = opts.tamperBody ?? bodyText;
  return new Request(`https://prov.example.workers.dev${path}`, { method: "POST", headers, body: sentBody || undefined });
}

async function call(env, req) {
  const res = await worker.fetch(req, env);
  return { status: res.status, body: await res.json() };
}

// ------------------------------------------------------------ tests
test("health needs no auth", async () => {
  fakeCloudflare();
  const r = await call(makeEnv(), new Request("https://x/v1/health"));
  assert.equal(r.status, 200);
  assert.equal(r.body.ok, true);
});

test("provision creates tunnel, ingress and proxied CNAME derived from the node key", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  const r = await call(env, await signed(node, "/v1/provision", { scheme: "https", port: 7107 }));
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.equal(r.body.label, node.label);
  assert.equal(r.body.label.length, 26);
  assert.equal(r.body.hostname, `${node.label}.${ZONE_NAME}`);
  assert.equal(r.body.url, `https://${node.label}.${ZONE_NAME}`);
  assert.equal(r.body.tunnelToken, `token-${r.body.tunnelId}`);
  assert.equal(r.body.rotated, false);

  const t = cf.tunnels.find((x) => x.id === r.body.tunnelId);
  assert.equal(t.name, `pnode-${node.label}`);
  assert.equal(t.config_src, "cloudflare");
  assert.deepEqual(t.config.ingress, [
    { hostname: r.body.hostname, service: "https://localhost:7107" },
    { service: "http_status:404" },
  ]);
  assert.equal(cf.dns.length, 1);
  assert.equal(cf.dns[0].type, "CNAME");
  assert.equal(cf.dns[0].name, r.body.hostname);
  assert.equal(cf.dns[0].content, `${t.id}.cfargotunnel.com`);
  assert.equal(cf.dns[0].proxied, true);
  assert.match(cf.dns[0].comment, /^pnode:[0-9a-f]{16} /);
});

test("second provision reuses the tunnel and only changes the port", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  const a = await call(env, await signed(node, "/v1/provision", { scheme: "https", port: 7107 }));
  cf.calls.length = 0;
  const b = await call(env, await signed(node, "/v1/provision", { scheme: "http", port: 5044 }));
  assert.equal(b.status, 200);
  assert.equal(b.body.tunnelId, a.body.tunnelId);
  assert.equal(b.body.tunnelToken, a.body.tunnelToken);
  assert.equal(cf.tunnels.filter((t) => !t.deleted_at).length, 1);
  assert.equal(cf.tunnels[0].config.ingress[0].service, "http://localhost:5044");
  assert.ok(!cf.calls.some((c) => c.startsWith("POST /zones")), "CNAME unchanged, no new record");
  assert.ok(!cf.calls.some((c) => c.startsWith("PUT /zones")), "CNAME unchanged, no update");
});

test("rotate replaces the tunnel (old token dies) and repoints the CNAME", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  const a = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  cf.tunnels[0].connections = [{ id: "c1" }]; // connector running
  const b = await call(env, await signed(node, "/v1/provision", { port: 7107, rotate: true }));
  assert.equal(b.status, 200, JSON.stringify(b.body));
  assert.equal(b.body.rotated, true);
  assert.notEqual(b.body.tunnelId, a.body.tunnelId);
  assert.notEqual(b.body.tunnelToken, a.body.tunnelToken);
  assert.ok(cf.tunnels.find((t) => t.id === a.body.tunnelId).deleted_at);
  assert.equal(cf.dns.length, 1);
  assert.equal(cf.dns[0].content, `${b.body.tunnelId}.cfargotunnel.com`);
});

test("two nodes get separate names and can't touch each other", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv();
  const n1 = await makeNode();
  const n2 = await makeNode();
  const a = await call(env, await signed(n1, "/v1/provision", { port: 7107 }));
  const b = await call(env, await signed(n2, "/v1/provision", { port: 7108 }));
  assert.notEqual(a.body.hostname, b.body.hostname);
  await call(env, await signed(n2, "/v1/release", {}));
  assert.ok(!cf.tunnels.find((t) => t.id === a.body.tunnelId).deleted_at, "n1's tunnel untouched");
  assert.ok(cf.dns.some((r) => r.name === a.body.hostname), "n1's record untouched");
  assert.ok(!cf.dns.some((r) => r.name === b.body.hostname));
});

test("signature, timestamp, nonce and key checks", async () => {
  fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  const ok = { port: 7107 };

  let r = await call(env, await signed(node, "/v1/provision", ok, { tamperBody: JSON.stringify({ port: 22 }) }));
  assert.equal(r.status, 401); assert.match(r.body.error, /Bad signature/);

  r = await call(env, await signed(node, "/v1/provision", ok, { ts: Math.floor(Date.now() / 1000) - 600 }));
  assert.equal(r.status, 401); assert.match(r.body.error, /expired/);

  const req1 = await signed(node, "/v1/provision", ok, { nonce: "fixed-nonce-0000001" });
  const req2 = await signed(node, "/v1/provision", ok, { nonce: "fixed-nonce-0000001" });
  assert.equal((await call(env, req1)).status, 200);
  r = await call(env, req2);
  assert.equal(r.status, 401); assert.match(r.body.error, /Replayed/);

  // signature for one path doesn't work on another
  const rel = await signed(node, "/v1/provision", {});
  r = await call(env, new Request("https://x/v1/release", { method: "POST", headers: rel.headers, body: "{}" }));
  assert.equal(r.status, 401);

  const p384 = await makeNode("P-384");
  r = await call(env, await signed(p384, "/v1/provision", ok));
  assert.equal(r.status, 401); assert.match(r.body.error, /Unsupported node key/);

  r = await call(env, new Request("https://x/v1/provision", { method: "POST", body: "{}" }));
  assert.equal(r.status, 401); assert.match(r.body.error, /Missing signature/);

  // another node's key with this node's signature
  const other = await makeNode();
  const req = await signed(node, "/v1/provision", ok);
  req.headers.set("X-Node-Key", b64(other.spki));
  r = await call(env, req);
  assert.equal(r.status, 401);
});

test("input validation", async () => {
  fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  for (const bad of [{ port: 0 }, { port: 70000 }, { port: "7107" }, { port: 7107, scheme: "ftp" }]) {
    const r = await call(env, await signed(node, "/v1/provision", bad));
    assert.equal(r.status, 400, JSON.stringify(bad));
  }
  const big = { port: 7107, pad: "x".repeat(5000) };
  assert.equal((await call(env, await signed(node, "/v1/provision", big))).status, 413);
});

test("join key", async () => {
  fakeCloudflare();
  const env = makeEnv({ JOIN_KEY: "pool-secret" });
  const node = await makeNode();
  let r = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 403);
  r = await call(env, await signed(node, "/v1/provision", { port: 7107 }, { headers: { "X-Join-Key": "wrong" } }));
  assert.equal(r.status, 403);
  r = await call(env, await signed(node, "/v1/provision", { port: 7107 }, { headers: { "X-Join-Key": "pool-secret" } }));
  assert.equal(r.status, 200);
});

test("allowlist, admin evict and allow", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv({ REQUIRE_ALLOWLIST: "true", ADMIN_KEY: "admin-secret" });
  const node = await makeNode();
  const admin = (path, body, key = "admin-secret") =>
    new Request(`https://x${path}`, {
      method: body === undefined ? "GET" : "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

  let r = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 403); assert.match(r.body.error, new RegExp(node.label));

  assert.equal((await call(env, admin("/v1/admin/allow", { label: node.label }, "nope"))).status, 401);
  assert.equal((await call(env, admin("/v1/admin/allow", { label: node.label }))).status, 200);
  r = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 200);

  r = await call(env, admin("/v1/admin/nodes"));
  assert.equal(r.status, 200);
  assert.deepEqual(r.body.nodes.map((n) => n.label), [node.label]);

  r = await call(env, admin("/v1/admin/evict", { label: node.label, reason: "test" }));
  assert.equal(r.status, 200);
  assert.equal(r.body.tunnelDeleted, true);
  assert.equal(r.body.dnsRecordsDeleted, 1);
  assert.equal(cf.dns.length, 0);
  r = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 403); assert.match(r.body.error, /blocked/);

  assert.equal((await call(env, admin("/v1/admin/evict", { label: "../../x" }))).status, 400);
});

test("admin routes are off without ADMIN_KEY", async () => {
  fakeCloudflare();
  const r = await call(makeEnv(), new Request("https://x/v1/admin/nodes", { headers: { Authorization: "Bearer " } }));
  assert.equal(r.status, 404);
});

test("missing configuration and Cloudflare errors", async () => {
  const cf = fakeCloudflare();
  const node = await makeNode();
  let r = await call(makeEnv({ ZONE_ID: "" }), await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 500); assert.match(r.body.error, /not configured/);

  cf.fail = (m, p) => p.includes("/dns_records");
  r = await call(makeEnv(), await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 502); assert.match(r.body.error, /Authentication error/);
  assert.ok(!JSON.stringify(r.body).includes("api-token"));
});

test("works without KV (no replay protection, no lists)", async () => {
  fakeCloudflare();
  const node = await makeNode();
  const r = await call(makeEnv({ NODES: undefined }), await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 200);
  const r2 = await call(makeEnv({ NODES: undefined, REQUIRE_ALLOWLIST: "true" }), await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r2.status, 500);
});

test("stray records at the node's name are replaced", async () => {
  const cf = fakeCloudflare();
  const env = makeEnv();
  const node = await makeNode();
  const host = `${node.label}.${ZONE_NAME}`;
  cf.dns.push({ id: "old1", type: "A", name: host, content: "1.2.3.4", proxied: false });
  cf.dns.push({ id: "old2", type: "AAAA", name: host, content: "::1", proxied: false });
  const r = await call(env, await signed(node, "/v1/provision", { port: 7107 }));
  assert.equal(r.status, 200);
  assert.equal(cf.dns.length, 1);
  assert.equal(cf.dns[0].type, "CNAME");
});
