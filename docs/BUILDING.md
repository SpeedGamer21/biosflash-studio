# Building BiosFlash Studio

Developer notes: building from source, running the self-tests and the project layout.

## Building from source

You need the [.NET 10 SDK](https://dot.net).

```sh
# run from source
dotnet run --project src/BiosFlashStudio.csproj               # add: -- --demo
```

**Windows**

```bat
build.bat              :: framework-dependent EXE        -> dist\
build.bat portable     :: self-contained single EXE      -> dist\portable\
build.bat installer    :: setup.exe (Inno Setup 6)        -> dist\installers\
build.bat linux        :: cross-builds linux-x64/arm64 + .tar.gz
```

**Linux** (Debian/Ubuntu or Fedora)

```sh
./build.sh             # .tar.gz + .deb (dpkg-deb) + .rpm (rpmbuild) for this machine
./build.sh arm64       # cross-build for ARM64
# package binaries already cross-built on Windows, no .NET SDK needed:
./packaging/linux/build-packages.sh x64 --prebuilt
```

On Fedora, install `rpm-build` to get `.rpm` output. On Debian/Ubuntu, `dpkg-deb` is already installed and `rpmbuild` comes with the `rpm` package.

**Useful command-line options:**
- `--demo`: simulator plus demo firmware.
- `--page=N`: open a page directly.
- `--light`: start in the light theme.
- `--stock=<file>`: load a stock image into Repair Tools.
- `--extract=<file>`: open a vendor update in the Update Extractor.
- `--diag`: open Diagnostics and run the driver check.
- A file path opens that file (`.cap` files open in the CAP tool).

## Testing

A headless self-test exercises everything in software. It runs these against simulated chips and a packet-level CH341A emulator:
- the analyzer
- the SPI and I²C drivers
- the raw USB protocol used by the WinUSB and libusb backends
- the capsule parser, the repair engine and the update extractor
- the boot logo engine (UEFI parser, LZMA and EFI/Tiano rebuild, conversions)

```sh
run-tests.bat           # or ./run-tests.sh   — full run with live progress bars (91 tests)
run-tests.bat --quick   # skips the 32 MB chip (~1.5 min)
run-tests.bat --repair  # repair tools, update extractor and boot logo (~3 s)
run-tests.bat --logo    # boot logo engine only (~2 s)
```

Progress is also written to `tests/SelfTest/bin/Release/net10.0/selftest.log`.

> **Note:** all automated tests run against simulated hardware and synthetic firmware. Please report results with real programmers, boards and BIOS updates.

## Project layout

```
src/
  Hardware/    CH341 backends: WCH DLL, shared raw USB protocol, WinUSB + libusb transports, simulator, USB scanner
  Flash/       chip database, SPI NOR driver, I²C EEPROM driver
  Analysis/    firmware analyzer, UEFI volume parser, boot logo engine, capsule parser, repair engine, update extractor, demo image
  Controls/    hex editor, region bar, usage map, pinout drawings
  Ui/          theme palette, stroke icons, dialogs
  Platform/    OS helpers (udev rule install, folders)
  *.axaml      Avalonia UI
tests/SelfTest/  headless self-test
packaging/
  windows/     Inno Setup script
  linux/       .deb/.rpm/.tar.gz builder, install.sh, udev rule, desktop entry, icon
docs/          logo and screenshots
```

