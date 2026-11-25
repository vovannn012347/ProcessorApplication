# ProcessorApplication on Linux

How to build the application for Linux, run it as a service, and use it from other computers on the local network.

## 1. Build (on the Windows development PC)

Modules aren't referenced by the main project; the build copies them into its `Modules/` folder. So build the **whole solution** for the Linux platform instead of using `dotnet publish`.

```bash
# Git Bash, repository root
dotnet build ProcessorApplication.sln -c Release -p:BuildRid=linux-x64
```

- Output: `bin/linux-x64/Release/ProcessorApplication/` (the program plus `Modules/`).
- The application is self-contained: it carries .NET 6, so the Linux machine needs no .NET installed.
- Use `-p:BuildRid=...` here. The SDK rejects a RuntimeIdentifier for a whole solution (`-r` and `-p:RuntimeIdentifier` both fail with NETSDK1134); `BuildRid` sets it for each project instead (`Directory.Build.props`).

## 2. Install on the Linux machine

```bash
# copy the program
scp -r bin/linux-x64/Release/ProcessorApplication user@box:/tmp/
ssh user@box
sudo mv /tmp/ProcessorApplication /opt/processor
sudo chown -R root:root /opt/processor && sudo chmod -R a+rX /opt/processor
sudo chmod +x /opt/processor/ProcessorApplication

# a service account and its node home (databases, keys, Module_Data, settings)
sudo useradd --system --home /var/lib/processor --shell /usr/sbin/nologin processor
sudo mkdir -p /var/lib/processor/node1
sudo chown -R processor:processor /var/lib/processor
sudo chmod 750 /var/lib/processor /var/lib/processor/node1
```

- **Node home:** keep it at mode 750, and the files in it at 640. The OSUser sandbox worker must not be able to read the databases or keys.
- **Don't copy a node home from Windows.** Its database belongs to that node's `node.key` and key chain, and a copy without them can't open users' keys. A new Linux node starts with a fresh home.

**Script executor:** build the Linux executor as described in `LINUX.md` of the `python-standalone-pyinstaller` repository. Install it to `/opt/processor-executor`, then set Processing → Settings → **Python Executable Path** to `/opt/processor-executor/executor`. For a first run, sandbox mode **None** is the low-risk choice. **OSUser** additionally needs the worker account and sudo setup, and there are no CPU or memory limits on Linux yet.

## 3. First admin (headless machine)

The first admin account can only be created from **this computer**. A headless box has no browser, so put the admin in a file instead.
The node uses the file only while it has no admin yet. It creates the admin with the node's own keys and gives it all roles, like the registration page does.

`admin.json`:

```json
{ "Name": "Admin", "Surname": "", "Email": "admin@example.com", "Password": "a-strong-password" }
```

The password must meet the normal password rules. If it doesn't, the log says why, and the file stays so you can fix it.
The file has to hold the password itself (not a hash): the admin's personal key is locked with it.

**Where the node looks for it** (first match wins):

| Place | After the admin is created |
|---|---|
| Path given with `--AdminFile /path/admin.json` (or environment variable `AdminFile=/path/admin.json`) | kept |
| Node home: `admin.json` (or `admin-seed.json`) | deleted |
| Program folder: `/opt/processor/admin.json`, shared by every node started from that program folder | kept |

`"DeleteAfterUse": true` or `false` in the file overrides the default.

**One node:** put the file in the node home; it disappears after the first start.

```bash
sudo install -o processor -g processor -m 600 admin.json /var/lib/processor/node1/admin.json
```

**Many nodes (e.g. 20 headless machines):**
- Ship one `admin.json` with the program: copy it to `/opt/processor/admin.json` on each machine, or point every node to it with `--AdminFile`.
- Each node creates its own admin from it on first start. A shared file is **kept**, because the other nodes still need it.
- It holds the password in clear text, so delete it once every node has started.
- The log reminds you on every start for as long as the file is there.

```bash
sudo install -o root -g processor -m 640 admin.json /opt/processor/admin.json
# ... start all nodes, check each log for "[Admin seed] Admin user created" ...
sudo rm /opt/processor/admin.json
```

Each node keeps its own admin. Changing the password on one node doesn't change it on the others.

**Without a file:** run `ssh -L 5000:localhost:5000 user@box` and open `http://localhost:5000` on your own PC. The application sees the request as local and shows the admin registration page.

