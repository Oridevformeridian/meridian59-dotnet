#!/bin/bash
set -e
# Build directly to the target bin folder as configured in csproj
dotnet build Meridian59.TuiClient/Meridian59.TuiClient.csproj --configuration Debug --arch x64 --no-incremental -v quiet
echo "Build complete. Output is in bin/"
