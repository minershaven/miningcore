#!/usr/bin/env bash
# =====================================================================
#  Miningcore installer for Ubuntu 22.04 / 24.04
#  - installs build tools, .NET 9 SDK and PostgreSQL
#  - creates the miningcore database + tables
#  - clones and builds your Miningcore fork into /opt/miningcore/build
#  - writes a starter config (API on 127.0.0.1:4000 for nginx)
#  - installs a systemd service so Miningcore starts on boot
#
#  Usage:
#    wget https://raw.githubusercontent.com/minershaven/miningcore/main/scripts/install-miningcore.sh
#    sudo bash install-miningcore.sh
#  Re-running is safe: it updates the source, rebuilds and restarts,
#  and never overwrites an existing config.json or database.
#
#  It builds the "main" branch of the Miningcore repo. Merge your pull requests into
#  main first, or build another branch with:  sudo BRANCH=my-branch bash install-miningcore.sh
#
#  Private repo?  Run with a GitHub token:
#    sudo REPO_URL=https://<TOKEN>@github.com/minershaven/miningcore.git bash install-miningcore.sh
# =====================================================================
set -euo pipefail

REPO_URL="${REPO_URL:-https://github.com/minershaven/miningcore.git}"
BRANCH="${BRANCH:-main}"
BASE=/opt/miningcore
SRC="$BASE/src"
BUILD="$BASE/build"
CONFIG="$BASE/config.json"
SVC_USER=miningcore
DB_NAME=miningcore
DB_USER=miningcore
API_PORT=4000

