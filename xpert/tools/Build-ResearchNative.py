"""Build-only Cython native modules. Original candidate sources stay unchanged."""
from pathlib import Path
import hashlib
import json
import shutil
import sys
import os
import importlib.util
from setuptools import Extension, setup
from Cython.Build import cythonize
from Cython.Compiler import Options

project = Path(__file__).resolve().parent.parent
source = project / 'modules-src/LocationABCD/CDRelease'
manifest = source / 'manifest.json'
assert hashlib.sha256(manifest.read_bytes()).hexdigest() == '6d01a4f8116ee378774bced70297ebdec4345b147930b9c273f70cff40a046b0'
for name, digest in json.loads(manifest.read_text('utf-8'))['files'].items():
    assert hashlib.sha256((source/name).read_bytes()).hexdigest() == digest, name
output = Path(sys.argv[1]).resolve()
output.relative_to(project / 'build')
output.mkdir(parents=True, exist_ok=False)
scratch = output / 'compiler-input'
scratch.mkdir()
inputs = {'q71': source/'C/owts_pd/localization_c.py',
          'q72': source/'D/localization_d.py',
          'q73': project/'modules-src/ResearchAdapter/research_entry.py'}
spec = importlib.util.spec_from_file_location('e_sources',project/'modules-src/LocationE/runtime_sources.py')
e_sources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(e_sources)
adapted_e = e_sources.source_inputs()
for module, path in inputs.items():
    code = path.read_text('utf-8-sig')
    if module == 'q72':
        code = code.replace("sys.path.insert(0, str(Path(__file__).resolve().parent.parent/'C'))", '')
        code = code.replace('from owts_pd import localization_c as c', 'import q71 as c')
    (scratch/(module+'.py')).write_text(code, encoding='utf-8')
for module,code in adapted_e.items():
    (scratch/(module+'.py')).write_text(code,encoding='utf-8')
Options.docstrings = False
os.chdir(output)
sys.argv = [__file__, 'build_ext', '--build-lib', 'runtime', '--build-temp', 'objects']
setup(ext_modules=cythonize([Extension(name, ['compiler-input/'+name+'.py'],
    define_macros=[('CYTHON_CLINE_IN_TRACEBACK', '0')]) for name in list(inputs)+list(adapted_e)],
    compiler_directives={'language_level': 3, 'annotation_typing': False, 'binding': True},
    build_dir='generated', quiet=True))
(output/'source-receipt.json').write_text(json.dumps({name:hashlib.sha256(path.read_bytes()).hexdigest()
    for name,path in inputs.items()},indent=2),encoding='utf-8')
(output/'e-source-receipt.json').write_text(json.dumps({
    'manifestSha256':hashlib.sha256((project/'modules-src/LocationE/source-manifest.json').read_bytes()).hexdigest(),
    'adapterSha256':hashlib.sha256((project/'modules-src/LocationE/runtime_sources.py').read_bytes()).hexdigest(),
    'eplusPacketManifestSha256':hashlib.sha256((project/'modules-src/LocationEPlusPacket/source-manifest.json').read_bytes()).hexdigest(),
    'eplusFinalScoreManifestSha256':hashlib.sha256((project/'modules-src/LocationEPlusFinalScore/source-manifest.json').read_bytes()).hexdigest(),
    'compiledInputs':{name:hashlib.sha256(code.encode('utf-8')).hexdigest() for name,code in adapted_e.items()},
    'correctedEPlusZipSha256':'F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F',
    'correctedCompositionSha256':hashlib.sha256((project/'provenance/eplus-corrected-20261012-r01/package/eplus_api.py').read_bytes()).hexdigest(),
    'physicalValidation':False},indent=2),encoding='utf-8')
