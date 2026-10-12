"""Copy only already SHA-pinned algorithm runtime inputs; no product/SDK loading."""
from pathlib import Path
import hashlib
import importlib.util
import json
import shutil

ROOT=Path(__file__).resolve().parent.parent
FROZEN=Path('D:/codex/HVXR27Release/build/r27v1007d')
CURRENT=ROOT/'build/r58-eplus-corrected-runtime-20261012-01'
DEPS=ROOT/'dependencies/ResearchRuntime'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest().upper()


def unlinked(path):
    for p in (path,*path.parents):
        if p.is_symlink() or (hasattr(p,'is_junction') and p.is_junction()):
            raise RuntimeError('Linked input/output rejected: '+str(p))


def copy_verified(source,target,expected):
    unlinked(source);unlinked(target)
    if sha(source)!=expected:
        raise RuntimeError('Source hash differs: '+str(source))
    target.parent.mkdir(parents=True,exist_ok=True)
    with source.open('rb') as src,target.open('xb') as dst:
        shutil.copyfileobj(src,dst,1024*1024)
    if sha(target)!=expected or sha(source)!=expected:
        raise RuntimeError('Copy hash differs: '+str(target))


def main():
    dest=DEPS/'frozen-r27-source';corrected=DEPS/'r58-corrected-runtime'
    assert not dest.exists() and not corrected.exists(),'Earlier dependency copies are preserved'
    freeze=FROZEN/'qa/r27-freeze.json';pinfile=FROZEN/'research-pins.json'
    assert sha(freeze)=='79EC1832D17F15D2FB4698AF950D94ED39B94CE7DB939D2FEB8E5AB8703B68CF'
    assert sha(pinfile)=='E107F81077FEB010AD9E1D66AA4F6BB5511FA28D9BC3B7144F62CC24448ECDE7'
    pins=json.loads(pinfile.read_text('utf-8-sig'));assert len(pins)==439
    for name,digest in pins.items():
        assert name and not name.startswith(('/', '\\')) and ':' not in name and '\\' not in name and '..' not in name.split('/')
        copy_verified(FROZEN/'modules/r71'/name,dest/'modules/r71'/name,digest)
    for path,rel in ((freeze,'qa/r27-freeze.json'),(pinfile,'research-pins.json')):
        copy_verified(path,dest/rel,sha(path))
    runtime=CURRENT/'modules/r71'
    current_pins=CURRENT/'modules/research-pins.json'
    newpins=json.loads(current_pins.read_text('utf-8-sig'))
    for name,digest in newpins.items():
        copy_verified(runtime/name,corrected/'r71'/name,digest)
    for name in ('research-pins.json','research-runtime-input-receipt.json'):
        copy_verified(CURRENT/'modules'/name,corrected/name,sha(CURRENT/'modules'/name))
    for source in (CURRENT/'research-native/compiler-input').glob('q*.py'):
        copy_verified(source,corrected/'research-native/compiler-input'/source.name,sha(source))
    for name in ('source-receipt.json','e-source-receipt.json'):
        copy_verified(CURRENT/'research-native'/name,corrected/'research-native'/name,sha(CURRENT/'research-native'/name))
    # Validate the portable relative manifest with the same production stager.
    spec=importlib.util.spec_from_file_location('portable_guard',ROOT/'tools/Stage-ResearchFrozenRuntime.py')
    guard=importlib.util.module_from_spec(spec);spec.loader.exec_module(guard)
    guard.verify_frozen(DEPS/'frozen-runtime-inputs.json')
    receipt=dict(schema='HVX.R58.PortableResearchInputs/1',frozenFiles=439,currentPinnedFiles=len(newpins),
        currentPinsSha256=sha(corrected/'research-pins.json'),
        relativeFrozenManifestSha256=sha(DEPS/'frozen-runtime-inputs.json'),
        correctedZipSha256='F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F',
        copiesVerifiedBeforeAndAfter=True,SDKIncluded=False,hardwareAccessed=False,productStarted=False,
        currentCompilerInputsIncluded=True,compilerEnvironmentIncluded=False,
        compilerRequirement='CPython 3.12 x64, Cython 3.1.6, setuptools 80.9.0, MSVC x64 and Windows SDK; pass -PythonBuildInterpreter to Build-ResearchRuntime.ps1',
        maintenanceStatus='Frozen Python 3.12.14 / NumPy 2.3.5 / OpenSSL 3.5.8 reuse is not a security certification; updates remain a separately traced work item.')
    with (DEPS/'portable-input-receipt.json').open('x',encoding='utf-8') as stream:
        json.dump(receipt,stream,indent=2)
    print(json.dumps(receipt))


if __name__=='__main__':
    main()
