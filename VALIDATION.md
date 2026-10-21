# Desktop v0.3 verification

- Scanner, transfer, and native Windows Shell integration checks pass.
- The drag loop now runs on the source window input thread, retaining the initiating mouse gesture.
- Explorer asynchronous copy negotiation is tested: Drop returns before file extraction finishes, and the message pump continues while the transfer completes.
- Transfer lifetime remains active through EndOperation, with cache cleanup deferred until all download writers stop.
- Mixed file/folder selections preserve nested and empty folders. Filename safety, bounded concurrency, cancellation, partial-file cleanup, and indexed stream bytes are tested.
- The branded application render was visually inspected. Menu, directory navigation, logo, executable icon, and developer credit are included; Download to controls are removed.
- Live desktop screenshot capture still times out in this environment, so the physical mouse gesture to the desktop could not be independently verified here. Native Shell copying and async completion are exercised through the real Windows drop target.

Known limits: one transfer selection at a time; no automatic retry/resume; file redirects rejected; Windows-incompatible and overly long paths fail explicitly. Explorer may leave partial destinations after cancellation. Keep the app open while copying. The package is unsigned.
