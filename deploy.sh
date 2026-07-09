#!/bin/bash
# WdpMgr Server — One-click deploy for Ubuntu 20.04+ / Debian 11+
# Usage: bash deploy.sh
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

APP_DIR="/opt/wdpmgr-server"
SERVICE_NAME="wdpmgr"
CF_TUNNEL_NAME="wdpmgr-tunnel"
CF_CONFIG_DIR="/etc/cloudflared"
REPO_URL="https://github.com/HackMe7822/WdpMgr.git"

echo ""
echo "╔══════════════════════════════════════════════╗"
echo "║   WdpMgr Server — Deployment Script         ║"
echo "╚══════════════════════════════════════════════╝"
echo ""

# ── Root check ────────────────────────────────────────────────────────────────
[[ $EUID -ne 0 ]] && error "Run as root:  sudo bash deploy.sh"

# ── Collect configuration ─────────────────────────────────────────────────────
echo "── Configuration ──────────────────────────────"

read -rp "Admin key (leave blank to auto-generate): " ADMIN_KEY
if [[ -z "$ADMIN_KEY" ]]; then
    ADMIN_KEY=$(openssl rand -hex 24)
    info "Generated admin key: ${YELLOW}${ADMIN_KEY}${NC}"
fi

read -rp "Cloudflare API token (Zone Read + DNS Edit): " CF_API_TOKEN
read -rp "Cloudflare Account ID: " CF_ACCOUNT_ID
read -rp "Cloudflare Zone ID: " CF_ZONE_ID
read -rp "Your domain (e.g. example.com): " CF_DOMAIN
read -rp "Subdomain prefix (e.g. wdpmgr → wdpmgr.example.com): " CF_SUBDOMAIN
CF_FULL_DOMAIN="${CF_SUBDOMAIN}.${CF_DOMAIN}"

