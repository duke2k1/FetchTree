# Desktop v0.2 verification

- Both Windows executables rebuilt successfully.
- 17 scanner checks passed, including actual loopback HTTP transport.
- 24 desktop/transfer checks passed: Windows names and collisions, recursive folder resolution, empty directories, no file downloads during descriptor preparation, bounded concurrency, byte-accurate indexed streams, no overwrite, failure cleanup, and cancellation.
- Actual Windows Shell IDropTarget integration passed: copied two files and a folder together, preserving nested content and an empty subfolder, into an isolated test folder.
- The application's own rendering was inspected; a narrow folder pane was corrected.
- Physical mouse dragging to the desktop was not completed during automated testing: desktop capture timed out and the user stopped computer-use testing. Native Shell drop behavior is covered by the integration test above.
- Local Windows Schannel HTTPS is restricted in the Codex sandbox. Certificate validation remains enabled; Windows CI separately checks a live ArduPilot HTTPS listing.

Known limits: no automatic retry/resume, file redirects rejected, Windows-incompatible or overly long paths fail explicitly. External drop targets control final writes and may leave partial destinations after cancellation. Temporary staging may survive a crash or an external consumer holding a stream open.
