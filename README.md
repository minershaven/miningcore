<img src="https://github.com/lurchinms/miningcoreV5/blob/main/logo.png" width="150">

High-performance mining pool server for Linux, written in C# / .NET 9. This is the Miningcore build that runs
[theminershaven.org](https://theminershaven.org) (LTC, DOGE, FIRO, ZANO, NEOX, QTC and PRL).

It is a fork of [Miningcore Community Edition](https://github.com/soosho/miningcore) by soosho, which itself continues
the original [Miningcore](https://github.com/oliverw/miningcore) by Oliver Weichhold (archived in 2022). All credit for
the core of the software goes to them; this fork adds coins, fixes and pool-operator features on top.

## Features

- Clusters of pools, each running its own coin, in one process
- Low-latency, multi-threaded Stratum server with adaptive share difficulty (vardiff)
- Native-code share validation (scrypt, ProgPoW/KawPoW/FiroPoW, Zano, Quantus, and more)
- PPLNS, PROP, PPS, SOLO and more payout schemes, with automatic payment processing
- Banning and DDoS/zombie-worker protection
- REST API, WebSocket event stream and Prometheus metrics
- Share relays for running stratum servers in several regions that feed one main pool
- PROXY-protocol support per port, so pools behind a TCP forwarder still see real miner IPs
- Per-pool log files with automatic size limits

### Added in this fork

| Area | What |
|---|---|
| **Quantus (QTC)** | New `quantus` coin family (Poseidon2 qPoW, SRBMiner-MULTI). Talks to `quantus-node` over QUIC and pays out with the Quantus CLI. See [docs/quantus.md](docs/quantus.md). |
| **Pearl (PRL)** | New `pearl` coin family. Speaks the Pearl stratum v1 dialect used by SRBMiner-MULTI; proofs are checked by a Python bridge (`src/Miningcore/Blockchain/Pearl/pearl_mining_bridge.py`). Example: [config-pearl-example.json](src/Miningcore/Blockchain/Pearl/config-pearl-example.json). |
| **Zano** | Updated `libzanonote` (Zano 2.2.3), EthProxy-style miners, `zano` entry in `coins.json`. |
| **Firo** | Stage 3 development/community fund outputs, and the FiroPoW epoch taken from the daemon. |
| **Admin panel** | Password-protected web dashboard: pools, miners, blocks, payments, and a **Wallets** page with each pool wallet's balance, what's owed to miners, and a password-confirmed send. |
| **API** | Correct effort for scrypt coins and before a pool's first block; live per-miner share stream (`/api/pools/{id}/miners/{address}/sharestream`, server-sent events). |
| **Logging** | Log files roll over at `logMaxSizeMB` (default 50) and keep `logMaxArchiveFiles` (default 5). |

## Requirements

- **Ubuntu 24.04 or 22.04** (Linux is the only supported production OS)
- 2 CPU cores and 4 GB RAM or more (2 GB works for one or two coins; the installer adds swap on small servers so the build doesn't run out of memory). Coin nodes need their own RAM and disk on top.
- A full node for every coin you run (and its wallet, for payouts), on the same server or reachable over a private network
- PostgreSQL (installed by the installer)

## Install

The installer sets up everything on a fresh Ubuntu server: build tools, the .NET 9 SDK, Rust (for the Quantus
library), PostgreSQL with the Miningcore database, the build itself in `/opt/miningcore/build`, a starter
`config.json`, and a `miningcore` systemd service.

```console
wget https://raw.githubusercontent.com/minershaven/miningcore/main/scripts/install-miningcore.sh
sudo bash install-miningcore.sh
```

The first run takes 10–20 minutes. When it finishes it prints where everything is:

| | |
|---|---|
| Config | `/opt/miningcore/config.json` |
| Program | `/opt/miningcore/build/Miningcore` |
| Logs | `journalctl -u miningcore -f` and `/opt/miningcore/logs/` |
| Database password | `/opt/miningcore/.dbpass` |

**Updating:** run the same command again. It pulls the latest `main`, rebuilds while the pool keeps running, then
restarts it (miners are disconnected for a few seconds). It never overwrites your `config.json` or database.
To build a different branch: `sudo BRANCH=my-branch bash install-miningcore.sh`.

### After installing

1. **Set up your coin nodes** and make sure each one's RPC is reachable from the pool.
2. **Edit `/opt/miningcore/config.json`.** The starter config has one Zano pool, switched off. Add your pools (see
   [Configuration](#configuration)), put in your wallet addresses, and set `"enabled": true`.
   Miningcore will not start until at least one pool is enabled.
3. **Start it:** `sudo systemctl restart miningcore`, then watch `journalctl -u miningcore -f`.
4. **Open your stratum ports** in the firewall, e.g. `sudo ufw allow 3364/tcp`.
5. **Put a web front end in front of the API.** The API listens on `127.0.0.1:4000` only. Proxy `/api/` to it from
   nginx for your website, and **block `/api/admin`** on the public site (see [Security](#security)).

## Configuration

Miningcore reads one JSON file (`-c config.json`). The [original configuration guide](https://github.com/oliverw/miningcore/wiki/Configuration)
still applies, and there are example pool configs for many coins in [`examples/`](examples).

Coin definitions (algorithm, address format, explorer links) are in
[`src/Miningcore/coins.json`](src/Miningcore/coins.json); a pool's `"coin"` must match a key in that file.

Options added by this fork:

```jsonc
{
  "logging": {
    "logMaxSizeMB": 50,          // roll each log file over at this size (0 = no limit)
    "logMaxArchiveFiles": 5      // old copies kept per log
  },
  "adminPanel": {
    "enabled": true,
    "port": 8080,                // listens on the API's listenAddress (127.0.0.1 by default)
    "password": "long-random-password",
    "sessionTimeout": 28800,     // seconds
    "maxLoginAttempts": 5,
    "loginBanDuration": 900      // seconds
  }
}
```

Database tables are created by the installer. Share-table partitions for new pools are created automatically when
a pool starts.

## Admin panel

With `adminPanel.enabled`, Miningcore serves a dashboard at `http://127.0.0.1:<port>/admin`. To reach it from your
browser, put it behind nginx with HTTPS, for example on port 8443 with your site's certificate:

```nginx
server {
    listen 8443 ssl http2;
    server_name pool.example.com;
    ssl_certificate     /etc/letsencrypt/live/pool.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/pool.example.com/privkey.pem;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 900s;   # Quantus sends wait for the block
    }
}
```

Every page and API call needs a login, and every wallet send asks for the password again.

## Security

- **Block the admin API on your public site.** Miningcore trusts admin API calls that come from the server
  itself, and a reverse proxy on the same server looks like the server itself. If nginx forwards `/api/` to
  Miningcore, add this above that block, or anyone can turn payouts off:

  ```nginx
  location ~* ^/+api/+admin(/|$) { return 404; }
  ```

- Keep the API on `127.0.0.1` and expose it only through nginx.
- Keep coin node RPC ports closed to the internet.
- `config.json` holds wallet and database passwords; the installer makes it readable only by root and the
  `miningcore` service user.

## Building by hand

If you'd rather not use the installer (Ubuntu 22.04/24.04):

```console
sudo add-apt-repository -y ppa:dotnet/backports      # only if dotnet-sdk-9.0 isn't found
sudo apt-get update
sudo apt-get install -y dotnet-sdk-9.0 git cmake clang ninja-build build-essential libssl-dev pkg-config \
  libboost-all-dev libsodium-dev libzmq5 libzmq3-dev libgmp-dev libc++-dev zlib1g-dev golang-go

git clone https://github.com/minershaven/miningcore
cd miningcore/src/Miningcore
dotnet publish -c Release --framework net9.0 -o ../../build -p:DisableGitVersionTask=true
bash build-libs-linux.sh ../../build                 # native hashing libraries (.so files)
```

`build-libs-linux.sh` builds `libquantus.so` only if Rust (`cargo`) is installed.

Then create the database:

```console
sudo -u postgres psql -c "CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'your-secure-password'"
sudo -u postgres psql -c "CREATE DATABASE miningcore OWNER miningcore"
sudo -u postgres psql -d miningcore -f Persistence/Postgres/Scripts/createdb.sql
```

and run it with `../../build/Miningcore -c config.json`.

The older `build-ubuntu-*.sh` / `build-debian-*.sh` scripts, the `Dockerfile` and `build-windows.bat` come from
upstream and are not maintained or tested in this fork. Windows works for development only.

## Coin notes

**Quantus (QTC).** Needs `quantus-node` started with the external-miner QUIC port, `libmsquic`, the Quantus CLI
for payouts, and a timer that collects block rewards into the payout wallet. Full setup in [docs/quantus.md](docs/quantus.md).

**Pearl (PRL).** Needs `pearld` plus the `oyster` wallet, and a Python 3 virtual environment with the Pearl
mining package for proof checking. The wallet passphrase goes in `paymentProcessing.walletPassphrase`. Use
SRBMiner-MULTI 3.8.7 or newer; 3.8.0 sends corrupted proofs.

**Zano.** Needs `zanod` and `simplewallet` running in RPC mode; the wallet is added to the pool's `daemons` with
`"category": "wallet"`.

**Firo.** Block templates must include the Stage 3 development and community fund outputs. They are built into
the code, so if Firo changes those addresses again blocks will be rejected with
`Stage 3 developer/community reward check failed` until `FiroJob.cs` is updated.

**Monero.** `monero-wallet-rpc` must run with `--disable-rpc-login` (Miningcore doesn't support digest auth);
put it behind a reverse proxy if it's reachable. RandomX light mode uses 256 MB per VM; raise `randomXVmCount`,
or set `"randomXFlagsAdd": "RANDOMX_FLAG_FULL_MEM"` for fast mode (about 3 GB per VM).

**Zcash.** Configure both a t-address and a `z-address`; the first daemon must hold both keys. Raise
`equihashMaxThreads` for more throughput (about 1 GB RAM each).

**Vertcoin.** Copy `verthash.dat` to the pool server and set `"vertHashDataFile": "/path/to/verthash.dat"`.

## API

The REST API is documented in the [original wiki](https://github.com/oliverw/miningcore/wiki/API). Main endpoints:
`/api/pools`, `/api/pools/{id}`, `/api/pools/{id}/blocks`, `/api/pools/{id}/payments`,
`/api/pools/{id}/miners/{address}`. Events stream over WebSocket at `/notifications`, and Prometheus metrics are at
`/metrics`.

## Running a production pool

Miningcore has no public website of its own: you need a front end for miners to see their hashrate, balances and
payments. Community front ends such as [miningcore.ui](https://github.com/soosho/miningcore.ui) work with this API.

## Contributing

Issues and pull requests are welcome. Base pull requests on `main`.

## Credits and donations

Built on the work of [Oliver Weichhold](https://github.com/oliverw/miningcore) (original Miningcore) and
[soosho](https://github.com/soosho/miningcore) (Miningcore Community Edition). If this software is useful to you,
consider supporting them:

**soosho (MCCE):** [GitHub Sponsors](https://github.com/sponsors/soosho)
- BTC `1AqqFf13RcfGbwa4GQGQV27T6HL1r35WVk`
- LTC `La2a5YZH3dDJh1L8tTRKk3zz2zUwtYfeHj`
- DOGE `DCMuGWx62HoW6mKVsvfkvuqqqYpsWevfod`

**oliverw (original Miningcore):**
- BTC `16xvkGfG9nrJSKKo5nGWphP8w4hr2ZzVuw`
- LTC `LLs76baYT7iMqQhizxtBC96Cy48iX3Eh1p`
- DOGE `DFuvDSFh4N3SiXGDnye2Vbc8kqvMHbyQE1`
- FIRO `a5AsoTSkfPHQ3SUmR6binG1XW7oQQoFNU1`
- ZANO `ZxDKT1aqiEXPA5cDADtYEfMR1oXsRd68bby4nzUvVmnjHzzrfvjwhNdQ9yiWNeGutzg9LZdwsbP2FGB1gNpZXiYY1fCfpw33c`

## License

A public production pool requires a web-frontend for your users to check their hashrate, earnings etc. Miningcore does not include such frontend but there are several community projects that can be used as starting point.

Once again, do not run a production pool on Windows! This is not a supported configuration.

## Donations

