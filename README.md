# FetchTree v0.2

An Explorer-style Windows desktop app for HTTP/HTTPS directory listings.

## Run

Extract the package and double-click **FetchTree.exe**. Requires Windows 10/11 and .NET Framework 4.8. No installation or extra packages are needed; the executable is unsigned.

1. Enter a directory URL such as `https://firmware.ardupilot.org/Plane/` and click **Scan**.
2. Double-click folders or use the folder tree. Back, Up, Refresh, and Icon/Details views provide familiar navigation.
3. Select files and folders together using **Ctrl-click**, **Shift-click**, or **Ctrl+A**.
4. **Drag the selection to your desktop or a File Explorer folder.** Actual contents are copied, preserving nested and empty folders.
5. Alternatively use **Download to…**, which creates a new dated FetchTree folder inside the chosen destination to avoid overwrites.

The queue shows file status and downloaded bytes. Choose 1–8 simultaneous downloads (default 3). Cancel stops scanning or transfers. Keep FetchTree open until the transfer finishes.

## Folder transfers

Depth 0 reads the root listing; opening a folder loads it on demand. Higher depths preload more levels. Transferring a folder always resolves its full subtree independently of browsing depth. An incomplete scan fails rather than silently omitting files.

Folders are resolved when a drop target requests descriptors. Large selections may take time to prepare. File downloads begin when the target requests contents, not when items are selected. A target may request contents before mouse release; Explorer normally does so at the drop. The browser remains responsive during the transfer.

Limits: 2,000 listing requests per selected folder, depth 100, and 10,000 total transfer entries. Browsing allows 1,000 pages and 10,000 entries per scan. Listing requests have a 20-second timeout and a 4 MB response limit. Downloads have a 60-second inactivity timeout.

Windows-incompatible names, case collisions, and relative transfer paths over 220 characters are rejected. Explorer handles destination conflicts during drops. Downloads are staged under `%TEMP%\FetchTree` and cleaned after transfers; crashes or external consumers holding streams open may leave cached files. Files are never executed.

## Supported sites and limitations

Supports Apache/nginx-style directory links and ArduPilot folder-icon tables, including folder links without trailing slashes. Use exact URL casing (`Plane`, not `plane`). Parent, external, duplicate, query, and fragment links are excluded. Listing redirects may only normalize a trailing slash; file redirects are rejected. Enter the final directory URL.

JavaScript-generated listings, authentication, automatic retries, resume, signing, and an installer are not included. Inspect the destination after a cancelled or failed drop: Explorer may have copied only part of a selection. The queue reports downloaded bytes; Explorer controls the final destination copy.

## Scanner CLI

The original scanner is included as `FetchTree.Scanner.exe`:

```powershell
.\FetchTree.Scanner.exe https://firmware.ardupilot.org/Plane/ --depth 0 --json scan.json
.\FetchTree.Scanner.exe --help
```

## Build and test

```powershell
.\build.ps1
.\test.ps1
```

Uses the Windows .NET Framework compiler without package restore. Tests cover discovery, path safety, recursive transfers, parallel downloads, cancellation, virtual-file descriptors and streams, and an actual Windows Shell drop target copying a mixed selection to an isolated test folder. GitHub Actions also checks a live HTTPS listing and uploads both executables as `FetchTree-windows`.

Sources: `Scanner.cs` handles discovery, `Transfers.cs` prepares and downloads selections, `ShellDrag.cs` implements Windows virtual files, and `ExplorerWindow.cs` provides the desktop UI. See `VALIDATION.md` for verification scope.
