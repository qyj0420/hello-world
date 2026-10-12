# HVX Acquisition Native — stage 1 boundary

Windows x64 C++17 module `HvxAcquisitionNative.dll`. It exports the existing 27 ART-DAQ and 20 PicoScope 5000A APIs, with the original names, argument widths, callbacks and vendor status values. The ABI list is explicit in `HvxSdkApis.inc` and `HvxAcquisitionNative.h`.

This is **native API concentration and handle ownership**, not a completed migration of the high-level acquisition/voltage state machine. C# still owns application workflow, AI/AO/channel allocation, timeouts, DI supervision, buffers, callback generations and voltage safety decisions. Python/algorithms, waveform data, voltage step sizes and outputs are unchanged by this module. No SDK reset, reconnect, retry of outputs, or automatic resumption of voltage is implemented here.

## Build and offline verification

From PowerShell:

```powershell
& .\native-hardware\build-native.ps1 -RunOfflineTests
& .\native-hardware\audit-exports.ps1
```

The installed MSVC/Windows SDK is located by the existing `tools/Get-CppToolchain.ps1`; nothing is downloaded. Production DLL is **compiled only**, never loaded by the tests. All runtime tests link a separate `HvxAcquisitionNativeFake.dll` built with `HVX_FAKE_TEST`; its real SDK load branch is compiled out. It resolves only an explicitly loaded, independently built `HvxFakeSdk.dll`. SDK-named PE fixtures are copies of the fake bytes for **path validation only** and are never loaded. Test hooks are absent from the production exports. Do not deploy the `fake-only` directory.

Evidence: `out/production-exports.txt`, `out/production-pe-audit.txt`, `out/api-coverage.json`, `out/build-evidence.json`, `out/fake-only/offline-results.txt`. The build evidence hashes the native input files and final production DLL. API coverage separately records the current C# declaration input hashes and read-only official header hashes; it is not an application-wide release snapshot. Production PE is x64 with ASLR/high-entropy VA, NX and Control Flow Guard; its only static imports are Windows system DLLs, not vendor SDKs or a separate MSVC runtime.

## Lifetime and concurrency

- ART CreateTask records every returned nonzero handle. Nonnegative status permits normal use. A failed create that unexpectedly returns a handle is quarantined: only explicit Clear is allowed.
- Pico OpenUnit retains **positive** handles for success and documented power/USB warnings `0x119`, `0x11A`, `0x11E`; these allow the caller's official ChangePowerSource continuation. Other failure + positive-handle results are retained but quarantined, permitting only Close. The official failed-open `-1` and no-scope `0` sentinels are never registered and do not retain an owner lease; vendor failure statuses remain unchanged. A success status with a nonpositive handle fails closed as a private invalid-handle error.
- Stop never deletes ownership. ART Clear and Pico Close first require actual SDK cleanup success, then an acknowledged owner-lease release. If the latter fails or exceeds its bounded wait, a private cleanup error is returned with the original successful SDK status retained in diagnostics. An SDK-released tombstone remains registered and blocks all ordinary operations; an explicit repeated Clear/Close only confirms/retries **the same lease ticket**, never calls SDK cleanup on the invalidated handle again. The tombstone is removed only after release acknowledgement. A failed SDK cleanup retains the original live handle unchanged. No fabricated success is returned.
- Create/Clear and Open/Close use a family-local lifecycle try-lock; normal DI/AI/Pico calls do not acquire it. While cleanup is in flight or a tombstone is pending, new Create/Open is rejected before any SDK call, including SDK reuse of a just-released identifier. A delayed release receipt cannot decrement the owner reference twice. Cross-process leases remain conservative family-wide leases, not device-level scheduling.
- Per-handle try-locks make same-handle concurrent operations return a distinct busy error promptly. Different handles, including a dedicated DI task while AI/Pico is reading, do not share an SDK-call lock. Calls are not automatically repeated.
- ART and Pico each use a **conservative family-wide cross-process lease**, not resource-level parallel scheduling. A dedicated holding thread acquires and releases the named Windows mutex, so creation and cleanup may occur on different caller threads. Requests have a bounded 2-second deadline; mutex acquisition itself is nonblocking. References remain while handles are owned. A dead process releases its OS mutex; this does not assert hardware is discharged or permit automatic continuation.
- The lease coordinates participating HVX native modules only. An unrelated SDK application/LabVIEW/vendor utility does not honor it and can still conflict. Existing C# device/channel coordination and official SDK status checks remain necessary. DMC administrative device-record operations are separate and are not exported by this acquisition bridge.
- SDK and bridge DLLs are held/pinned for process lifetime; no loader-lock cleanup occurs in DllMain. C# must retain native buffer/delegate lifetimes and reject stale callbacks using its existing session generations.

### Official Pico handle reference

