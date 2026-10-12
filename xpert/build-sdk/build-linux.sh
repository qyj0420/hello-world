#!/usr/bin/env bash
# 雄新电力 · 雄新 OWTS — SDK build + win-x64 publish of the HV-DAC Xpert desktop shell with the
# FactoryPress pressure stack. Runs on Linux or Windows (Git Bash) with the .NET 8 SDK; produces the
# .NET Framework 4.8 WinExe Xiongxin.OWTS.Desktop.exe. No product, SDK or device is started.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK="$ROOT/build-sdk"
CONFIG="${CONFIG:-Release}"
PUBLISH="$ROOT/publish"
STAGE="$PUBLISH/Xiongxin.OWTS.Desktop"
ZIP="$PUBLISH/xpert-desktop-win-x64.zip"
STAMP="${BUILD_STAMP:-$(TZ=Asia/Shanghai date +%Y-%m-%dT%H:%M:%S+08:00)}"

command -v dotnet >/dev/null || { echo "dotnet SDK not found (export PATH=\$HOME/.dotnet:\$PATH)" >&2; exit 2; }

echo "== inputs"
python3 "$ROOT/tools/extract-owts-factorypress.py" --check
mkdir -p "$SDK/generated"
python3 - "$ROOT" <<'PY'
import hashlib, json, os, sys
root = sys.argv[1]
src = os.path.join(root, "modules-src", "LocationABCD", "ABRelease", "confirmed-legacy-inputs.json")
raw = open(src, "rb").read()
if hashlib.sha256(raw).hexdigest().upper() != "5BE1895AA9BF2D9449A4875112C1A3D976F0119D23F624E976141FE955C6D37D":
    sys.exit("Legacy unit confirmation changed")
pins = [r["sha256"] for r in json.loads(raw.decode("utf-8-sig"))]
with open(os.path.join(root, "build-sdk", "generated", "legacy-unit-pins.json"), "w") as f:
    json.dump(pins, f, indent=4)
PY

echo "== build ($CONFIG)"
projects=(
  "$SDK/Xiongxin.OWTS.Desktop/Xiongxin.OWTS.Desktop.csproj"
  "$SDK/modules/DataAnalysis/DataAnalysis.csproj"
  "$SDK/modules/ProjectCreation/ProjectCreation.csproj"
  "$SDK/modules/ProjectManagement/ProjectManagement.csproj"
  "$SDK/modules/DataBrowser/DataBrowser.csproj"
  "$SDK/modules/ReportGenerator/ReportGenerator.csproj"
  "$SDK/modules/PressureTest/PressureTest.csproj"
  "$SDK/modules/CalibrationTest/CalibrationTest.csproj"
  "$SDK/runtime/ReportEngine/ReportEngine.csproj"
  "$SDK/runtime/CalibrationAcquisition/CalibrationAcquisition.csproj"
  "$SDK/runtime/PressureRuntime/PressureRuntime.csproj"
  "$SDK/runtime/Task39/Task39.csproj"
  "$SDK/factorypress/Xiongxin.OWTS.FactoryPressBridge/Xiongxin.OWTS.FactoryPressBridge.csproj"
  "$SDK/factorypress/Xiongxin.OWTS.FactoryPressBridge.Smoke/Xiongxin.OWTS.FactoryPressBridge.Smoke.csproj"
)
for p in "${projects[@]}"; do
  dotnet build "$p" -c "$CONFIG" -nologo -v q -clp:ErrorsOnly
done

echo "== stage $STAGE"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/modules" "$STAGE/assets/shortcuts"
bin() { echo "$SDK/$1/bin/$CONFIG"; }
cp "$(bin Xiongxin.OWTS.Desktop)/Xiongxin.OWTS.Desktop.exe" "$STAGE/"
cp "$ROOT/App.config" "$STAGE/Xiongxin.OWTS.Desktop.exe.config"
cp "$ROOT/assets/xiongxin.ico" "$ROOT/assets/xiongxin-original.png" "$STAGE/assets/"
cp "$ROOT/assets/shortcuts/256.ico" "$ROOT/assets/shortcuts/512.ico" "$STAGE/assets/shortcuts/"
if [ -d "$ROOT/docs/international" ]; then mkdir -p "$STAGE/docs/international"; cp "$ROOT/docs/international/"* "$STAGE/docs/international/"; fi

