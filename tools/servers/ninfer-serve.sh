#!/usr/bin/env bash
# Serve Qwen3.8-27B NVFP4 with NInfer (https://github.com/Neroued/ninfer) on :8080.
# Run inside WSL2 (Ubuntu) on the RTX 5090. Build tree: ~/ninfer, model: ~/ninfer/models.
# OpenAI/Anthropic-compatible; served model name is "qwen3.8-27b". Auth is disabled.
#
# Needs CUDA 13.x on PATH (built with 13.4). The 256k config leaves ~236 MiB of GPU memory
# free after startup; CTX=32768 leaves ~7 GiB if you need headroom for other GPU work.
#   CTX=32768 tools/servers/ninfer-serve.sh > ~/serve.log 2>&1
set -euo pipefail
export PATH="/usr/local/cuda-13.4/bin:$PATH"
cd "${NINFER_DIR:-$HOME/ninfer}"
CTX="${CTX:-262144}"
exec ./build/apps/ninfer-serve models/qwen3_8_27b_nvfp4.ninfer \
  --max-context "$CTX" --kv-capacity "$CTX" --max-concurrency 2 \
  --kv-dtype fp8 --device-state-slots 2 \
  --spec mtp --draft-tokens 3 --lm-head-draft --preserve-thinking "$@"
