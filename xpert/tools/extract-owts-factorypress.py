#!/usr/bin/env python3
"""Generate the press-only OwtsServices for the Xpert Desktop shell.

vendor/factory-press/Xiongxin.Owts.HostAdapters/OwtsServices.cs is the FactoryPress composition root.
It also composes Persistence / Algorithms / Ai / Task15 projects that are not in
the FactoryPress handoff, so the file cannot be compiled as a unit here.

This script copies the acquisition / press members of that file byte-for-byte
(RunFactoryAlignedPress, RunFactoryAlignedCalibration, live/FakeLive session
factories, e-stop latch, SessionFault latch, Pico cooldown, range memory ...)
into build-sdk/factorypress/Xiongxin.Owts.HostAdapters.FactoryPress/OwtsServices.FactoryPress.g.cs.
Only Compose() and Dispose() are replaced (see PRESS_ONLY_TAIL); they drop the
non-acquisition services and nothing else.

Usage:
  extract-owts-factorypress.py            # regenerate
  extract-owts-factorypress.py --check    # exit 1 if the generated file is stale
"""
import hashlib
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SOURCE = os.path.join(ROOT, "vendor", "factory-press", "Xiongxin.Owts.HostAdapters", "OwtsServices.cs")
TARGET = os.path.join(ROOT, "build-sdk", "factorypress", "Xiongxin.Owts.HostAdapters.FactoryPress", "OwtsServices.FactoryPress.g.cs")

# Members of OwtsServices that depend on projects absent from the handoff
# (Persistence / Algorithms / Ai / Task15) or are replaced below.
EXCLUDED_MEMBERS = {
    "DefectRecognition", "DefectRecognizer", "LocationAnalyzer", "SeventhEvidence",
    "SignalFilter", "ConclusionEngine", "Repository", "Workspace", "ReportExporter",
    "Compose", "OpenProjectDatabase", "ListProjectLibraries", "TrySwitchProjectLibrary",
    "ListCurrentAcquisitionSummaries", "EvaluateConclusionPath", "DumpMidStatusJson",
    "DescribeOnnxFeatureStub", "BuildLocateRequestFromPressCycle", "Dispose",
}
EXCLUDED_USINGS = {
    "using Xiongxin.Owts.Ai;", "using Xiongxin.Owts.Algorithms;", "using Xiongxin.Owts.Persistence;",
}

PRESS_ONLY_TAIL = '''
        // ---- press-only replacements (not copied from OwtsServices.cs) ----

        /// <summary>
        /// Press-only composition: acquisition session + status board + PD calibration path.
        /// The ONNX / location / conclusion / SQLite / report services of the full
        /// OwtsServices are not part of the Xpert pressure path and are not composed.
        /// </summary>
        static OwtsServices Compose(OwtsRuntimeOptions options, IAcquisitionSession acquisition, bool live, bool fakeLive, IFirstZeroPhaseEvaluator firstZero = null)
        {
            var services = new OwtsServices();
            services.Options = options;
            services.IsLiveHardware = live;
            services.IsFakeLive = fakeLive;
            services.Acquisition = acquisition;
            services.FirstZeroPhase = firstZero ?? FirstZeroPhaseEvaluatorFactory.Create(options != null ? options.Task6RuntimeDirectory : null);
            services.StatusBoard = new OperationalStatusBoard();
            services.PdCalibrationPcPerVolt = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
            services.PdCalibrationPath = PdCalibrationStore.DefaultPath();
            return services;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Acquisition != null) Acquisition.Dispose();
            if (FirstZeroPhase != null) FirstZeroPhase.Dispose();
            FirstZeroPhase = null;
            Acquisition = null;
            StatusBoard = null;
        }
'''


