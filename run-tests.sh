#!/bin/sh
# Runs the headless self-test. Progress is shown live and logged to tests/SelfTest/bin/Release/net10.0/selftest.log
#   ./run-tests.sh          full run      ./run-tests.sh --quick   skips the 32 MB chip
cd "$(dirname "$0")"
exec dotnet run --project tests/SelfTest -c Release -- "$@"
