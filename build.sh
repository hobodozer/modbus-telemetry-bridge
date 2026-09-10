#!/usr/bin/env sh
# Build and test the cross-platform half of the bridge. POSIX sh on purpose - Linux does not ship
# PowerShell, so build.ps1 is not a portable entry point even though pwsh can be installed.
#
#   ./build.sh              build + repo-check + smoke test
#   ./build.sh --no-tests   build only
#
# What this does NOT build is src/ModbusBridge.App, the WPF GUI, which cannot exist off Windows.
# Everything else - the engine, the Modbus client and server, the tag bus, the telemetry protocol
# and the headless host - is plain net8.0. Run the bridge with:
#
#   dotnet run --project src/ModbusBridge.Cli -- --data ./rig
#
# vJoy and the PDH host counters are Windows-only. Off Windows they report themselves unavailable
# and the rest of the bridge runs normally.

set -eu

root=$(cd "$(dirname "$0")" && pwd)
cd "$root"

configuration=${CONFIGURATION:-Release}
run_tests=1
for arg in "$@"; do
    case "$arg" in
        --no-tests) run_tests=0 ;;
        --debug) configuration=Debug ;;
        -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    esac
done

step() {
    printf '\n==> %s\n' "$1"
}

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download" >&2
    exit 1
fi

# repo-check is a nicety, not a build dependency - a missing interpreter must not block a build.
if command -v python3 >/dev/null 2>&1; then
    python=python3
elif command -v python >/dev/null 2>&1; then
    python=python
else
    python=
fi

if [ "$run_tests" -eq 1 ] && [ -n "$python" ]; then
    step "Checking repository consistency"
    # Written as an if, not "grep -q FAIL && exit". Under set -e a grep that finds nothing exits 1,
    # and "no FAIL lines" is the success case - so the short form killed the script on a clean run.
    if "$python" tools/repo-check.py --self-test | grep -q FAIL; then
        echo "repo-check self-test failed - run: $python tools/repo-check.py --self-test" >&2
        exit 1
    fi
    "$python" tools/repo-check.py --quiet
    echo "    repo-check passed."
elif [ "$run_tests" -eq 1 ]; then
    step "Python not found - skipping repo-check. The build does not need it."
fi

step "Building ($configuration)"
# The solution filter excludes only the WPF app. Using the full solution here would fail on Linux
# for a reason that has nothing to do with this code.
dotnet build ModbusBridge.CrossPlatform.slnf -c "$configuration" --nologo -v quiet

if [ "$run_tests" -eq 1 ]; then
    step "Running the end-to-end smoke test"
    # Binds loopback ports 15020, 15502 and 15601 briefly. vJoy checks report unavailable off
    # Windows rather than failing.
    dotnet run --project tests/ModbusBridge.SmokeTest/ModbusBridge.SmokeTest.csproj \
        -c "$configuration" --no-build --nologo

    step "Probing the server data store"
    dotnet run --project tools/store-probe/StoreProbe.csproj \
        -c "$configuration" --no-build --nologo -- --quiet
fi

step "Done"
printf '\n    run it with: dotnet run --project src/ModbusBridge.Cli -- --data ./rig\n\n'