stage_module() { mkdir -p "$STAGE/modules/$1"; cp "$(bin "modules/$1")/$2" "$STAGE/modules/$1/"; }
stage_module DataAnalysis HVX.PdReview.V1.0.dll
stage_module ProjectCreation Xiongxin.OWTS.ProjectCreation.dll
stage_module ProjectManagement Xiongxin.OWTS.ProjectManagement.dll
stage_module DataBrowser Xiongxin.OWTS.DataBrowser.dll
stage_module ReportGenerator Xiongxin.OWTS.ReportGenerator.dll
stage_module PressureTest Xiongxin.OWTS.PressureTest.dll
cp "$STAGE/modules/DataAnalysis/HVX.PdReview.V1.0.dll" "$STAGE/modules/PressureTest/"
stage_module CalibrationTest Xiongxin.OWTS.CalibrationTest.dll
mkdir -p "$STAGE/modules/ReportGenerator/runtime" "$STAGE/modules/CalibrationAcquisition" "$STAGE/modules/PressureRuntime" "$STAGE/modules/Task39" "$STAGE/modules/FactoryPressOwts"
cp "$(bin runtime/ReportEngine)/HVX.Report.Engine.dll" "$STAGE/modules/ReportGenerator/runtime/"
cp "$(bin runtime/CalibrationAcquisition)/XIONGXIN.Calibration.Verification.exe" "$STAGE/modules/CalibrationAcquisition/"
cp "$ROOT/modules-src/CalibrationStandalone/App.config" "$STAGE/modules/CalibrationAcquisition/XIONGXIN.Calibration.Verification.exe.config"
cp "$(bin runtime/PressureRuntime)/Xiongxin.Pressure.Runtime.exe" "$STAGE/modules/PressureRuntime/"
cp "$ROOT/App.config" "$STAGE/modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe.config"
cp "$(bin runtime/Task39)/Xiongxin.Task39.InputWorker.exe" "$STAGE/modules/Task39/"
cp "$ROOT/App.config" "$STAGE/modules/Task39/Xiongxin.Task39.InputWorker.exe.config"
fp="$SDK/factorypress/Xiongxin.OWTS.FactoryPressBridge/bin/$CONFIG/net48"
for f in Xiongxin.OWTS.FactoryPressBridge.dll Xiongxin.Owts.HostAdapters.FactoryPress.dll Xiongxin.Owts.Contracts.dll Xiongxin.Owts.Acquisition.dll; do
  cp "$fp/$f" "$STAGE/modules/FactoryPressOwts/"
done
cat > "$STAGE/factory-press-backend.enabled" <<EOF
雄新电力 · 雄新 OWTS
This marker routes the 加压测试 page to the FactoryPress hardware stack
(modules/FactoryPressOwts: DeviceSdkLease / LiveEmergencyMonitor / SessionFault /
StatusCopy / RunFactoryAlignedPress / Task6RangeAdvisor).
Delete this file (or set XIONGXIN_OWTS_FACTORY_ALIGNED=0) to use the legacy PressureRuntime path.
EOF

echo "== manifest"
python3 - "$STAGE" "$STAMP" "$CONFIG" <<'PY'
import hashlib, json, os, sys
stage, stamp, config = sys.argv[1:4]
files = []
for dirpath, _, names in os.walk(stage):
    for n in sorted(names):
        full = os.path.join(dirpath, n)
        rel = os.path.relpath(full, stage).replace(os.sep, "/")
        if rel in ("BUILD_MANIFEST.json",):
            continue
        files.append({"path": rel, "bytes": os.path.getsize(full), "sha256": hashlib.sha256(open(full, "rb").read()).hexdigest().upper()})
files.sort(key=lambda f: f["path"])
manifest = {
    "schema": "XIONGXIN.OWTS.DacRebuild.Publish/1",
    "brand": "雄新电力 · 雄新 OWTS",
    "product": "HV-DAC Xpert V1.1.2 r58 + FactoryPress pressure stack",
    "buildStamp": stamp,
    "configuration": config,
    "targetFramework": ".NET Framework 4.8 (x64 WinExe; requires the OS .NET Framework 4.8 runtime)",
    "hostDefines": ["V30_BATCH_VALIDATION_20261008", "PUBLIC_BETA_NO_DRIVER_20261011", "PUBLIC_BETA_NO_DRIVER_V112_20261011", "MAINTENANCE_PASSWORD_GUARD", "DAC_REBUILD_FACTORYPRESS_20261012"],
    "protectedRelease": False,
    "hardwareAccessed": False,
    "physicalAcceptance": False,
    "notIncluded": [
        "HvxAcquisitionNative.dll (MSVC native bridge; legacy hardware connection, Task38, PressureRuntime/Task39/CalibrationAcquisition hardware I/O)",
        "HardwareIntegration / HV-DAC-Xpert connector and Task38 (native bridge, vendor Sxuptp/SxConnectDev, manifests absent from snapshot)",
        "Task17 location DLL fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll (three private runtime DLLs absent from snapshot)",
        "r71 research runtime, defect model payload, ONNX runtime, Hybrid native core",
        "assets/device-asset-catalog.v1.json (privacy-excluded), assets/reference-simulation/rdfz-jmf-v1.json",
        "ART (Art_DAQ.dll) and PicoScope 5000A (ps5000a.dll, picoipp.dll) vendor drivers"
    ],
    "files": files,
}
with open(os.path.join(stage, "BUILD_MANIFEST.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
print("files", len(files))
PY

echo "== zip"
python3 - "$PUBLISH" "$STAGE" "$ZIP" <<'PY'
import os, sys, zipfile
publish, stage, out = sys.argv[1:4]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for dirpath, _, names in os.walk(stage):
        for n in sorted(names):
            full = os.path.join(dirpath, n)
            arc = os.path.relpath(full, publish).replace(os.sep, "/")
            info = zipfile.ZipInfo(arc, date_time=(2026, 10, 12, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            info.flag_bits |= 0x800
            with open(full, "rb") as f:
                z.writestr(info, f.read())
print(out, os.path.getsize(out))
PY
echo "BUILD_STAMP=$STAMP"
