# Building

[日本語](BUILD.md)

> **This is an experimental application** and has not been sufficiently verified. Read the [README](../README_en.md#this-is-an-experimental-application) first.

How to build BeepDump from source. To just use it on Windows, no build is needed ([INSTALL_en.md](INSTALL_en.md)). The Linux and macOS versions need building.

## 1. Install the .NET 10 SDK

| OS | Command or method |
|---|---|
| Windows | `winget install Microsoft.DotNet.SDK.10` |
| Ubuntu 24.04, 26.04 | `sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0` |
| Fedora | `sudo dnf install dotnet-sdk-10.0` |
| Debian 13 | See below |
| macOS | On the [download page](https://dotnet.microsoft.com/download/dotnet), run the .NET 10 SDK installer. **Arm64** for Apple Silicon, **x64** for Intel |

On Debian 13, add Microsoft's package source first.

```sh
wget https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

For other distributions, see [Microsoft's instructions](https://learn.microsoft.com/dotnet/core/install/linux).

**Open a new terminal** and confirm. A number starting with 10 means it worked.

```sh
dotnet --version
```

## 2. Get the source

```sh
git clone https://github.com/litchi-gh/pc-e500_beep_romdumper.git
cd pc-e500_beep_romdumper/BeepDump
```

Without Git, use Code → Download ZIP on the GitHub page and extract it.

## 3. Build

This needs an internet connection.

### Windows

```powershell
.\build.cmd            # run the tests, then build the Windows application and command-line version
.\build.cmd -Cli       # also build the command-line version for Linux and macOS
.\build.cmd -TestOnly  # tests only
```

The tests take about 10 minutes. `BeepDump.exe` cannot be updated while it is open, so close it first.

### Linux and macOS

`build.cmd` is for Windows, so run `dotnet publish` directly. The windowed application can only be built on Windows.

```sh
dotnet publish src/BeepDump.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None \
  -o app/cli/linux-x64
```

Replace `linux-x64` in both places to match the machine.

| Machine | Use |
|---|---|
| Linux (x64) | `linux-x64` |
| Linux (Arm64) | `linux-arm64` |
| macOS (Apple Silicon) | `osx-arm64` |
| macOS (Intel) | `osx-x64` |

> What has been tested is building with `.\build.cmd -Cli` on Windows, and running the resulting `linux-x64` version on Ubuntu under WSL.

You can also run it directly, without publishing:

```sh
dotnet run --project src/BeepDump.Cli -c Release -- decode recording.wav
```

### The files it makes

They go under `BeepDump/app/`. Each runs on its own, with no .NET installation.

| File | Content | Size |
|---|---|---|
| `BeepDump.exe` | Windowed application (Windows) | About 50MB |
| `cli/win-x64/beepdump.exe` | Command-line version (Windows) | About 36MB |
| `cli/linux-x64/beepdump` | Command-line version (Linux x64) | About 38MB |
| `cli/linux-arm64/beepdump` | Command-line version (Linux Arm64) | About 36MB |
| `cli/osx-x64/beepdump` | Command-line version (macOS Intel) | About 37MB |
| `cli/osx-arm64/beepdump` | Command-line version (macOS Apple Silicon) | About 35MB |

See [INSTALL_en.md](INSTALL_en.md) for where to put them.

## Running only the tests

```sh
dotnet run --project tests/BeepDump.Tests -c Release
dotnet run --project tests/BeepDump.Tests -c Release -- "fast:"   # filter by part of a test's name
```

`All tests passed.` means they all passed.

## Making a Windows release

[INSTALL_en.md](INSTALL_en.md) assumes these two files under Assets in Releases:

| Asset name | Content |
|---|---|
| `BeepDump.exe` | `app\BeepDump.exe` |
| `beepdump-win-x64.zip` | `app\cli\win-x64\beepdump.exe`, zipped |

Windows treats `BeepDump.exe` and `beepdump.exe` as the same name, so the command-line version goes in a zip.

```powershell
.\build.cmd
Compress-Archive -Path app\cli\win-x64\beepdump.exe -DestinationPath beepdump-win-x64.zip -Force
```

## Rebuilding the machine code, listings, and figures

The application build does not need these. They are only for making changes. Run the commands at the top of the repository.

`fast\build.py` needs `e500asm`, an SC62015 assembler, and `ce140conv`, which converts text to a CE-140F BAS file. Put them on your `PATH`, or give their locations in the environment variables `E500ASM` and `CE140CONV`. The generated files are in the repository, so you do not need these unless you change the machine code or listings.

| What it makes | Command | Needs |
|---|---|---|
| Machine code, listings, listing images, and the listings in the READMEs | `py fast\build.py` | Python, Pillow (`py -m pip install pillow`), `e500asm.exe`, `ce140conv.exe`, and the Windows fonts Consolas and Meiryo |
| Figures | `py docs\figures\build_figures.py` | Python |
| Figure data | `py docs\figures\extract_data.py WORKDIR ROMDIR` | Python, NumPy, SciPy, real recordings, reference ROMs, and their analysis output (none in the repository). `data.json` is normally used as it is |

## Troubleshooting

| Symptom | What to check |
|---|---|
| `dotnet` is not found | Open a new terminal after installing the SDK |
| `dotnet --version` does not start with 10 | Install the .NET 10 SDK. It can coexist with other versions |
| `Access to the path ... is denied` or `publish failed` | `BeepDump.exe` is still open. Close it and build again |
| PowerShell refuses to run the script | Run `build.cmd` rather than `build.ps1` |
| Package downloads fail | Check your internet connection. Builds for other OSes download their runtime |
