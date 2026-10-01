#!/bin/sh
# Builds and runs SharpTurns from source. Extra arguments go to `dotnet run`,
# for example `./run.sh -c Release`.
set -e
cd "$(dirname "$0")"
exec dotnet run --project src/SharpTurns.App "$@"