## 4. Access from the local network (HTTPS)

By default the application listens on `localhost` only, so other computers can't reach it. For the LAN, use **HTTPS**:

- **Why not plain HTTP.** After login the application keeps the user's decrypted personal key in a session cookie marked "secure only". Browsers accept that cookie over plain HTTP only for `localhost`. Over `http://192.168.x.x` the cookie is dropped: login seems to work, but profile and encrypted-data pages fail. Loosening the cookie would send the key across the network unencrypted.

**Certificate** (self-signed; browsers warn once). Replace `192.168.1.50` with the box's address:

```bash
sudo mkdir -p /etc/processor
sudo openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=processor" \
  -addext "subjectAltName=IP:192.168.1.50" \
  -keyout /etc/processor/key.pem -out /etc/processor/cert.pem
sudo openssl pkcs12 -export -inkey /etc/processor/key.pem -in /etc/processor/cert.pem \
  -out /etc/processor/cert.pfx -passout pass:CHANGE_ME
sudo rm /etc/processor/key.pem
sudo chown processor:processor /etc/processor/cert.pfx && sudo chmod 600 /etc/processor/cert.pfx
```

**Settings** (`/etc/processor/processor.env`):

```bash
# HTTPS for the network, plain HTTP only on this machine (the Cloudflare tunnel can use that one)
ASPNETCORE_URLS=https://0.0.0.0:5001;http://localhost:5000
Kestrel__Certificates__Default__Path=/etc/processor/cert.pfx
Kestrel__Certificates__Default__Password=CHANGE_ME
```

```bash
sudo chown root:processor /etc/processor/processor.env && sudo chmod 640 /etc/processor/processor.env
```

**Firewall:** open the port to your LAN only. Example for `ufw`, if it's enabled:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5001 proto tcp
sudo ufw allow from 192.168.0.0/24 to any port 5001 proto tcp
```

Open `https://192.168.1.50:5001` from another computer and accept the certificate warning once.

## 5. Admin pages from other computers

Admin pages work from this computer only, unless you allow more:

1. Log in as admin. From another computer this works once the steps in section 4 are done, or use the SSH forward from section 3.
2. Open **Settings → Security → "Admin access from other computers"**.
3. Type a network (e.g. `192.168.1.0/24`) and press **Add**; repeat for each one (`192.168.0.0/24`, …). Single addresses (`192.168.1.20`) and IPv6 networks also work. The × removes an entry. An empty list means this computer only.
4. Save.

- **First admin setup** stays this-computer-only regardless (section 3).
- **Requests through the Cloudflare tunnel** carry the visitor's real public address. They only get admin access if that address is listed, so don't list public networks.

## 6. Run as a service (systemd)

`/etc/systemd/system/processor.service`:

```ini
[Unit]
Description=ProcessorApplication node
After=network-online.target
Wants=network-online.target

[Service]
User=processor
Group=processor
WorkingDirectory=/opt/processor
EnvironmentFile=/etc/processor/processor.env
ExecStart=/opt/processor/ProcessorApplication --node-home /var/lib/processor/node1
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now processor
journalctl -u processor -f      # log
```

## Notes

- **Cloudflare tunnel.** The tunnel's Linux download step (`cloudflared`) hasn't been tested yet. After the first start, check in the log that the tunnel comes up, and that it points at the plain-HTTP port on localhost.
- **P2P over the LAN.** Nodes accept a self-signed certificate from a **private IP address** (192.168.x.x, 10.x.x.x, 172.16-31.x.x, IPv6 ULA/link-local).
  - **How to add the endpoint:** in P2P Settings → Add Endpoints, pick `Local_Network <ip>`, keep the HTTPS port entry (`<ip>:5001`) and remove the localhost-only one.
  - **Why this is safe:** the node handshake proves identity with the node keys, not the certificate.
  - **Use the IP address, not a host name:** host names (and public addresses) still need a valid certificate.
- **Logs.** `journalctl -u processor` shows the app's own log. EF Core's SQL statements go to `/var/lib/processor/node1/logs/sql-<date>.log` instead (only warnings and errors also reach the journal).
- **Updating.** Stop the service, replace `/opt/processor` with the new build, then start the service again. The node home is untouched.