The official PicoScope 5000 Series (A API) Programmer's Guide, revision 4, section 4.37 `ps5000aOpenUnit`, printed page 69 (PDF page 75), includes the 5000D series and specifies that only positive identifiers are valid device handles. Revision 3, section 4.34, printed page 63 (PDF page 69), explicitly distinguishes the failed-open `-1`, no-scope `0`, and positive-handle results. Both official PDFs were opened and the relevant section read on 2026-10-12: [revision 4](https://www.picotech.com/download/manuals/picoscope-5000-series-a-api-programmers-guide.pdf), [revision 3](https://www.picotech.com/download/manuals/ps5000apg.en-3.pdf). The fake regression separately checks failure sentinels, power-warning ownership, and quarantined unexpected failures; no real SDK is loaded for this verification.

## Loading contract

`HvxConfigureArtPathW` and `HvxConfigurePicoPathW` return signed private bridge errors or 0. They validate an absolute local path, exact SDK base filename, x64 PE headers, absence of reparse components, and (for Pico) a matching sibling x64 `picoipp.dll`. Configuration does **not** load a driver. The selected path is immutable within the process; repeated selection of the same path is harmless. Existing SDK modules at a different path are rejected.

On the first real API operation, the loader uses only the configured path, or the official installation directories resolved through Windows Known Folders, or System32. It never searches the working directory/PATH, changes SetDllDirectory, or hardcodes a user's directory. LoadLibraryEx uses `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32`. Pico's exact sibling dependency is preloaded safely. A missing/invalid SDK, dependency or export is a private bridge failure, not a fabricated device-disconnected vendor status. Runtime source-path trust, SDK installation/version compatibility and permissions still require target-machine validation.

`HvxSuppressPicoStartupWindow()` calls `ps5000aApplyFix(485331304, 4582)` using its established **void** ABI. It reports only bridge invocation success/failure and never interprets an undocumented return register as a Pico status.

## Status and diagnostics

All ART SDK functions return original `int32_t` vendor status. All Pico SDK functions return original **`uint32_t`** vendor status. Bridge errors have separate application-private ranges:

| ID | Reason | ART / configuration | Pico API |
| --- | --- | --- | --- |
| 1 | SDK/dependency missing | -4850001 | 0xE4850001 |
| 2 | Export missing | -4850002 | 0xE4850002 |
| 3 | Cross-process owner conflict | -4850003 | 0xE4850003 |
| 4 | Unknown/released handle | -4850004 | 0xE4850004 |
| 5 | Handle busy | -4850005 | 0xE4850005 |
| 6 | Invalid path/x64 image | -4850006 | 0xE4850006 |
| 7 | Selected/loaded path conflict | -4850007 | 0xE4850007 |
| 8 | Native SDK exception | -4850008 | 0xE4850008 |
| 9 | Internal bridge failure | -4850009 | 0xE4850009 |
| 10 | Invalid bridge argument | -4850010 | 0xE485000A |
| 11 | Ownership service deadline | -4850011 | 0xE485000B |
| 12 | Failed-open/create handle quarantined | -4850012 | 0xE485000C |
| 13 | Owner-lease release unconfirmed | -4850013 | 0xE485000D |

They are **not** official ART/Pico error aliases. In particular ART `-200284` and `-50103` are not reinterpreted, ignored, or converted into success.

Diagnostics are thread-local; retrieve them on the calling thread before another bridge call:

```c
int32_t HvxGetLastErrorInfo(char* utf8Buffer, uint32_t capacity);
int32_t HvxGetLastStatus(int32_t* bridgeCode, uint32_t* vendorStatus, uint32_t* win32Error);
int32_t HvxDescribeStatus(uint32_t family /* 1=ART,2=Pico */, uint32_t rawStatus, char* utf8Buffer, uint32_t capacity);
int32_t HvxGetOwnershipCount(uint32_t family, uint32_t* count); /* no SDK calls */
```

The diagnostic bridgeCode uses the signed private range for both families; vendorStatus retains the raw 32 bits when an SDK status was actually returned. On a loader/SEH/pre-SDK bridge failure, no vendor status is fabricated: bridgeCode is nonzero, vendorStatus is 0/not available. A lease-cleanup error **after actual SDK cleanup** instead retains that SDK status and explicitly records `sdkCleanupCompleted=true`, `rawVendorStatus`, the lease status and Win32 release failure. A successful SDK cleanup is not a full owner-release acknowledgement. Detail strings are developer diagnostics, not ordinary customer popup text. C# Pico checks recognize the entire reserved `0xE485xxxx` private domain; the new cleanup status uses the existing conservative stop fallback, not an official Pico disconnected alias. Temporary/global calls also explicitly return unconfirmed release as a private error (preserving original bridge/vendor status in diagnostics), and block new acquisition conservatively; they have no SDK resource handle to retry and require ending the process safely. No automatic owner reset, reconnection or output recovery is provided.

## Scope not validated here

No actual device, SDK runtime, product GUI, calibration, high-voltage operation, electrical timing, driver stress/USB disconnect or real callback scheduling was executed. Official installed headers were read to verify ABI, including ART's int32 sample-count parameter and void-pointer TaskHandle, and Pico's power-warning statuses/callback widths. Synthetic checks demonstrate boundary behavior only. Full high-level resource arbitration, callback-generation enforcement inside C++, native cancellation of a blocking SDK call, multi-device IPC scheduling and DMC administration are not migrated in this stage. Existing independent DI safety supervision and application safety checks must remain in place.
