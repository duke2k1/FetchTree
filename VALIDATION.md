# Initial prototype verification

- Built a Windows managed PE executable with the .NET Framework compiler.
- Automated tests: URL validation, tree boundaries, encoded separators and control characters, HTML entities, duplicate removal, directory icon recognition, depth 0/1, partial HTTP failures, off-site redirects, scan limits, cancellation, and actual loopback HTTP transport.
- Retrieved the current ArduPilot Plane listing over verified HTTPS using Python's OpenSSL transport and passed it through the same C# parser. Confirmed `stable` and `latest` are folders. No downloaded firmware files were executed.
- Direct live HTTPS from the C# executable could not be verified in the local Codex sandbox: Windows Schannel returned `SEC_E_NO_CREDENTIALS`. PowerShell and curl with Schannel failed identically. TLS certificate checks have not been disabled. The Windows CI workflow includes a direct live HTTPS smoke test for independent verification.
- GUI, file downloads, and Explorer drag-and-drop are not part of this milestone.
