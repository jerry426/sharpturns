#!/bin/sh
# Builds and runs the SharpTurns Instance Manager from source. Extra arguments
# go to `dotnet run`, for example `./run-instance-manager.sh -c Release`.
set -e
cd "$(dirname "$0")"
exec dotnet run --project src/SharpTurns.InstanceManager.App "$@"