echo ""
info "Will deploy to:  https://${CF_FULL_DOMAIN}"
read -rp "Continue? [y/N] " CONFIRM
[[ "$CONFIRM" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

# ── Install .NET 8 ────────────────────────────────────────────────────────────
echo ""
echo "── Installing .NET 8 runtime ──────────────────"
if ! command -v dotnet &>/dev/null || [[ $(dotnet --version 2>/dev/null | cut -d. -f1) -lt 8 ]]; then
    info "Installing .NET 8..."
    apt-get update -qq
    apt-get install -y wget ca-certificates
    # Microsoft package feed
    wget -q https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb \
        -O /tmp/packages-microsoft-prod.deb 2>/dev/null || \
    wget -q https://packages.microsoft.com/config/debian/$(lsb_release -rs)/packages-microsoft-prod.deb \
        -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    apt-get update -qq
    apt-get install -y dotnet-sdk-8.0
    info ".NET 8 installed: $(dotnet --version)"
else
    info ".NET $(dotnet --version) already present"
fi

# ── Install cloudflared ───────────────────────────────────────────────────────
echo ""
echo "── Installing cloudflared ─────────────────────"
if ! command -v cloudflared &>/dev/null; then
    info "Downloading cloudflared..."
    ARCH=$(dpkg --print-architecture)
    wget -q "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${ARCH}.deb" \
        -O /tmp/cloudflared.deb
    dpkg -i /tmp/cloudflared.deb
    info "cloudflared installed: $(cloudflared --version)"
else
    info "cloudflared already present: $(cloudflared --version)"
fi

# ── Clone / update repo ────────────────────────────────────────────────────────
echo ""
echo "── Setting up app directory ───────────────────"
if [[ -d "$APP_DIR/.git" ]]; then
    info "Updating existing repo..."
    git -C "$APP_DIR" pull
else
    info "Cloning repo to $APP_DIR..."
    git clone "$REPO_URL" "$APP_DIR"
fi

# ── Build server ──────────────────────────────────────────────────────────────
echo ""
echo "── Building server ────────────────────────────"
PUBLISH_DIR="$APP_DIR/publish"
dotnet publish "$APP_DIR/Server/WdpMgrServer.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_DIR" -v quiet
info "Build complete → $PUBLISH_DIR/WdpMgrServer"
chmod +x "$PUBLISH_DIR/WdpMgrServer"

# ── Write environment file ────────────────────────────────────────────────────
DATA_DIR="/var/lib/wdpmgr"
mkdir -p "$DATA_DIR"
cat > /etc/wdpmgr.env << EOF
WDPMGR_ADMIN_KEY=${ADMIN_KEY}
WDPMGR_DB_PATH=${DATA_DIR}/wdpmgr.db
PORT=5000
EOF
chmod 600 /etc/wdpmgr.env
info "Env file written to /etc/wdpmgr.env"

# ── Create systemd service for app ────────────────────────────────────────────
echo ""
echo "── Creating systemd service ───────────────────"
cat > "/etc/systemd/system/${SERVICE_NAME}.service" << EOF
[Unit]
Description=Windows Display Policy Manager Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=${PUBLISH_DIR}
ExecStart=${PUBLISH_DIR}/WdpMgrServer
EnvironmentFile=/etc/wdpmgr.env
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"
info "Service $SERVICE_NAME started"
sleep 2
systemctl is-active --quiet "$SERVICE_NAME" && info "Service is running ✓" || warn "Service may not be running — check: journalctl -u $SERVICE_NAME"

# ── Set up Cloudflare Tunnel ──────────────────────────────────────────────────
echo ""
echo "── Setting up Cloudflare Tunnel ───────────────"
mkdir -p "$CF_CONFIG_DIR"

# Check if tunnel already exists via API
info "Checking for existing tunnel '$CF_TUNNEL_NAME'..."
TUNNELS_JSON=$(curl -s -X GET \
    "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT_ID}/cfd_tunnel?name=${CF_TUNNEL_NAME}" \
    -H "Authorization: Bearer ${CF_API_TOKEN}" \
    -H "Content-Type: application/json")

TUNNEL_ID=$(echo "$TUNNELS_JSON" | python3 -c "
import sys, json
d = json.load(sys.stdin)
r = d.get('result', [])
print(r[0]['id'] if r else '')
" 2>/dev/null || echo "")

if [[ -z "$TUNNEL_ID" ]]; then
    info "Creating new Cloudflare Tunnel..."
    TUNNEL_SECRET=$(openssl rand -base64 32)
    CREATE_JSON=$(curl -s -X POST \
        "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT_ID}/cfd_tunnel" \
        -H "Authorization: Bearer ${CF_API_TOKEN}" \
        -H "Content-Type: application/json" \
        --data "{\"name\":\"${CF_TUNNEL_NAME}\",\"tunnel_secret\":\"$(echo -n $TUNNEL_SECRET | base64 -w0)\"}")
    TUNNEL_ID=$(echo "$CREATE_JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['result']['id'])" 2>/dev/null)
    [[ -z "$TUNNEL_ID" ]] && error "Failed to create tunnel. Check CF_ACCOUNT_ID and API token permissions.\n$CREATE_JSON"

    # Write credentials file
    cat > "${CF_CONFIG_DIR}/${TUNNEL_ID}.json" << CFEOF
{
  "AccountTag":   "${CF_ACCOUNT_ID}",
  "TunnelID":     "${TUNNEL_ID}",
  "TunnelName":   "${CF_TUNNEL_NAME}",
  "TunnelSecret": "$(echo -n $TUNNEL_SECRET | base64 -w0)"
}
CFEOF
    info "Tunnel created: $TUNNEL_ID"
else
    info "Reusing existing tunnel: $TUNNEL_ID"
fi

# Write cloudflared config
cat > "${CF_CONFIG_DIR}/config.yml" << EOF
tunnel: ${TUNNEL_ID}
credentials-file: ${CF_CONFIG_DIR}/${TUNNEL_ID}.json

ingress:
  - hostname: ${CF_FULL_DOMAIN}
    service: http://localhost:5000
  - service: http_status:404
EOF
info "cloudflared config written to ${CF_CONFIG_DIR}/config.yml"

# ── Create CNAME DNS record (idempotent) ──────────────────────────────────────
echo ""
info "Setting up DNS CNAME: ${CF_FULL_DOMAIN} → ${TUNNEL_ID}.cfargotunnel.com"

EXISTING_RECORD=$(curl -s -X GET \
    "https://api.cloudflare.com/client/v4/zones/${CF_ZONE_ID}/dns_records?type=CNAME&name=${CF_FULL_DOMAIN}" \
    -H "Authorization: Bearer ${CF_API_TOKEN}" \
    -H "Content-Type: application/json")

RECORD_ID=$(echo "$EXISTING_RECORD" | python3 -c "
import sys, json
d = json.load(sys.stdin)
r = d.get('result', [])
print(r[0]['id'] if r else '')
" 2>/dev/null || echo "")

CNAME_TARGET="${TUNNEL_ID}.cfargotunnel.com"

if [[ -z "$RECORD_ID" ]]; then
    info "Creating CNAME record..."
    curl -s -X POST \
        "https://api.cloudflare.com/client/v4/zones/${CF_ZONE_ID}/dns_records" \
        -H "Authorization: Bearer ${CF_API_TOKEN}" \
        -H "Content-Type: application/json" \
        --data "{\"type\":\"CNAME\",\"name\":\"${CF_SUBDOMAIN}\",\"content\":\"${CNAME_TARGET}\",\"proxied\":true}" \
        | python3 -c "import sys,json; d=json.load(sys.stdin); print('Created:', d.get('result',{}).get('name',''))" 2>/dev/null || true
else
    info "Updating existing CNAME record $RECORD_ID..."
    curl -s -X PUT \
        "https://api.cloudflare.com/client/v4/zones/${CF_ZONE_ID}/dns_records/${RECORD_ID}" \
        -H "Authorization: Bearer ${CF_API_TOKEN}" \
        -H "Content-Type: application/json" \
        --data "{\"type\":\"CNAME\",\"name\":\"${CF_SUBDOMAIN}\",\"content\":\"${CNAME_TARGET}\",\"proxied\":true}" \
        | python3 -c "import sys,json; d=json.load(sys.stdin); print('Updated:', d.get('result',{}).get('name',''))" 2>/dev/null || true
fi

# ── Create systemd service for cloudflared ────────────────────────────────────
cat > /etc/systemd/system/cloudflared.service << EOF
[Unit]
Description=Cloudflare Tunnel for WdpMgr
After=network.target

[Service]
Type=simple
User=root
ExecStart=/usr/bin/cloudflared tunnel --config ${CF_CONFIG_DIR}/config.yml run
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable cloudflared
systemctl restart cloudflared
sleep 2
systemctl is-active --quiet cloudflared && info "cloudflared tunnel is running ✓" || warn "Check cloudflared: journalctl -u cloudflared"

# ── Final summary ─────────────────────────────────────────────────────────────
echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║              Deployment Complete!                   ║"
echo "╠══════════════════════════════════════════════════════╣"
printf "║  %-52s║\n" "Admin Panel:  https://${CF_FULL_DOMAIN}"
printf "║  %-52s║\n" "Admin Key:    ${ADMIN_KEY}"
printf "║  %-52s║\n" "DB Path:      ${DATA_DIR}/wdpmgr.db"
printf "║  %-52s║\n" "Tunnel ID:    ${TUNNEL_ID}"
echo "╠══════════════════════════════════════════════════════╣"
echo "║  Next steps:                                        ║"
echo "║  1. Open admin panel, go to Settings               ║"
echo "║  2. Copy RSA Public Key                            ║"
echo "║  3. Run tools\\set-pubkey.bat on your Windows box   ║"
echo "║  4. Recompile WdpMgr.exe, create licenses, deploy  ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""
