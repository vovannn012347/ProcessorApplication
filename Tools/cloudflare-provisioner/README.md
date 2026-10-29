# Cloudflare provisioning Worker

Creates each node's Cloudflare Tunnel and `<label>.processor-app.pp.ua` CNAME **without giving the node any Cloudflare credentials**.

- The Cloudflare API token lives only here, as a Worker secret.
- Each node has its own P-256 key (`Module_Data/Portal/keys/provisioning_node.key`, created on first use) and signs every request with it.
- The node's subdomain label is derived from its public key (`base32(SHA-256(key))`, 26 chars). A node can only create, change or delete *its own* tunnel and record; it can't name another node's.
- The node gets back only its **tunnel token**, which can run its own tunnel and nothing else.

Rotating the API token is a Worker-only change (`wrangler secret put CF_API_TOKEN`); nodes are not touched.

## 1. Create the API token (once)

Dashboard → **My Profile → API Tokens → Create Token → Create Custom Token**

| Permission | Resources |
|---|---|
| Account · Cloudflare Tunnel · Edit | Include → your account |
| Zone · DNS · Edit | Include → Specific zone → `processor-app.pp.ua` |

Optionally set a TTL so it expires on its own; rotating is then one `wrangler secret put`.
**Never put this token in a node's settings or in the repo.**

You also need the **Account ID** and **Zone ID**: domain **Overview** page, **API** box on the right.

## 2. Deploy

### Option A: dashboard (no tools)

1. **Workers & Pages → Create → Create Worker** (Hello World), name it `processor-provisioner`, **Deploy**.
2. **Edit code**, replace everything with `src/index.js`, **Deploy**.
3. **Settings → Variables and Secrets**:
   - Text: `ACCOUNT_ID`, `ZONE_ID`, `ZONE_NAME` = `processor-app.pp.ua`
   - Secret: `CF_API_TOKEN` = the token from step 1
   - Secret (optional): `JOIN_KEY` (nodes must send it), `ADMIN_KEY` (enables the admin routes)
4. Recommended: **Storage & Databases → KV → Create** a namespace (e.g. `processor-nodes`), then **Worker → Settings → Bindings → Add → KV namespace**, variable name `NODES`.
   It gives replay protection and the deny list / allowlist.

### Option B: wrangler

```
cd Tools/cloudflare-provisioner
npm install
npx wrangler login
npx wrangler kv namespace create NODES        # paste the id into wrangler.toml, uncomment the block
# fill ACCOUNT_ID / ZONE_ID in wrangler.toml
npx wrangler secret put CF_API_TOKEN
npx wrangler secret put JOIN_KEY              # optional
npx wrangler secret put ADMIN_KEY             # optional
npx wrangler deploy
```

Check: `https://processor-provisioner.<your-subdomain>.workers.dev/v1/health` → `{"ok":true,...}`.
To serve it from your own zone instead, add a custom domain (Worker → Settings → Domains & Routes, or the `[[routes]]` block in `wrangler.toml`).

## 3. Point the nodes at it

The Worker mode is its own tunnel provider, **Cloudflare (Worker)** (`cloudflareworker`), with its own settings class
`CloudflareWorkerSettings` and its own section. The old token-based **Cloudflare** provider (`CloudflareSettings`) is unchanged.

App → **Qr Access → Settings → Tunneling Infrastructure**: select **CloudflareWorkerSettings_key** and fill in

- **Provisioning Service URL:** the Worker URL (only scheme + host are used)
- **Join Key:** if you set `JOIN_KEY`
- **Rotate Tunnel (Days):** optional, e.g. 30

Or in `appsettings.Providerless*.json`:

```json
"PortalAccessSettings": {
  "SelectedTunnel": "cloudflareworker"
},
"CloudflareWorkerSettings": {
  "ServiceUrl": "https://processor-provisioner.example.workers.dev",
  "JoinKey": "",
  "TunnelRotationDays": 30
}
```

`CloudflareWorkerSettings:TunnelToken` is written by the node itself; leave it empty.
The node doesn't need anything from `CloudflareSettings` (AccountId / ZoneId / ProvisionToken) in this mode.

On start the node calls `/v1/provision`, stores the returned tunnel token and runs `cloudflared` with it.
If the Worker is unreachable, the node starts with the stored token (the ingress port stays as last provisioned).
The node clock must be within 2 minutes of real time (signed requests expire).

The node's label is shown on the settings page. **Back up `Module_Data/Portal/keys/`**: a new key means a new subdomain.

## Rotation

| What | How |
|---|---|
| Cloudflare API token | Roll/create a new one, `npx wrangler secret put CF_API_TOKEN` (or edit the secret in the dashboard). Nodes don't change. |
| A node's tunnel token | Set **Rotate Tunnel (Days)** on the node. When the tunnel is older, the next start asks for `rotate: true`; the Worker deletes the old tunnel (old token stops working) and creates a new one under the same hostname. |
| Join key | Change `JOIN_KEY`, then update the nodes. Only affects new provisioning, running tunnels keep working. |

No "token distribution" service is needed: nodes never hold the API token, and each node fetches its own new tunnel token.

## Admin routes (only with `ADMIN_KEY`)

```
curl -H "Authorization: Bearer $ADMIN_KEY" https://<worker>/v1/admin/nodes
curl -X POST -H "Authorization: Bearer $ADMIN_KEY" -H "Content-Type: application/json" \
     -d '{"label":"<26-char label>","reason":"abuse"}' https://<worker>/v1/admin/evict
curl -X POST -H "Authorization: Bearer $ADMIN_KEY" -H "Content-Type: application/json" \
     -d '{"label":"<26-char label>"}' https://<worker>/v1/admin/allow
```

- **evict** deletes the node's tunnel and CNAME and, with KV, blocks the label (`deny:<label>`).
- **allow** removes a block and adds `allow:<label>`. With `REQUIRE_ALLOWLIST = "true"` only allowed labels can provision
  (a refused node's error message contains its label, and the label is shown on its settings page).
- These are what a future watchdog would call.

## What this does and doesn't protect

- A leaked node (or its key) can only touch its own subdomain. Evict it and it's gone.
- A leaked `JOIN_KEY` lets someone create *new* subdomains, not take over existing ones. Use the allowlist if that matters, and rotate the key.
- Someone with Cloudflare dashboard access to the account can still change anything. Use 2FA and keep the account to this pool.
- Without the `NODES` KV binding there is no replay protection (a captured request could be resent within 2 minutes), and no deny list / allowlist.

## Tests

```
npm test
```

Runs the Worker against an in-memory fake of the Cloudflare API (signing, replay, join key, allowlist, rotate, evict, two nodes isolated).