def scan_class_members(text, body_start, body_end):
    """Split the class body into member chunks using brace depth (string/comment aware)."""
    members = []
    i = body_start
    depth = 0
    chunk_start = body_start
    n = body_end
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == "@" and nxt == '"':
            i += 2
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                if text[i] == '"':
                    break
                i += 1
            i += 1
            continue
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                if text[i] == "\\":
                    i += 1
                i += 1
            i += 1
            continue
        if c == "'":
            i += 1
            while i < n and text[i] != "'":
                if text[i] == "\\":
                    i += 1
                i += 1
            i += 1
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                end = i + 1
                members.append((chunk_start, end))
                chunk_start = end
            elif depth < 0:
                members.append((chunk_start, i + 1))
                return members
        elif c == ";" and depth == 0:
            members.append((chunk_start, i + 1))
            chunk_start = i + 1
        i += 1
    return members


def strip_comments(s):
    s = re.sub(r"/\*.*?\*/", "", s, flags=re.S)
    s = re.sub(r"//[^\n]*", "", s)
    s = re.sub(r"\[[^\]]*\]\s*", "", s)  # attributes
    return s


def member_name(chunk):
    sig = strip_comments(chunk).strip()
    head = re.split(r"[({=;]", sig, maxsplit=1)[0].strip()
    toks = head.split()
    return toks[-1] if toks else ""


def generate():
    with open(SOURCE, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8-sig")
    sha = hashlib.sha256(raw).hexdigest().upper()

    m = re.search(r"public sealed class OwtsServices : IDisposable\s*\{", text)
    if not m:
        raise SystemExit("OwtsServices class header not found")
    body_start = m.end()
    # class body ends at the matching brace: scan with depth starting at 1
    members = scan_class_members(text, body_start, len(text))
    # The class's own closing brace shows up as a chunk that is only whitespace + '}'
    # (depth goes negative there, which scan_class_members records as a member end).
    class_end = None
    kept = []
    excluded = []
    for (s, e) in members:
        chunk = text[s:e]
        if strip_comments(chunk).strip() == "}":
            class_end = s + chunk.rfind("}")
            break
        name = member_name(chunk)
        if name in EXCLUDED_MEMBERS:
            excluded.append(name)
        else:
            kept.append(chunk)
    if class_end is None:
        raise SystemExit("class end not found")

    header = text[:m.start()]
    header_lines = [ln for ln in header.splitlines(True) if ln.strip() not in EXCLUDED_USINGS]
    header = "".join(header_lines)
    tail = text[class_end:]  # closing brace of class + result types + CallbackTextWriter + namespace end

    out = []
    out.append("// <auto-generated>\n")
    out.append("// Generated by tools/extract-owts-factorypress.py from\n")
    out.append("//   vendor/factory-press/Xiongxin.Owts.HostAdapters/OwtsServices.cs  SHA256 " + sha + "\n")
    out.append("// Members copied byte-for-byte; excluded (absent Persistence/Algorithms/Ai/Task15 or replaced): \n")
    out.append("//   " + ", ".join(sorted(set(excluded))) + "\n")
    out.append("// Do not edit by hand; edit OwtsServices.cs and regenerate.\n")
    out.append("// </auto-generated>\n")
    out.append(header)
    out.append(text[m.start():m.end()])
    out.extend(kept)
    out.append("\n")
    out.append(PRESS_ONLY_TAIL)
    out.append("    ")
    out.append(tail)
    return "".join(out), sha, sorted(set(excluded))


def main():
    content, sha, excluded = generate()
    data = content.encode("utf-8")
    if "--check" in sys.argv:
        try:
            with open(TARGET, "rb") as f:
                current = f.read()
        except FileNotFoundError:
            print("STALE: generated file missing")
            return 1
        if current != data:
            print("STALE: regenerate with tools/extract-owts-factorypress.py")
            return 1
        print("OK: press-only OwtsServices matches OwtsServices.cs SHA256 " + sha)
        return 0
    os.makedirs(os.path.dirname(TARGET), exist_ok=True)
    with open(TARGET, "wb") as f:
        f.write(data)
    print("generated " + os.path.relpath(TARGET, ROOT) + " from SHA256 " + sha)
    print("excluded: " + ", ".join(excluded))
    return 0


if __name__ == "__main__":
    sys.exit(main())
