#!/usr/bin/env bash
# Check that a host serves what LLM Host Monitor expects.
# Usage: ./check.sh [HOST]   (default: localhost)
set -euo pipefail

HOST="${1:-localhost}"
fail=0

echo "== llama-server: http://$HOST:8080/v1/models"
if models="$(curl -fsS --max-time 5 "http://$HOST:8080/v1/models")"; then
  id="$(printf '%s' "$models" | grep -o '"id":"[^"]*"' | head -n1 | cut -d'"' -f4 || true)"
  ctx="$(printf '%s' "$models" | grep -o '"n_ctx":[0-9]*' | head -n1 | cut -d: -f2 || true)"
  echo "   model id: ${id:-MISSING}"
  echo "   n_ctx:    ${ctx:-MISSING}"
  [ -n "$id" ] || fail=1
else
  echo "   FAILED: no response"
  fail=1
fi

echo "== GPU exporter: http://$HOST:9835/metrics"
if metrics="$(curl -fsS --max-time 5 "http://$HOST:9835/metrics")"; then
  gpus="$(printf '%s\n' "$metrics" | grep -c '^nvidia_smi_memory_total_bytes{.*uuid=' || true)"
  echo "   GPUs:     $gpus"
  for m in memory_used_bytes memory_total_bytes utilization_gpu_ratio temperature_gpu power_draw_watts; do
    if printf '%s\n' "$metrics" | grep -q "^nvidia_smi_${m}{"; then
      echo "   ok        nvidia_smi_${m}"
    else
      echo "   missing   nvidia_smi_${m}"
    fi
  done
  [ "$gpus" -gt 0 ] || fail=1
else
  echo "   FAILED: no response"
  fail=1
fi

if [ "$fail" -eq 0 ]; then echo "OK"; else echo "PROBLEMS FOUND"; fi
exit "$fail"
