#!/usr/bin/env bash
# Render llama-server.service.example into /etc/systemd/system and start it.
# Safe to re-run: only restarts the service when the unit changes.
#
# Usage:
#   sudo MODEL=/models/qwen3-coder-30b-a3b-q4_k_m.gguf ./install-llama-server.sh
# Env (defaults in brackets):
#   MODEL      path to the GGUF file (required)
#   LLAMA_BIN  [/opt/llama.cpp/build/bin/llama-server]
#   ALIAS      [qwen3-coder-30b]
#   CTX        [32768]
#   RUN_USER   [llm]
#   TENSOR_SPLIT  optional, e.g. 50,50 for two equal GPUs
set -euo pipefail

: "${MODEL:?Set MODEL to the path of your GGUF file}"
LLAMA_BIN="${LLAMA_BIN:-/opt/llama.cpp/build/bin/llama-server}"
ALIAS="${ALIAS:-qwen3-coder-30b}"
CTX="${CTX:-32768}"
RUN_USER="${RUN_USER:-llm}"
TENSOR_SPLIT="${TENSOR_SPLIT:-}"
SRC="$(dirname "$0")/llama-server.service.example"
UNIT=/etc/systemd/system/llama-server.service

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi
[ -x "$LLAMA_BIN" ] || { echo "llama-server not found at $LLAMA_BIN" >&2; exit 1; }
[ -r "$MODEL" ] || { echo "Model not found at $MODEL" >&2; exit 1; }
id "$RUN_USER" >/dev/null 2>&1 || { echo "User $RUN_USER does not exist (create it with: useradd -r -m $RUN_USER)" >&2; exit 1; }

# Escape characters that are special in a sed replacement.
esc() { printf '%s' "$1" | sed -e 's/[\/&|]/\\&/g'; }

sed -e "s|@USER@|$(esc "$RUN_USER")|g" \
    -e "s|@LLAMA_BIN@|$(esc "$LLAMA_BIN")|g" \
    -e "s|@MODEL@|$(esc "$MODEL")|g" \
    -e "s|@ALIAS@|$(esc "$ALIAS")|g" \
    -e "s|@CTX@|$(esc "$CTX")|g" \
    "$SRC" > "$UNIT.tmp"

if [ -n "$TENSOR_SPLIT" ]; then
  sed -i "s|^  -ngl 99 \\\\\$|  -ngl 99 --tensor-split $(esc "$TENSOR_SPLIT") \\\\|" "$UNIT.tmp"
fi

if cmp -s "$UNIT.tmp" "$UNIT"; then
  rm -f "$UNIT.tmp"
  echo "Unit unchanged."
  systemctl enable --now llama-server.service >/dev/null 2>&1
else
  mv "$UNIT.tmp" "$UNIT"
  systemctl daemon-reload
  systemctl enable llama-server.service >/dev/null 2>&1
  systemctl restart llama-server.service
  echo "Installed and (re)started llama-server."
fi
echo "Loading a large model can take a minute. Test: curl -s http://localhost:8080/v1/models"
