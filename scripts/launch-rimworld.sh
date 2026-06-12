#!/bin/bash
# Launch RimWorld and wait for the GameRL socket to appear.
# Usage: ./launch-rimworld.sh [timeout_seconds]
#
# Exit codes:
#   0 - Socket appeared, game is ready
#   1 - Timeout waiting for socket

set -euo pipefail

SOCKET_PATH="${GAMERL_SOCKET:-/tmp/gamerl-rimworld.sock}"
TIMEOUT="${1:-120}"
RIMWORLD_APP="RimWorldMac.app"

echo "[launch-rimworld] Starting RimWorld..."

# Remove stale socket
if [ -e "$SOCKET_PATH" ]; then
    rm -f "$SOCKET_PATH"
    echo "[launch-rimworld] Removed stale socket at $SOCKET_PATH"
fi

# Launch RimWorld via Steam (macOS)
if [ "$(uname)" = "Darwin" ]; then
    open -a "$RIMWORLD_APP" 2>/dev/null || open steam://rungameid/294100
else
    steam -applaunch 294100 &
fi

echo "[launch-rimworld] Waiting for socket at $SOCKET_PATH (timeout: ${TIMEOUT}s)..."

elapsed=0
while [ ! -e "$SOCKET_PATH" ]; do
    if [ "$elapsed" -ge "$TIMEOUT" ]; then
        echo "[launch-rimworld] ERROR: Timeout after ${TIMEOUT}s waiting for socket"
        exit 1
    fi
    sleep 2
    elapsed=$((elapsed + 2))
    if [ $((elapsed % 10)) -eq 0 ]; then
        echo "[launch-rimworld] Still waiting... (${elapsed}s elapsed)"
    fi
done

echo "[launch-rimworld] Socket detected! RimWorld is ready for game-rl connection."
exit 0
