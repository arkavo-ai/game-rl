#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="${ROOT_DIR}/Plugins/macOS"

mkdir -p "${OUT_DIR}"

clang++ -std=c++17 -fobjc-arc -dynamiclib \
  "${ROOT_DIR}/Native/macos/gamerl_vision.mm" \
  -o "${OUT_DIR}/libgamerl_vision.dylib" \
  -framework Foundation \
  -framework Metal \
  -framework IOSurface \
  -framework CoreVideo

echo "Built ${OUT_DIR}/libgamerl_vision.dylib"
