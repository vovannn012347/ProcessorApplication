// Checks a deployed provisioning Worker end to end, the same way a node does.
//
//   node scripts/try-provision.mjs <worker-url> [join-key] [--keep]
//
// Creates a throwaway node key, signs POST /v1/provision, prints what the Worker returned,
// then calls POST /v1/release with the same key so the test tunnel and CNAME are removed again.
// With --keep the tunnel and CNAME stay; delete them by hand in the dashboard afterwards
// (each run uses a new key, so a later run can't release them).
// Needs Node 20+ (built-in WebCrypto and fetch).

const args = process.argv.slice(2);
const keep = args.includes("--keep");
const [workerUrl, joinKey] = args.filter((a) => a !== "--keep");

if (!workerUrl) {
  console.error("Usage: node scripts/try-provision.mjs <worker-url> [join-key] [--keep]");
  process.exit(2);
}

const origin = new URL(workerUrl).origin;
const b64 = (buf) => Buffer.from(buf).toString("base64");
const hex = (buf) => Buffer.from(buf).toString("hex");

const keys = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const spki = new Uint8Array(await crypto.subtle.exportKey("spki", keys.publicKey));

async function signedPost(path, body) {
  const bodyBytes = new TextEncoder().encode(JSON.stringify(body));
  const ts = String(Math.floor(Date.now() / 1000));
  const nonce = Buffer.from(crypto.getRandomValues(new Uint8Array(18))).toString("base64url");
  const bodyHash = hex(await crypto.subtle.digest("SHA-256", bodyBytes));
  const canonical = ["PNODE-V1", "POST", path, ts, nonce, bodyHash].join("\n");
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keys.privateKey, new TextEncoder().encode(canonical));

  const headers = {
    "Content-Type": "application/json",
    "X-Node-Key": b64(spki),
    "X-Timestamp": ts,
    "X-Nonce": nonce,
    "X-Signature": b64(sig),
  };
  if (joinKey) headers["X-Join-Key"] = joinKey;

  const res = await fetch(origin + path, { method: "POST", headers, body: bodyBytes });
  let data;
  try { data = await res.json(); } catch { data = null; }
  return { status: res.status, data };
}

function mask(token) {
  return typeof token === "string" && token.length > 12 ? `${token.slice(0, 6)}…${token.slice(-4)} (${token.length} chars)` : token;
}

// 1. health
const health = await fetch(origin + "/v1/health").then(async (r) => ({ status: r.status, data: await r.json().catch(() => null) }));
console.log(`health: ${health.status}`, health.data);
if (health.status !== 200) process.exit(1);

// 2. provision
const prov = await signedPost("/v1/provision", { scheme: "https", port: 7107 });
if (prov.status !== 200) {
  console.error(`provision FAILED: ${prov.status}`, prov.data);
  process.exit(1);
}
console.log("provision OK:");
console.log(`  hostname : ${prov.data.hostname}`);
console.log(`  url      : ${prov.data.url}`);
console.log(`  tunnel   : ${prov.data.tunnelName} (${prov.data.tunnelId})`);
console.log(`  token    : ${mask(prov.data.tunnelToken)}`);

// 3. same key again: must reuse the same tunnel
const again = await signedPost("/v1/provision", { scheme: "https", port: 7108 });
if (again.status !== 200 || again.data.tunnelId !== prov.data.tunnelId) {
  console.error(`second provision unexpected: ${again.status}`, again.data);
  process.exit(1);
}
console.log("second provision reused the same tunnel: OK");

if (keep) {
  console.log("--keep: tunnel and CNAME left in place.");
  process.exit(0);
}

// 4. release
const rel = await signedPost("/v1/release", {});
if (rel.status !== 200) {
  console.error(`release FAILED: ${rel.status}`, rel.data);
  process.exit(1);
}
console.log("release OK:", rel.data);
console.log("Worker works.");
