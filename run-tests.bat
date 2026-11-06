@echo off
REM Runs the headless self-test with live progress. Log: tests\SelfTest\bin\Release\net10.0\selftest.log
REM   run-tests.bat          full run (about 5-10 minutes; the simulator mimics USB latency)
REM   run-tests.bat --quick  skips the 32 MB chip (about 2-3 minutes)
cd /d "%~dp0"
dotnet run --project tests\SelfTest -c Release -- %*
