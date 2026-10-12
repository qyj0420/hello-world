# Public beta application-only packaging audit — 2026-10-11

Scope: existing r52 bytes, current packaging source and dependency metadata. This is not physical acceptance, a full vulnerability scan or a claim that an installer has been generated. No product, worker, SDK, device API, setup or ISCC was executed for this audit. Maintenance secrets and real device serials were not read. Subsequent application fixes and the final V1.1.2 r54 build are separately owned by the main task. Historical r52/r53 findings and proof results below remain historical, not relabelled as V1.1.2 evidence.

## Findings before the authorized packaging changes

| ID / severity / status | Location and trigger | Expected / observed / reproduction | Minimum action |
|---|---|---|---|
| P01 / high / reproduced by source and metadata | `tools/Build-V30BatchSetup.ps1:6`, `installer/XIONGXIN-V30Batch-20261010-08.iss:57`; using the old publisher for the new no-driver request | Expected independent application-only package. Old publisher requires the historical profile and Task41 installer payload; r52 actually has `Validation-V30-Batch-20261008`, despite version 1.1.1.0. Read the hard-coded lane and actual build manifest; no installer execution needed. | New AppId, install directory, actual compiler/profile define and isolated config/ProgramData identity; do not relabel old evidence. |
| P02 / high / final acceptance pending | `tools/Protect-OwnedAlgorithms.ps1:150`, `tools/Build-EmbeddedPayload.ps1:82`; treating packaging/protection completion as full confidentiality | Actual protection includes method encryption, constants and control flow; names are retained. GZip embedding explicitly is not encryption. Existing conversion receipts do not substitute final compatibility/confidentiality acceptance. Inspect receipts and actual protected artifacts without executing product. | Reuse the actual protected build, maintenance negative tests, deployment gate and final confidentiality review bound to the exact current artifact. |
| P03 / medium / reproduced metadata, reachability pending | `dependencies/ResearchRuntime/frozen-runtime-inputs.json:11`, `modules-src/DataAnalysis/src/ReviewERuntime.cs:61`; bundled Python 3.12.14 / OpenSSL 3.5.8 | These exact versions have security fixes in newer patch releases. Current E/E+ entry is a pinned native module over bounded pipes; targeted source inspection found no TLS, urllib, archive-extraction or socket entry in the compiled algorithm inputs. `-I` is Python isolation, not OS network sandboxing. No exploit was reproduced. | Record conditional exposure; do not silently swap frozen DLLs/pins. A traced patch-runtime update needs independent compatibility and protection regression. |
| P04 / medium / reproduced metadata | r52 `Xiongxin.OWTS.Desktop.exe`; requiring a signed customer binary | Actual Authenticode status is `NotSigned`. The compiler itself has a valid Pyrsys signature, which does not sign the product. The installer source has no signing tool configured. | Do not claim signed delivery. If code signing is required, obtain the user's approved certificate/signing method; never invent a signature. |
| P05 / medium / static path support confirmed; new-machine behavior pending | Calibration `PicoCore/Native.cs:36`, FactoryWorker `PicoRuntimeLocator.cs:11`, `ArtUsb3135Device.cs:611`; installation without bundled drivers | Absolute configured SDK paths and official x64 fallback locations exist, including dependent Pico DLL validation. Locally observed Pico 2.2.5.5051 / 2.2.2.5029 are vendor-signed; ART 1.9.58.331 is unsigned. Reading PE metadata is not driver/API validation. | Application-only installer must not install/rebind drivers. User separately verifies matching x64 SDK, USB driver and VC dependencies on the target machine. Missing/incompatible dependencies remain explicit failures. |
| P06 / medium / source corrected, on-disk ACL pending | New `installer/XIONGXIN-PublicBeta-NoDriver.iss:43`; OEM image import | Expected ordinary user access only to the dedicated image folder. Granting write to all of `{app}` would allow executable/algorithm modification. | Only `{app}\OEMpic` gets `users-modify`; no broad app ACL. Parent owns verified copy/import logic. Installation ACL and upgrade/uninstall behavior need later target-machine verification. |

