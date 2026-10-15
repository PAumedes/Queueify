# Hosting Queueify.Web

`Queueify.Web` is one ASP.NET Core process with its data in one folder. This page covers three ways to run it
and the settings that matter for every one of them. Design background: [ADR-0010](adr/0010-web-app-and-shared-queue.md).

## What every host needs

- **HTTPS in front of it.** Outside Development the server treats every request as https and marks its cookies
  `Secure`, so TLS must end at a reverse proxy, tunnel or load balancer. A server kept on plain http on purpose
  (a home network) sets `Queueify__AllowInsecureCookies=true`, and browsers will still refuse its cookies on
  anything but `localhost`.
- **One data folder, backed up whole.** `Queueify__DataDir` holds the account database (`identity.db`), one
  folder per user, the Data Protection keys (`keys/`), the OCR models and the `owner-exists` marker. The keys and
  the encrypted Spotify/Tidal secrets are in the same folder, so a copy of it opens them: keep backups private.
  Back up the whole folder, or none of it is consistent. Stop the server, or copy a snapshot, for a clean copy.
- **Tesseract** (with the Spanish and English models) on the machine, for photos. The Docker image has it.

## Settings (environment variables)

ASP.NET Core reads `Section__Key` from the environment (or `appsettings.Production.json`).

| Variable | Meaning |
| --- | --- |
| `Queueify__DataDir` | The data folder. Default `App_Data` beside the app. In Docker: `/data`. |
| `Queueify__PublicBaseUrl` | The address people reach the site at, e.g. `https://queue.example.org`. The connect text for the Windows app is built from it, and the CSP names its websocket. Set it: without it the text uses the request's Host header. |
| `AllowedHosts` | Host names the server answers to, e.g. `queue.example.org`; default `*`. Separate names with `;`. Keep `localhost` in the list: the Docker image's health check calls the server by that name. |
| `Queueify__AllowRegistration` | `true` lets anyone register. Default: the first account owns the server, then registration closes (deleting that account does not reopen it; see SECURITY.md). |
| `Queueify__Proxy__KnownProxies` | Addresses of your reverse proxy (comma-free: `Queueify__Proxy__KnownProxies__0=10.0.0.5`). Only those may set `X-Forwarded-For/Proto`. `Queueify__Proxy__KnownNetworks__0=172.16.0.0/12` takes CIDR ranges. Without it the rate limit sees the proxy's address for everybody. |
| `Queueify__AllowInsecureCookies` | `true` only for a plain-http server (see above). |
| `Ocr__TesseractPath`, `Ocr__TessdataPath` | The tesseract program and a folder of models; empty `TessdataPath` downloads the two models once into the data folder. |
| `ConnectionStrings__DefaultConnection` | Moves the account database; leave it to keep `identity.db` in the data folder. |

The first account: register it yourself before exposing the site, because whoever registers first owns it.
Check `/healthz` (answers `ok`) for monitoring.

## Docker (any host with Docker)

```sh
docker build -t queueify-web .
docker run -d --name queueify --restart unless-stopped -p 127.0.0.1:8080:8080 -v queueify-data:/data \
  -e Queueify__PublicBaseUrl=https://queue.example.org -e "AllowedHosts=queue.example.org;localhost" \
  -e Queueify__Proxy__KnownProxies__0=172.17.0.1 queueify-web
```

The image runs as a non-root user, keeps everything in the `/data` volume, includes tesseract with Spanish and
English, and has a `HEALTHCHECK` on `/healthz`. Put a reverse proxy (Caddy, nginx, Traefik) in front for TLS
and set `KnownProxies` to the address the proxy connects from (check the container network; `172.17.0.1` is the
usual bridge gateway when the proxy runs on the host). Back up with `docker run --rm -v queueify-data:/data -v "$PWD":/b busybox tar czf /b/queueify.tgz /data`
after stopping the container. (The Dockerfile has not been built in the repository's CI; build it once before relying on it.)

## At home, behind a Cloudflare Tunnel

Good for a PC or small server that is always on, without opening a port on the router.

1. Install the .NET 10 runtime and tesseract (Windows installer, or `apt install tesseract-ocr tesseract-ocr-spa tesseract-ocr-eng`), or use Docker as above.
2. Publish and run: `dotnet publish Queueify.Web -c Release -o <folder>`, then
   `Queueify__DataDir=<data folder> ASPNETCORE_URLS=http://127.0.0.1:8080 dotnet Queueify.Web.dll` (as a service: systemd or Task Scheduler).
3. Install `cloudflared`, create a tunnel and point a public host name at `http://127.0.0.1:8080`. The tunnel
   connects from the same machine, so set `Queueify__Proxy__KnownProxies__0=127.0.0.1`.
4. Set `Queueify__PublicBaseUrl` and `AllowedHosts` to the tunnel's host name; register your account.

Cloudflare terminates TLS, so cookies stay `Secure` end to end. Mind that a home PC that sleeps or loses power is
a server that is down; keep the data folder on a disk you back up.

## A free-tier cloud VM (for example Oracle Cloud Always Free)

A small Linux VM runs this fine (the app is light; reading a very large photo can briefly use around a gigabyte, so keep `Ocr__MaxPixels` modest on a small VM).
Offers and limits change: **check the provider's current free-tier terms and what it does with idle or
unverified accounts before depending on it.** Steps, in short:

1. Create an Ubuntu VM (ARM or x86; the image builds on both) and open ports 80 and 443 in the cloud firewall
   and the VM's own firewall.
2. Install Docker and run the container as above, bound to `127.0.0.1:8080`.
3. Run Caddy or nginx on the VM for TLS (Let's Encrypt) in front of it, with your domain pointing at the VM.
4. Set the variables from the table, and take regular copies of the volume off the VM.

## Why not Vercel (or other serverless hosts)

Queueify.Web is a long-running .NET process: its Blazor circuits hold a WebSocket open per browser, it writes
SQLite files and encryption keys to a local disk that must persist, and it starts the `tesseract` program.
Serverless and static hosts have no .NET server runtime, no persistent local disk and no way to run that program, so
none of those three can work there. Use a VM, a container host with a persistent volume, or your own machine.

## Upgrading from an early build

Early builds kept the account database at `Data/app.db` beside the app. The server now uses `<DataDir>/identity.db`,
and a fresh one has no accounts, so before the first start of a new version move the old file there
(`Data/app.db` to `<DataDir>/identity.db`), or the first visitor becomes the owner of an empty server. Or set
`ConnectionStrings__DefaultConnection` to the old path.
