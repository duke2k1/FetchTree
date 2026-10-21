# FetchTree

FetchTree will bring an Explorer-style interface to HTTP/HTTPS directory listings: a folder tree, file selection, and concurrent downloads that preserve folder structure.

**Current milestone: working scanner prototype (v0.1).** This version is a Windows console application, not yet the graphical file browser or downloader. Its scan model is separate from the console so a future desktop UI can reuse it.

## Run the Windows executable

Requires Windows 10/11 with .NET Framework 4.8 enabled. No Python, Node, or third-party packages are needed. The prototype is unsigned.

Double-click `FetchTree.exe` for guided URL and depth prompts. It saves a timestamped JSON report in your Documents folder and waits for Enter before closing.

Or run from PowerShell:

```powershell
.\FetchTree.exe https://firmware.ardupilot.org/Plane/ --depth 0 --json scan.json
.\FetchTree.exe https://firmware.ardupilot.org/Plane/stable/ --depth 1 --json stable.json
.\FetchTree.exe --help
```

Use the site's exact URL casing (`Plane`, not `plane`). Depth **0** scans the root listing and displays its immediate files and folders; depth **1** also scans those folders, and so on (maximum 100). Folders beyond the requested depth remain visible with `DepthLimit` status.

Ctrl+C cancels requests and exports partial results when `--json` is specified. Default budgets: 1,000 listing requests, 100,000 discovered entries, 4 MB per page, 20 seconds per request. Override the first two with `--max-pages N` and `--max-entries N`. Scanning is sequential to limit load on hosting sites; concurrent file downloads are a later milestone.

Exit codes: `0` finished within the requested depth, `1` invalid input or fatal error, `2` partial scan (errors or resource limits), `130` cancelled. Always inspect folder statuses; an unread folder is not an empty folder.

## Supported listings and boundaries

- Apache/nginx-style anchor links with trailing slashes for directories.
- ArduPilot's table listings with folder icons, including directory links without trailing slashes.
- Relative and absolute links, encoded names, and HTML entities.
- Only direct children on the same scheme and host inside the selected root are traversed. Parent links, sort queries, fragments, external links, duplicate URLs, encoded separators, and control characters are excluded.
- Redirects may only normalize the trailing slash of the current directory. Redirects to another folder, host, or scheme are reported as errors. Enter the final HTTPS URL if a site redirects HTTP to HTTPS.
- HTML only; no JavaScript-rendered pages, authenticated sessions, arbitrary web crawling, or universal directory format detection. A generic HTML page may yield no entries. File size/date metadata is not inferred in this prototype.
- JSON contains URLs, display names, relative paths, status/error fields, counters, and child entries. **Relative paths are discovery metadata, not validated Windows download paths.** The future downloader must handle reserved names, case collisions, long paths, and atomic writes before using them locally.

## Build and test

From PowerShell at the repository root:

```powershell
.\build.ps1
.\test.ps1
.\bin\FetchTree.exe --help
```

Build uses the .NET Framework compiler supplied with Windows. No package restore is required. Tests cover URL boundaries, parsing, depth, partial failures, redirects, budgets, cancellation, and a real loopback HTTP server. An optional captured live listing can be checked using `test.ps1 -LiveHtml C:\path\listing.html`.

GitHub Actions builds and tests on Windows, then uploads `FetchTree-windows` containing the executable and this README. Download it from the successful workflow run's Artifacts section.

## Roadmap

1. **Scanner prototype:** implemented, CLI and reusable model.
2. **Desktop GUI:** URL bar, depth control, folder tree left, file list right, loading/error states, scan cancellation.
3. **Download manager:** select files/folders, preserve structure, bounded concurrent transfers, queue progress, cancellation/retry, collision handling, and partial-file cleanup.
4. **Windows integration:** drag remote files into Explorer using virtual file data objects; installer and signing.

See `VALIDATION.md` for the initial build's verification results and limitations.
