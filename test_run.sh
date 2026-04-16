#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Kill any running instances
killall -9 Urlaubstool.App 2>/dev/null

# Run with output capture
echo "Starting Urlaubstool..."
(
  sleep 15 && killall -9 Urlaubstool.App 2>/dev/null
) &

dotnet run --project Urlaubstool.App/Urlaubstool.App.csproj --configuration Debug 2>&1 | tee /tmp/urlaubstool_output.log

echo "Done. Output saved to /tmp/urlaubstool_output.log"
cat /tmp/urlaubstool_output.log
