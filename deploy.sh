#!/bin/bash
set -e
# Build directly to the target bin folder as configured in csproj
dotnet build Meridian59.TuiClient/Meridian59.TuiClient.csproj --configuration Debug --arch x64 --no-incremental -v quiet

# Ensure resources are available in bin/
if [ ! -d "bin/Resources" ]; then
    ln -s ../Resources bin/Resources
fi

# Copy test scripts
cp *.script bin/ 2>/dev/null || true

echo "Build complete. Output is in bin/"
