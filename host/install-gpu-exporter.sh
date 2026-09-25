#!/usr/bin/env bash
# Install utkuozdemir/nvidia_gpu_exporter as a systemd service on :9835.
# Safe to re-run: skips the download if the pinned version is already installed.
#
# Usage: sudo ./install-gpu-exporter.sh
# Env:   VERSION (default 1.14.0), PORT (default 9835), INTERVAL (default 2s)
set -euo pipefail

VERSION="${VERSION:-1.14.0}"
PORT="${PORT:-9835}"
INTERVAL="${INTERVAL:-2s}"
BIN=/usr/local/bin/nvidia_gpu_exporter
UNIT=/etc/systemd/system/nvidia-gpu-exporter.service
ASSET="nvidia_gpu_exporter_${VERSION}_linux_x86_64.tar.gz"
BASE="https://github.com/utkuozdemir/nvidia_gpu_exporter/releases/download/v${VERSION}"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi
if [ "$(uname -m)" != "x86_64" ]; then
  echo "This script only handles linux x86_64." >&2
  exit 1
fi
if ! command -v nvidia-smi >/dev/null 2>&1; then
  echo "nvidia-smi not found. Install the NVIDIA driver first." >&2
  exit 1
fi

if [ -x "$BIN" ] && "$BIN" --version 2>&1 | grep -q "version ${VERSION} "; then
  echo "nvidia_gpu_exporter ${VERSION} already installed."
else
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  echo "Downloading ${ASSET}..."
  curl -fsSL -o "$tmp/$ASSET" "$BASE/$ASSET"
  curl -fsSL -o "$tmp/checksums.txt" "$BASE/checksums.txt"
  (cd "$tmp" && grep " ${ASSET}\$" checksums.txt | sha256sum -c -)
  tar -xzf "$tmp/$ASSET" -C "$tmp" nvidia_gpu_exporter
  install -m 0755 "$tmp/nvidia_gpu_exporter" "$BIN"
  echo "Installed $BIN"
fi

if ! id nvidia_gpu_exporter >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin nvidia_gpu_exporter
fi

cat > "$UNIT.tmp" <<UNIT_EOF
[Unit]
Description=NVIDIA GPU Prometheus exporter
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=${BIN} --web.listen-address=:${PORT} --collect.interval=${INTERVAL}
User=nvidia_gpu_exporter
Group=nvidia_gpu_exporter
Restart=always
RestartSec=3
Nice=10

[Install]
WantedBy=multi-user.target
UNIT_EOF

if cmp -s "$UNIT.tmp" "$UNIT"; then
  rm -f "$UNIT.tmp"
else
  mv "$UNIT.tmp" "$UNIT"
  systemctl daemon-reload
fi
systemctl enable nvidia-gpu-exporter.service >/dev/null 2>&1
systemctl restart nvidia-gpu-exporter.service
echo "nvidia-gpu-exporter is running. Test: curl -s http://localhost:${PORT}/metrics | grep nvidia_smi_memory_total_bytes"