## Dependency lookup (official sources queried 2026-10-11)

| Component / actual version source | Official lookup and applicability | Coverage limit |
|---|---|---|
| CPython 3.12.14, actual r52 `python.exe` / `python312.dll` version metadata, frozen manifest | [Python 3.12.15 security release](https://www.python.org/downloads/release/python-31215/) lists CVE-2026-19445 / 19553 (TLS context/BIO), 15310 (zip decompression memory), 82049 / 19672 / 87910 (tar extraction), 15806 (urllib credentials), 17084 (StringPrep). These require the named APIs/input conditions; current numerical entry has no observed use. The 3.12 branch releases are source-only. | No malformed-input exploit or complete stdlib call-graph analysis; bundled Expat internal version was not attested from `pyexpat.pyd` file version. |
| OpenSSL 3.5.8, actual r52 libssl/libcrypto PE metadata | [OpenSSL official advisories](https://www.openssl-library.org/news/vulnerabilities/): CVE-2026-35189, Low, affects 3.5.0 up to but excluding 3.5.9, can cause memory pressure on receiving a crafted TLS peer certificate. Other 3.5.9 fixes include conditional QUIC/EC paths; no such runtime entry was found in the targeted numerical sources. | Not a general proof that DLL presence is unreachable everywhere, and not a claim that the affected bytes are fixed. |
| NumPy 2.3.5, r52 `licenses/numpy-METADATA.txt` | [Maintainer advisory page](https://github.com/numpy/numpy/security/advisories) showed no published advisories. | This is not an all-CVE/SBOM scan. The shipped NumPy OpenBLAS DLL's exact internal version was not attested. No Python SciPy package is shipped; a DLL named `libscipy_openblas...` belongs to NumPy's native dependencies. |
| ONNX Runtime managed 1.27.0.0 / native 1.27.20260615.2.8f0278c, r52 PE metadata | [Maintainer advisory page](https://github.com/microsoft/onnxruntime/security/advisories) showed no published advisories. | Malicious-model robustness/transitive native-library vulnerabilities not exhaustively scanned. Only the approved content-addressed model payloads are included. |
| Inno Setup 6.7.3, pinned signed ISCC SHA256 and bundled release history (ISCC FileVersion is 0.0.0.0) | [Official downloads](https://jrsoftware.org/isdl.php), [6.x changes](https://jrsoftware.org/files/is6-whatsnew.htm). The 6.7.3 PowerShell helper path fix is present in this tool version; this lane does not execute a PowerShell installer helper. | No compiler execution in this audit; compiler-support DLLs were not separately vulnerability-certified. |
| ConfuserEx 1.6.0, pinned build-tool path/hash; Cython 3.1.6, declared build environment | [ConfuserEx advisories](https://github.com/mkaring/ConfuserEx/security/advisories), [Cython advisories](https://github.com/cython/cython/security/advisories) showed no published advisories. | Build-tool statements are not a runtime security certificate or reverse-engineering guarantee. |
| .NET Framework 4.8, target plus signed Microsoft prerequisite FileVersion 4.8.04115.00 | Locally checked prerequisite hash and Microsoft signature. | Target-machine OS/.NET cumulative security patch level not checked. |

No full OSV/NuGet/Windows driver scanner, network isolation test, installed ACL test, clean-machine installer test or complete transitive dependency inventory was executed. “No published advisory found” must not become “no vulnerabilities”.

## Initial application-only lane and historical offline evidence

- New AppId: `2DAB4E38-ED9A-402F-AC41-663D0B14198D`.
- Profile: `PublicBeta-NoDriver-20261011`; actual host define: `PUBLIC_BETA_NO_DRIVER_20261011`.
- Install directory: `{autopf}\HV-DAC Xpert\Public Beta 1.1.1 20261011-NoDriver`.
- Driver installers are excluded; no Task41, `[Run]`, driver shortcut or automatic product launch. Existing maintenance/password, protected algorithm, encrypted model and interactive UAC/prerequisite behavior are not removed.
- Latest reviewed manual 4.1: 82 original HTML-tree files plus four English-named Chinese/English PDF/DOCX copies; bilingual terminology PDF, original glossary HTML/PDF, desktop shortcut icon and installation readme. Source audit verifies 91 targets by hash before application inputs.
- Only `OEMpic` receives ordinary-user write permission. User images are not included by the publisher.
- Final offline packaging-source regression: `build/qa-public-beta-no-driver-setup-20261011-b/proof.json`, PASS 22 checks. It checks PowerShell parse, path traversal/ADS/ambiguous-name rejection, strict booleans, source/document hashes and rejection of the old actual r52 profile. It does not compile the installer or assert final r53 acceptance.

## Final-build handoff

The user subsequently explicitly requested V1.1.2 and deferred the full confidentiality review, with source to be provided separately for further encryption. This does not resolve the Task12 provenance/classification gap or constitute confidentiality acceptance. The original `AlgorithmConfidentialityGate.ps1` is unchanged. `Required` remains the default policy and still executes that gate; the authorized exception must be selected explicitly as `DeferredByUser` and supplied with a current, bound non-acceptance receipt.

- Current profile: `PublicBeta-NoDriver-1.1.2-20261011`.
- Actual host defines: `PUBLIC_BETA_NO_DRIVER_V112_20261011`, common `PUBLIC_BETA_NO_DRIVER_20261011`, and `MAINTENANCE_PASSWORD_GUARD`.
- Actual host/installer version: `1.1.2.0`; independent installation directory `{autopf}\HV-DAC Xpert\Public Beta 1.1.2 20261011-NoDriver`; independent host state `HVXPublicBetaNoDriver11220261011` (host implementation owned by main task).
- Existing protection, maintenance negative tests, approved model hashes, post-freeze deployment, current source regressions and interactive prerequisite/UAC behavior remain required. This exception is not a change to the other historical installer gates.
- Offline V1.1.2 packaging regression: `build/qa-public-beta-no-driver-v112-setup-20261011-a/proof.json`, PASS 45 checks. It covers default `Required`, sorted full-runtime binding, duplicate/invalid identities and wrong-version/profile/hash/time/non-acceptance receipt negatives, plus the original static checks. No receipt or installer was generated by this fixture test.
- Offline V1.1.2 source regression: `build/qa-public-beta-no-driver-v112-source-20261011-a/proof.json`, PASS 69 checks, including truthful completed/deferred source metadata negatives. It did not request actual r54 compiled-source coverage; that check is repeated against the final build before export.

`tools/Build-PublicBetaNoDriverSetup.ps1` accepts `SourceAudit`, `RecordDeferral`, `Stage`, `Validate`, `Compile`. `RecordDeferral` validates the actual protected host, freeze, maintenance and deployment evidence before creating a fresh `HVX.AlgorithmConfidentialityDeferral/1` receipt. Receipt status is **`DEFERRED_BY_USER`**, with `FullConfidentialityAccepted=false`, `ReviewCompleted=false`, `NotAConfidentialityPass=true`; EXE, freeze, version/profile and the complete runtime/model inventory are SHA256-bound. It cannot reuse a historical build or overwrite an existing receipt. Stage/Validate/Compile require the **actual new build** and actual post-freeze proofs, never a historical r52/r53 pass. Example arguments (proof paths remain placeholders until the main task freezes r54):

```powershell
$laneArgs = @{
  BuildRoot = 'D:\codex\HVXUnifiedDesktop\build\public-beta-1.1.2-20261011-r54'
  PayloadDirectory = 'D:\codex\HVXUnifiedDesktop\build\HV-DAC_Xpert_PublicBeta_1.1.2_NoDriver_20261011.payload'
  FreezeEvidence = 'D:\codex\HVXUnifiedDesktop\build\public-beta-1.1.2-20261011-r54\qa\public-beta-no-driver-freeze.json'
  MaintenancePasswordProof = '<actual current HVX.MaintenanceAccess.Published/1 proof>'
  DeploymentProof = '<actual current deployment-payload proof>'
  ConfidentialityPolicy = 'DeferredByUser'
  ConfidentialityDeferral = 'D:\codex\HVXUnifiedDesktop\build\public-beta-1.1.2-20261011-r54\qa\algorithm-confidentiality-deferred-by-user.json'
  CurrentEvidence = '<actual HVX.ExternalStaticAudit.CurrentEvidence/1 collector output>'
}
& .\tools\Build-PublicBetaNoDriverSetup.ps1 -Mode RecordDeferral @laneArgs
& .\tools\Build-PublicBetaNoDriverSetup.ps1 -Mode Stage @laneArgs
& .\tools\Build-PublicBetaNoDriverSetup.ps1 -Mode Validate @laneArgs
# Compile is a separate, expressly approved step after the final freeze.
```

Freeze JSON schema (actual hashes and UTC timestamp required; no fabricated pass):

```json
{
  "Schema": "HVX.PublicBetaNoDriver.Freeze/1",
  "BuildRoot": "<actual absolute isolated build directory>",
  "FrozenUtc": "<actual UTC ISO8601 timestamp before maintenance/deployment tests>",
  "ExecutableSHA256": "<actual SHA256>",
  "BuildManifestSHA256": "<actual SHA256>",
  "ModuleManifestSHA256": "<actual SHA256>",
  "HardwareAccessed": false
}
```

Remaining before packaging: main-task source freeze/new build; actual current maintenance/deployment/current-source evidence and either accepted full confidentiality review (default) or the explicit user-deferred receipt (this authorized lane); dependency-risk disposition; approved ISCC compilation. Remaining after compilation: installation/ACL/shortcut/help behavior and external driver/hardware checks by the user. Product and installer Authenticode status must be reported as observed. Deferred confidentiality, incomplete full provenance classification and the affected dependency bytes are not described as fixed or accepted.

## Source snapshot (separate post-package action)

`tools/Export-PublicBetaNoDriverSource.ps1 -Mode Audit` only inventories path-whitelisted source files and hashes. The latest offline selector regression passed 58 checks in `build/qa-public-beta-no-driver-source-20261011-c/proof.json`; no ZIP was created. The runtime-settings name rule now applies only to data/configuration formats, so formal `UserSettings.cs`, `UserConfig.cs`, related resource/class definitions remain included while personal JSON/XML/config files remain excluded. With `-BuildRoot` set to actual r53, the audit covered 576 distinct host/UI compiled source files with zero missing entries or source-hash mismatches. Missing, hash-changed, blocked and empty compiled-source sets have negative tests. Excluded: build/deliverables, nested build/bin/obj/evidence/data/logs, saved user settings/device catalogs/OEM images, credentials/private-key paths and extensions, driver/tool binaries, encrypted models and personal configuration. Historical diagnostics prose is not swept into the snapshot. This is a path/extension whitelist, **not a content-level secret scan**; no credential-content inspection is claimed. The source-only coverage pass does not resolve the separately blocked Task12 provenance/confidentiality package gate.

After the main task expressly confirms the final package freeze, use:

```powershell
& .\tools\Export-PublicBetaNoDriverSource.ps1 -Mode Export `
  -BuildRoot '<actual V1.1.2 r54 build>' `
  -FreezeEvidence '<actual new freeze JSON>' `
  -ReleaseEvidence '<actual staged release-validation.json>' `
  -ConfidentialityDeferral '<actual bound DEFERRED_BY_USER receipt>' `
  -SetupPath '<actual newly compiled setup>' `
  -ExpectedSetupSHA256 '<actual uppercase SHA256>'
```

Export binds the actual final installer/EXE/freeze, deferred receipt (when applicable) and current compiled host/UI source hashes, creates a new archive without overwriting any old export, and reads back every source ZIP entry to verify bytes/hash. `RELEASE_BINDING.json` records the actual confidentiality status without upgrading `DEFERRED_BY_USER` to PASS. Generated build pins and cross-module binary references are hash-bound but not included. The source archive intentionally omits third-party compiler/runtime binaries and models; its `BUILDING_SOURCE.md` explains that those require separately provisioned, reviewed dependencies rather than promising an immediately standalone rebuild.
