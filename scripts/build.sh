#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# Full local Nota build: native engine (universal) + managed.
# Usage: scripts/build.sh

set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="/opt/homebrew/bin:$PATH"

echo "==> Building native engine (universal arm64+x86_64)"
cmake -G Ninja -S src/native/knox.engine -B src/native/knox.engine/build \
  -DCMAKE_BUILD_TYPE=Release
cmake --build src/native/knox.engine/build
lipo -info src/native/knox.engine/build/libknox_engine.dylib

echo "==> Building managed (.NET)"
dotnet build Knox.sln -c Release --nologo

echo "==> Smoke test"
dotnet run --project tests/Knox.SmokeTest -c Release --nologo

echo "==> Done. Run the app with: dotnet run --project src/managed/Knox.App"
