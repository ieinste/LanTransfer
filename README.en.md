# 轻传 LanTransfer

[简体中文](README.md) | English

A lightweight file transfer tool for Windows computers on the same local network or connected directly by an Ethernet cable. Run `LanTransfer.exe` on both computers. No internet connection, account, server, browser, or external relay is required.

The application interface is currently in Simplified Chinese. This document provides English instructions, with the Chinese button labels included where useful.

Download the Windows package from the [latest release](https://github.com/ieinste/qingchuan-lan-transfer/releases/latest).

## Requirements and running the app

- Target systems: Windows 7 SP1 / 8.1 / 10 / 11, x86 and x64.
- Requires **.NET Framework 4.x**, rather than .NET 9. If it is missing on Windows 7, install the Windows 7 version of .NET Framework 4.8. Windows 10/11 usually already include the runtime.
- Keep `LanTransfer.exe` and `LanTransfer.exe.config` in the same directory, and copy them to both computers. For a local build, these files are generated in `release/` by default. No application installation is needed.

## Usage

1. On the receiving computer, choose a destination directory and click **Start receiving (开始接收)**. Give the sender your local IPv4 address and receive code.
2. On the sending computer, add files or folders, or drag them into the queue. Enter the receiver's IPv4 address, or click **Search devices (搜索设备)**. Enter the receiver's receive code and click **Start sending (开始发送)**.
3. If a firewall prompt appears on first use, allow access on private networks. Transfers prefer TCP port **45871**. If that port is occupied or the system prevents it from being bound, the receiver automatically selects an available port and updates its interface. Device discovery retrieves the actual port; when connecting manually, enter the port shown by the receiver. Discovery uses UDP ports **37951–37958** and automatically skips occupied discovery ports. You do not need to disable the firewall.
4. After an interruption, keep the destination directory and its `.lantransfer-cache` subdirectory. Send **the same original batch of files** again to the same destination to resume. Completed files are verified and skipped. A modified source file is treated as a new file.
5. A file with the same name but different contents is reported as a conflict and skipped; other files continue to transfer. Choose a different destination directory or rename the existing file first.
6. **Pause (暂停)** closes the sending connection. **Stop receiving (停止接收)** also interrupts an active incoming transfer, while retaining incomplete data. For transfers in both directions, enable receiving on each computer separately.

## Direct Ethernet connection

After connecting the two computers, ensure their IPv4 addresses are on the same subnet. For example, set their Ethernet adapters to `192.168.50.1` and `192.168.50.2`, with subnet mask `255.255.255.0`. The gateway and DNS fields for these directly connected adapters may be left blank. Choose a subnet that does not conflict with your existing networks. LanTransfer does not change network settings automatically. Send to the address of the other computer's **Ethernet adapter**.

## Implementation and limitations

**v0.2.1 — Consistent styling:** Sending and receiving share a purple accent, rounded buttons and cards, and equally sized input containers for text boxes, dropdowns, and port fields. The progress bar uses the same accent, and files and folders use matching outline icons.

**v0.2.0 — Interface improvements:** Sending and receiving use a two-column card layout. The queue provides access to full file paths. Local IPv4 addresses can be selected from a dropdown, and the address and actual receiving port can be copied together. Sending and receiving have separate status displays, with a shared activity log. Fixes include discovered ports not being applied correctly, conflicting search/send button states, and stale progress updates replacing the stopped receiving status. Smaller windows retain the two-column layout.

**v0.1.1 — Port conflict fixes:** The receiver selects an available port when the requested port is unavailable, and the interface displays the actual port. Discovery supports multiple candidate UDP ports. The file transfer protocol remains compatible with older versions, but using the newer version on both computers is recommended so devices can still be discovered when an alternative discovery port is used.

- Native WinForms interface and a single AnyCPU executable. No Electron, WebView, or third-party runtime is bundled.
- Files are transferred sequentially over one connection, using a 1 MiB streaming buffer. Directories are scanned incrementally; the contents of the entire batch are not loaded into memory.
- The sender calculates each source file's SHA-256 hash before sending. The receiver verifies an existing partial prefix before resuming, verifies the completed file again, and only then moves it to its final name. File modification times, directory structure, and empty directories are preserved.
- Integrity takes priority: the first send reads the entire source file for hashing, and the receiver reads the completed file again for verification. Resuming also scans the received prefix. Hashing depends on disk speed and is not displayed as network throughput.
- Receive codes are verified with a random challenge and HMAC-SHA256. **File contents are not encrypted.** The app is intended for trusted local networks and direct connections, with no internet traversal or public port exposure feature. While receiving is enabled, any device holding the receive code can write directly into the selected destination directory.
- Each receiver processes one sending connection at a time by default. Throughput for large numbers of small files is affected by disk operations and per-file verification. There is currently no small-file packing, parallel multi-connection transfer, or performance guarantee.
- Temporary receiving data is stored in `.lantransfer-cache`. Deleting this directory discards resume progress for incomplete files. Do not modify the destination directory or temporary files while receiving.
- Symbolic links and directory junctions are not transferred. ACLs, alternate data streams, hard-link relationships, sparse-file attributes, and similar metadata are not copied. Traditional Windows 7/.NET Framework path-length limits still apply; shorten source or destination paths when necessary. An unreadable source file stops the current send; resolve the problem and retry.
- For files larger than 4 GiB, use a destination file system that supports that size, such as NTFS or exFAT. FAT32 does not support them.
- Progress and remaining time refer to the current file, rather than a precise estimate for the entire batch. File sizes use 64-bit integers.

## Building and testing

No NuGet packages are required for the application build. The build computer needs the .NET Framework 4.x compiler and .NET Framework 4.0 reference assemblies. These reference assemblies are development requirements only; users do not need them to run the app. The build uses `/nostdlib+` with the 4.0 reference assemblies to prevent accidental use of newer framework APIs.

Run the following in Windows PowerShell:

```powershell
# Build the application
powershell -ExecutionPolicy Bypass -File .\build.ps1
# Build and run transfer tests and UI checks
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test
# Build and run only the UI rendering checks
powershell -ExecutionPolicy Bypass -File .\build.ps1 -UiTest
# Optional: transfer a real file larger than 4 GiB; requires about 9 GiB of free space
powershell -ExecutionPolicy Bypass -File .\build.ps1 -LargeTest
# Build into a separate directory to avoid replacing a running application
powershell -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory release\v0.2.1
```

If the build computer lacks the 4.0 reference assemblies, run `powershell -ExecutionPolicy Bypass -File .\prepare-references.ps1` with PowerShell 5 or later. This downloads Microsoft's `Microsoft.NETFramework.ReferenceAssemblies.net40` version 1.0.3 from NuGet for building only. It adds no application runtime dependency.

The automated test suite covers batch transfers, Chinese paths, empty directories and files, skipping completed files, conflict protection, incorrect receive codes, interrupted-transfer recovery, retransmission after a corrupted prefix, path traversal protection, and protocol length limits. `-Test` also runs the UI checks; `-UiTest` runs the UI rendering checks separately. Actual throughput, cross-computer firewall behavior, and direct Ethernet connections must be verified on the target devices and networks.

## Source layout

```text
src/                    Windows desktop interface and TCP transfer core
tests/                  Transfer behavior tests and UI rendering checks
build.ps1               Local build and test entry point
prepare-references.ps1  Downloads Microsoft's build-time reference assemblies
```

The repository contains source code and documentation. Build outputs, test data, caches, and third-party runtime installers are not committed. The first build may require an internet connection to run `prepare-references.ps1` if reference assemblies are missing. Once those assemblies are available, the application can be built offline.

## Cross-platform support

Only the Windows client has been implemented. macOS, Linux, Android, and iOS could be added through a new cross-platform client, but the current WinForms application does not run on those platforms. The plan is to retain the Windows 7-compatible version while evaluating a modern .NET and Avalonia client that reuses the transfer protocol. No clients or validation results for the other platforms are currently available. See the [cross-platform support assessment (Chinese)](docs/PLATFORM-SUPPORT.md).

## Official runtime resources

- [Microsoft .NET Framework 4.8 download page](https://dotnet.microsoft.com/download/dotnet-framework/net48)
- [Microsoft .NET Framework system requirements](https://learn.microsoft.com/dotnet/framework/get-started/system-requirements)
