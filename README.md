# ReClass.NET
This is a port of ReClass to the .NET platform with lots of additional features.

![](https://abload.de/img/main4hsbj.jpg)

## Features
- Support for x86 / x64
- File import from ReClass 2007-2016 and ReClass QT
- Memory Nodes
  - Arrays and Pointers to every other node types
  - Hex 8 / 16 / 32 / 64
  - Int 8 / 16 / 32 / 64
  - UInt 8 / 16 / 32 / 64
  - Bool
  - Bits ![](https://abload.de/img/bitsnhlql.jpg)
  - Enumerations
  - Float / Double
  - Vector 2 / 3 / 4
  - Matrix 3x3 / 3x4 / 4x4
  - UTF8 / UTF16 / UTF32 Text and pointer to text
  - Virtual Tables
  - Function
  - Function Pointer
  - Unions
- Automatic Node Dissection
- Highlight changed memory
- Pointer Preview
- Copy / Paste Support across ReClass.NET instances
- Display types from Debug Symbols (*.pdb)
- Display Runtime Type Informations (RTTI)
- Control the remote process: start / stop / kill
- Process Selection Dialog with filtering
- Memory Viewer
- Memory Scanner
  - Import files from Cheat Engine and CrySearch
  - Scan for values correlated to your input
- Class address calculator
- Code Generator (C++ / C#)
- Module / Section Dumper
- Linux Support (tested on Ubuntu 18.04)
- Debugger with "Find out what writes/accesses this address" support
- Plugin Support
  - Plugins can be written in different languages (example: C++, C++/CLI, C#)
  - Plugins can provide custom methods to access an other process (example: use a driver)
  - Plugins can interact with the ReClass.NET windows
  - Plugins can provide node infos which will be displayed (example: class informations for Frostbite games)
  - Plugins can implement custom nodes with load/save and code generation support

## Plugins
- [Sample Plugins](https://github.com/ReClassNET/ReClass.NET-SamplePlugin)
- [Frostbite Plugin](https://github.com/ReClassNET/ReClass.NET-FrostbitePlugin)
- [MemoryPipe Plugin](https://github.com/ReClassNET/ReClass.NET-MemoryPipePlugin)
- [LoadBinary Plugin](https://github.com/ReClassNET/ReClass.NET-LoadBinaryPlugin)
- [Handle Abuser Plugin](https://github.com/ReClassNET/ReClass.NET-HandleAbuser)
- Unreal Plugin (not available anymore) (by [DrP3pp3r](https://github.com/DrP3pp3r))
- [DriverReader](https://github.com/niemand-sec/ReClass.NET-DriverReader) (by [Niemand](https://github.com/niemand-sec))

To install a plugin just copy it in the "Plugins" folder.
If you want to develop your own plugin just learn from the code of the [Sample Plugins](https://github.com/ReClassNET/ReClass.NET-SamplePlugin) and [Frostbite Plugin](https://github.com/ReClassNET/ReClass.NET-FrostbitePlugin) repositories. If you have developed a nice plugin, leave me a message and I will add it to the list above.

## Installation
Just download the [latest version](https://github.com/ReClassNET/ReClass.NET/releases) and start the x86 / x64 version or let the launcher decide.

## Tips
- Lots of elements have a context menu. Just right-click it and see what you can do there.
- The node window can be controlled with the keyboard too. Arrow keys can select other keys, combined with the shift key the nodes get selected. The menu key opens the context menu which itself can be controlled with the keyboard.
- The memory address field of a class can contain a real formula not just a fixed address.  
  
  **\<Program.exe> + 0x123** will use the base address of Program.exe and add 0x123 to it.  
  **[0x4012ABDE]** will read the integer (4 byte on x86 / 8 byte on x64) from the address 0x4012ABDE and use this value as class address.  
  **[\<Program.exe> + 0xDE] - AB** will use the base address of Program.exe, add 0xDE to it, read the value from this address and finally sub 0xAB from it.  
  **[\<Program.exe> + offset + [\<Program.exe> + offset2]]** Nested operations are supported too.  
  
  Valid operations are read ([..]), add (+), sub (-), mul (*) and div (/). Please note that all operations are integer calculations.

## Compiling
If you want to compile ReClass.NET just fork the repository and open the ReClass.NET.sln file with Visual Studio 2019.
Compile the project and copy the dependencies to the output folder.

To compile the linux native core library, you need WSL [installed and configured](https://learn.microsoft.com/en-us/cpp/build/walkthrough-build-debug-wsl2). If you do not need linux support, simply unload the project in the Solution Explorer. If you want to build cross-platform (x86/x64) you have to install `g++-multilib` too.

If you use the `Makefile` with `docker` or `podman` you have to build the needed image `gcc_multilib` from the following `Dockerfile` (`docker build -t gcc_multi .`):

```
FROM ubuntu:latest

RUN apt-get update \
 && apt-get install --assume-yes --no-install-recommends --quiet \
        make \
        g++ \
        g++-multilib \
 && apt-get clean all
```

## macOS (experimental)

ReClass.NET runs on Apple Silicon macOS under Mono with the X11 WinForms backend.

**Prerequisites**

```
brew install mono mono-libgdiplus autoconf automake libtool pkg-config glib libpng jpeg giflib libtiff libexif pango
brew install --cask xquartz
```

Then log out and back in, and start XQuartz once. (Homebrew's libgdiplus is used only at build time; the X11-capable libgdiplus from `scripts/build-libgdiplus-macos.sh` is used at runtime.)

**Build**

The build requires two native pieces in addition to the C# binaries:

```
make macos                                # builds launcher, app, NativeCore.dylib into build/Release/x64
scripts/build-libgdiplus-macos.sh         # builds libgdiplus from source with cairo-xlib support
scripts/build-xim-shim-macos.sh           # builds XIM interpose shim
```

(Do not run `make macos_update`; it is known not to work.)

**Run**

```
./run-macos.sh
```

The script runs Mono as root via `sudo` because `task_for_pid` requires it. It will fail loudly if the libgdiplus or XIM shim builds are missing.

**Known Issues**

- Non-fatal X11 `BadFont` and `BadCursor` errors print to stderr.

**Known issues / security**

- `run-macos.sh` loads `NativeCore.dylib`, the XIM shim and `libgdiplus` from the repository checkout as root (via `sudo`). Keep the checkout owned by you and not group- or world-writable; the script refuses to run otherwise.

**Limitations**

- Reading other processes requires root. Even as root, macOS System Integrity Protection prevents attaching to Apple-signed / hardened-runtime processes; third-party apps and games work.
- The debugger ("Find out what accesses/writes this address") is not available.
- Disassembly uses distorm (x86 only). It is correct for x86_64 processes running under Rosetta and meaningless for native arm64 processes.
- Debug symbols (PDB) and file-type registration are Windows-only.
- The UI is drawn through XQuartz and does not look native.
- Attach-to-process was not end-to-end verified in this port yet (experimental).

## Videos

[Youtube Playlist](https://www.youtube.com/playlist?list=PLO246BmtoITanq3ygMCL8_w0eov4D8hjk)

## Screenshots
Process Selection  
![](https://abload.de/img/processgya2k.jpg)

Memory Viewer  
![](https://abload.de/img/memoryviewerb4y1s.jpg)

Memory Scanner  
![](https://abload.de/img/scannerytub1.jpg)

Pointer Preview  
![](https://abload.de/img/memorypreview2gsfp.jpg)

Code Generator  
![](https://abload.de/img/codegeneratorqdat2.jpg)
![](https://abload.de/img/codegenerator24qzce.jpg)

Plugins  
![](https://abload.de/img/plugin1mda4r.jpg)
![](https://abload.de/img/plugin25dxk1.jpg)

Settings  
![](https://abload.de/img/settings8sz4b.jpg)

## Authors / Special Thanks
- [KN4CK3R](https://github.com/KN4CK3R)
- DrUnKeN ChEeTaH
- P47R!CK
- DogMatt
- [ajkhoury](https://github.com/ajkhoury)
- [IChooseYou](https://github.com/IChooseYou)
- [stevemk14ebr](https://github.com/stevemk14ebr)
- [Timboy67678](https://github.com/Timboy67678)
- [DarthTon](https://github.com/DarthTon)
- [ReUnioN](https://github.com/ReUnioN)
- leveln
- [buddyfavors](https://github.com/buddyfavors)
- [DrP3pp3r](https://github.com/DrP3pp3r)
- [ko1N](https://github.com/ko1N)
- [Niemand](https://github.com/niemand-sec) (see his talk at [BlackHat Europe 2019 (London) "Unveiling the underground world of Anti-Cheats"](https://www.blackhat.com/eu-19/briefings/schedule/index.html#unveiling-the-underground-world-of-anti-cheats-17358))