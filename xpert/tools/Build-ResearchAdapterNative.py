"""Compile only the owned transport adapter; no algorithm or SDK execution."""
from pathlib import Path
import hashlib
import json
import os
import sys
from setuptools import Extension, setup
from Cython.Build import cythonize
from Cython.Compiler import Options

project = Path(__file__).resolve().parent.parent
output = Path(sys.argv[1]).resolve()
output.relative_to(project / "build")
output.mkdir(parents=True, exist_ok=False)
source = project / "modules-src/ResearchAdapter/research_entry.py"
original = source.read_bytes()
scratch = output / "compiler-input"
scratch.mkdir()
# Preserve every source byte except an optional UTF-8 BOM. text-mode output
# on Windows would translate existing CRLF a second time and break provenance.
original.decode("utf-8-sig")  # Strict validation before creating compiler input.
(scratch / "q73.py").write_bytes(original[3:] if original.startswith(b"\xef\xbb\xbf") else original)
Options.docstrings = False
os.chdir(output)
sys.argv = [__file__, "build_ext", "--build-lib", "runtime", "--build-temp", "objects"]
setup(ext_modules=cythonize([Extension("q73", ["compiler-input/q73.py"],
    define_macros=[("CYTHON_CLINE_IN_TRACEBACK", "0")])],
    compiler_directives={"language_level": 3, "annotation_typing": False, "binding": True},
    build_dir="generated", quiet=True))
if source.read_bytes() != original:
    raise RuntimeError("Adapter source changed during compilation")
(output / "source-receipt.json").write_text(json.dumps({
    "schema": "HVX.ResearchAdapter.NativeBuild/1",
    "source": str(source), "sourceSha256": hashlib.sha256(original).hexdigest().upper(),
    "compilerInputSha256": hashlib.sha256((scratch / "q73.py").read_bytes()).hexdigest().upper(),
    "algorithmSourcesChanged": False, "hardwareAccessed": False,
}, indent=2), encoding="utf-8")
