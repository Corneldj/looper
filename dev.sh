#!/usr/bin/env bash
# Starts the Looper API and the Angular dev server together.
set -euo pipefail
cd "$(dirname "$0")"

trap 'kill 0' EXIT

(cd api && dotnet run --project Looper.Api) &
(cd web && npm start) &

wait