say()  { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m!!  %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31mXX  %s\033[0m\n' "$*"; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root: sudo bash $0"
. /etc/os-release
[[ "${ID:-}" == "ubuntu" ]] || warn "This script is written for Ubuntu (found: ${PRETTY_NAME:-unknown}). Continuing anyway."

# ---------------------------------------------------------------- swap
MEM_MB=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)
SWAP_MB=$(awk '/SwapTotal/ {print int($2/1024)}' /proc/meminfo)
if (( MEM_MB < 3500 && SWAP_MB < 1024 )); then
  say "Only ${MEM_MB} MB RAM and no swap: adding a 4 GB swap file so the build doesn't run out of memory"
  if [[ ! -f /swapfile ]]; then
    fallocate -l 4G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=4096
    chmod 600 /swapfile && mkswap /swapfile
  fi
  swapon /swapfile 2>/dev/null || true
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

# ---------------------------------------------------------- packages
say "Installing build tools and libraries"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y software-properties-common ca-certificates curl git jq \
  cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev \
  libsodium-dev libzmq5 libzmq3-dev libgmp-dev libc++-dev zlib1g-dev golang-go

say "Installing .NET 9 SDK"
if ! (command -v dotnet >/dev/null && dotnet --list-sdks | grep -q '^9\.'); then
  if ! apt-cache policy dotnet-sdk-9.0 | grep -q 'Candidate: [0-9]'; then
    add-apt-repository -y ppa:dotnet/backports   # .NET 9 for Ubuntu 22.04 / 24.04
    apt-get update -y
  fi
  apt-get install -y dotnet-sdk-9.0
fi
dotnet --list-sdks | grep -q '^9\.' || die ".NET 9 SDK did not install"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

say "Installing Rust (needed to build the Quantus hash library)"
export PATH="$HOME/.cargo/bin:$PATH"
if ! command -v cargo >/dev/null; then
  curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --no-modify-path \
    || warn "Rust install failed; libquantus.so (Quantus pools only) will not be built"
fi

say "Installing PostgreSQL"
apt-get install -y postgresql postgresql-contrib
systemctl enable --now postgresql

# --------------------------------------------------------------- user
id -u "$SVC_USER" >/dev/null 2>&1 || useradd --system --home "$BASE" --shell /usr/sbin/nologin "$SVC_USER"
mkdir -p "$BASE" "$BASE/logs"

# ------------------------------------------------------------- source
say "Fetching Miningcore source ($REPO_URL, branch $BRANCH)"
git config --global --add safe.directory "$SRC" 2>/dev/null || true
# Full history is needed: the build runs GitVersion, which fails on shallow clones.
if [[ -d "$SRC/.git" ]]; then
  if [[ "$(git -C "$SRC" rev-parse --is-shallow-repository)" == "true" ]]; then
    git -C "$SRC" fetch --unshallow origin || git -C "$SRC" fetch origin
  fi
  git -C "$SRC" fetch origin "$BRANCH"
  git -C "$SRC" checkout -q -f -B "$BRANCH" "origin/$BRANCH"   # -f: discard hand edits; source of truth is GitHub
  git -C "$SRC" reset --hard "origin/$BRANCH"
else
  rm -rf "$SRC"
  git clone --branch "$BRANCH" "$REPO_URL" "$SRC" || die "Clone failed. If the repo is private, re-run with REPO_URL=https://<TOKEN>@github.com/... (see top of script)"
fi

# -------------------------------------------------------------- build
say "Building Miningcore (this takes several minutes)"
# GitVersion only stamps the version string Miningcore prints at startup, and it
# breaks easily on servers (git ownership checks, detached checkouts), so skip it.
# Build into a staging folder while the pool keeps running, then swap the new files
# in with a short stop/start, so miners are only disconnected for a few seconds.
STAGE="$BASE/build-staging"
rm -rf "$STAGE"
( cd "$SRC/src/Miningcore" && \
  dotnet publish -c Release --framework net9.0 -o "$STAGE" -p:DisableGitVersionTask=true -p:NuGetAudit=false ) \
  || die "Build failed. Scroll up for the first line containing 'error' and send it over. The running pool was not touched."
[[ -x "$STAGE/Miningcore" ]] || die "Build finished but $STAGE/Miningcore was not found. The running pool was not touched."

# The fork's csproj has the native-library build step commented out, so the
# .so files (scrypt, Zano, ProgPow, FiroPoW, Quantus, ...) must be compiled separately.
say "Building native hashing libraries (a few minutes)"
( cd "$SRC/src/Miningcore" && bash build-libs-linux.sh "$STAGE" ) || warn "Some native libraries failed to build (only matters for coins that use them)"

say "Installing the new build"
systemctl stop miningcore 2>/dev/null || true
mkdir -p "$BUILD"
# copy over the old build without deleting anything: a native library that failed to
# rebuild this time keeps its previous working copy
cp -a "$STAGE/." "$BUILD/"
rm -rf "$STAGE"
[[ -x "$BUILD/Miningcore" ]] || die "Install failed: $BUILD/Miningcore was not found"
for lib in libmultihash libcryptonote libzanonote libprogpowz libkawpow libfiropow libquantus; do
  [[ -f "$BUILD/$lib.so" ]] || warn "$lib.so is missing; pools that need it will fail (LTC/DOGE: libmultihash, Zano: libcryptonote/libzanonote/libprogpowz, Neoxa: libkawpow, Firo: libfiropow, Quantus: libquantus)"
done

# ----------------------------------------------------------- database
say "Setting up the PostgreSQL database"
DB_PASS_FILE="$BASE/.dbpass"
if [[ -f "$DB_PASS_FILE" ]]; then
  DB_PASS=$(cat "$DB_PASS_FILE")
else
  DB_PASS=$(openssl rand -hex 20)
  echo -n "$DB_PASS" > "$DB_PASS_FILE"
  chmod 600 "$DB_PASS_FILE"
fi
psql_admin() { sudo -u postgres PGOPTIONS="-c client_min_messages=warning" psql -v ON_ERROR_STOP=1 -qtA "$@"; }

if [[ "$(psql_admin -c "SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'")" != "1" ]]; then
  psql_admin -c "CREATE ROLE $DB_USER WITH LOGIN ENCRYPTED PASSWORD '$DB_PASS'"
else
  psql_admin -c "ALTER ROLE $DB_USER WITH LOGIN ENCRYPTED PASSWORD '$DB_PASS'"
fi
if [[ "$(psql_admin -c "SELECT 1 FROM pg_database WHERE datname='$DB_NAME'")" != "1" ]]; then
  psql_admin -c "CREATE DATABASE $DB_NAME OWNER $DB_USER"
fi
SCRIPTS="$SRC/src/Miningcore/Persistence/Postgres/Scripts"
if [[ "$(psql_admin -d "$DB_NAME" -c "SELECT to_regclass('public.shares') IS NOT NULL")" != "t" ]]; then
  psql_admin -d "$DB_NAME" < "$SCRIPTS/createdb.sql"
  echo "Tables created."
else
  echo "Tables already exist; applying safe migrations only."
fi
[[ -f "$SCRIPTS/add_minereffort.sql" ]] && { echo "SET ROLE $DB_USER;"; cat "$SCRIPTS/add_minereffort.sql"; } | psql_admin -d "$DB_NAME" >/dev/null || true
[[ -f "$SCRIPTS/migrate_add_indexes.sql" ]] && { echo "SET ROLE $DB_USER;"; cat "$SCRIPTS/migrate_add_indexes.sql"; } | psql_admin -d "$DB_NAME" >/dev/null || true

# ------------------------------------------------------------- config
if [[ -f "$CONFIG" ]]; then
  say "Keeping existing $CONFIG"
  # keep the DB password in sync in case it was regenerated
  tmp=$(mktemp) && jq --arg p "$DB_PASS" '.persistence.postgres.password = $p' "$CONFIG" > "$tmp" && mv "$tmp" "$CONFIG" || rm -f "$tmp"
else
  say "Writing starter config to $CONFIG"
  cat > "$CONFIG" <<EOF
{
  "logging": {
    "level": "info",
    "enableConsoleLog": true,
    "enableConsoleColors": false,
    "logFile": "core.log",
    "apiLogFile": "api.log",
    "logBaseDirectory": "$BASE/logs",
    "perPoolLogFile": true
  },
  "banning": { "manager": "Integrated", "banOnJunkReceive": false, "banOnInvalidShares": false },
  "notifications": { "enabled": false },
  "persistence": {
    "postgres": {
      "host": "127.0.0.1",
      "port": 5432,
      "user": "$DB_USER",
      "password": "$DB_PASS",
      "database": "$DB_NAME"
    }
  },
  "paymentProcessing": { "enabled": true, "interval": 600, "shareRecoveryFile": "$BASE/recovered-shares.txt" },
  "api": {
    "enabled": true,
    "listenAddress": "127.0.0.1",
    "port": $API_PORT,
    "metricsIpWhitelist": [],
    "rateLimiting": { "disabled": true }
  },
  "pools": [
    {
      "id": "zano-pplns",
      "enabled": false,
      "coin": "zano",
      "address": "REPLACE_WITH_YOUR_ZANO_POOL_WALLET_ADDRESS",
      "rewardRecipients": [
        { "type": "op", "address": "REPLACE_WITH_YOUR_ZANO_FEE_ADDRESS", "percentage": 1.0 }
      ],
      "blockRefreshInterval": 250,
      "jobRebroadcastTimeout": 3,
      "clientConnectionTimeout": 600,
      "banning": { "enabled": true, "time": 600, "invalidPercent": 50, "checkThreshold": 50 },
      "ports": {
        "3364": {
          "listenAddress": "0.0.0.0",
          "name": "GPU",
          "difficulty": 250000000,
          "varDiff": { "minDiff": 250000000, "maxDiff": null, "targetTime": 15, "retargetTime": 90, "variancePercent": 30, "maxDelta": 500 }
        }
      },
      "daemons": [
        { "host": "127.0.0.1", "port": 11211, "user": null, "password": null },
        { "host": "127.0.0.1", "port": 11212, "user": null, "password": null, "category": "wallet" }
      ],
      "paymentProcessing": {
        "enabled": true,
        "minimumPayment": 0.1,
        "payoutScheme": "PPLNS",
        "payoutSchemeConfig": { "factor": 0.5 }
      }
    }
  ]
}
EOF
fi
chmod 640 "$CONFIG"
chown -R "$SVC_USER:$SVC_USER" "$BASE"
chown root:"$SVC_USER" "$CONFIG"

# ------------------------------------------------------------ service
say "Installing systemd service"
cat > /etc/systemd/system/miningcore.service <<EOF
[Unit]
Description=Miningcore mining pool
After=network-online.target postgresql.service
Wants=network-online.target
Requires=postgresql.service

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=$BUILD
ExecStart=$BUILD/Miningcore -c $CONFIG
Restart=always
RestartSec=10
LimitNOFILE=1048576
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_gcServer=1

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable miningcore >/dev/null

# Miningcore exits with "No pools are enabled." if every pool is disabled,
# so only start it once at least one pool is switched on.
if [[ "$(jq '[.pools[]? | select(.enabled == true)] | length' "$CONFIG")" -gt 0 ]]; then
  systemctl restart miningcore
  say "Waiting for the API on 127.0.0.1:$API_PORT"
  ok=0
  for _ in $(seq 1 45); do
    if curl -sf "http://127.0.0.1:$API_PORT/api/pools" >/dev/null; then ok=1; break; fi
    sleep 2
  done
  if (( ok )); then
    echo "API is up:"
    curl -s "http://127.0.0.1:$API_PORT/api/pools" | head -c 300; echo
  else
    warn "API did not answer yet. Check the log with:  journalctl -u miningcore -n 80 --no-pager"
  fi
else
  systemctl stop miningcore 2>/dev/null || true
  warn "No pools are enabled in $CONFIG yet, so Miningcore was installed but NOT started (it refuses to run with zero pools)."
fi

if command -v ufw >/dev/null && ufw status | grep -q 'Status: active'; then
  warn "ufw is active: open each stratum port you enable, e.g.  sudo ufw allow 3364/tcp"
fi

cat <<EOF

Done.
  Config:    $CONFIG   (edit, then: sudo systemctl restart miningcore)
  Binary:    $BUILD/Miningcore
  Logs:      journalctl -u miningcore -f     and  $BASE/logs/
  DB pass:   stored in $DB_PASS_FILE

The starter Zano pool is DISABLED. To turn it on:
  1. Run zanod (RPC 11211) and simplewallet (RPC 11212) on this server, fully synced.
  2. Put your pool wallet address in "address" and your fee address in "rewardRecipients".
  3. Set "enabled": true, then  sudo systemctl restart miningcore
  4. Check:  curl -s http://127.0.0.1:$API_PORT/api/pools   (then set demoFallback: false in the website's config.js)
EOF
