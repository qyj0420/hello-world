"""Deterministic native-input adaptations; immutable upstream snapshots stay intact."""
from pathlib import Path
import ast
import hashlib
import json

ROOT = Path(__file__).resolve().parent


def source_inputs():
    manifest=json.loads((ROOT/'source-manifest.json').read_text('utf-8'))
    for name, digest in manifest['vendoredFiles'].items():
        if hashlib.sha256((ROOT/name).read_bytes()).hexdigest() != digest:
            raise ValueError('E/E+ frozen source differs: '+name)
    def text(name):
        return (ROOT/'Upstream'/name).read_text('utf-8')
    core=text('dac_core.py')
    # Do not compile filesystem readers or the old research statistics stage.
    tree=ast.parse(core)
    removed=set()
    for node in tree.body:
        if isinstance(node,(ast.FunctionDef,ast.AsyncFunctionDef)) and node.name in ('sha','read_loc','apply_statistics'):
            removed.update(range(node.lineno,node.end_lineno+1))
        if isinstance(node,ast.ImportFrom) and node.module=='dac_recovered.cluster':
            removed.update(range(node.lineno,node.end_lineno+1))
    core='\n'.join(line for number,line in enumerate(core.splitlines(),1) if number not in removed)+'\n'
    plus=text('eplus_research.py'); node=next(n for n in ast.parse(plus).body if isinstance(n,ast.FunctionDef) and n.name=='apply_a_interval_suppression')
    plus='import copy\nimport math\n'+ '\n'.join(plus.splitlines()[node.lineno-1:node.end_lineno])+'\n'
    inputs={'q74':(ROOT/'numpy_compat.py').read_text('utf-8'),
            'q75':text('dac_recovered/filters.py'), 'q76':text('dac_recovered/reflection.py'),
            'q77':text('task17_dac.py'), 'q78':text('e_adjudication.py'),
            'q79':core, 'q80':plus}
    # E stays frozen. The named packet/20% candidate is private to E+.
    packet_root=ROOT.parent/'LocationEPlusPacket'
    packet_manifest=json.loads((packet_root/'source-manifest.json').read_text('utf-8'))
    for name,digest in packet_manifest['vendoredFiles'].items():
        if hashlib.sha256((packet_root/name).read_bytes()).hexdigest()!=digest:
            raise ValueError('E+ packet source differs: '+name)
    packet_core=(packet_root/'Upstream/eplus_core.py').read_text('utf-8')
    packet_tree=ast.parse(packet_core); packet_removed=set()
    for node in packet_tree.body:
        if isinstance(node,(ast.FunctionDef,ast.AsyncFunctionDef)) and node.name in ('sha','read_loc','apply_statistics'):
            packet_removed.update(range(node.lineno,node.end_lineno+1))
        if isinstance(node,ast.ImportFrom) and node.module=='dac_recovered.cluster':
            packet_removed.update(range(node.lineno,node.end_lineno+1))
    packet_core='\n'.join(line for number,line in enumerate(packet_core.splitlines(),1) if number not in packet_removed)+'\n'
    # Dependency order also permits the independent interpreted QA loader.
    inputs.update({'q82':(packet_root/'Upstream/a_packet_guard.py').read_text('utf-8'),
                   'q83':(packet_root/'Upstream/sparse_packets.py').read_text('utf-8'),
                   'q81':packet_core})
    score_root=ROOT.parent/'LocationEPlusFinalScore'
    score_manifest=json.loads((score_root/'source-manifest.json').read_text('utf-8'))
    for name,digest in score_manifest['vendoredFiles'].items():
        if hashlib.sha256((score_root/name).read_bytes()).hexdigest()!=digest:
            raise ValueError('E+ final-score source differs: '+name)
    inputs.update({'q84':(score_root/'Upstream/pair_score.py').read_text('utf-8'),
                   'q85':(score_root/'Upstream/score_adjudication.py').read_text('utf-8')})
    replacements={'from scipy.ndimage import maximum_filter1d':'from q74 import maximum_filter1d',
                  'from scipy.signal import correlate, find_peaks':'from q74 import correlate, find_peaks',
                  'from .filters import automatic_threshold':'from q75 import automatic_threshold',
                  'from dac_recovered.filters import automatic_threshold':'from q75 import automatic_threshold',
                  'from dac_recovered.reflection import':'from q76 import',
                  'from task17_dac import endpoint_analysis':'from q77 import endpoint_analysis',
                  'from e_adjudication import adjudicate':'from q78 import adjudicate',
                  'from a_packet_guard import':'from q82 import',
                  'from sparse_packets import':'from q83 import'}
    for name,code in inputs.items():
        for before,after in replacements.items(): code=code.replace(before,after)
        if 'scipy' in code and name != 'q74':
            if any(isinstance(n,ast.ImportFrom) and n.module.startswith('scipy') for n in ast.walk(ast.parse(code))):
                raise ValueError('Unexpected SciPy dependency')
        inputs[name]=code
    # The user's corrected composition is the actual E+ entry, not a version
    # label attached to the older local scorer. Keep the imported source intact;
    # only replace filesystem dependency discovery with fixed native bindings.
    corrected=ROOT.parent.parent/'provenance/eplus-corrected-20261012-r01/package'
    api_path=corrected/'eplus_api.py'
    profile_path=corrected/'source/current/Eplus/profile.json'
    expected={'eplus_api.py':'BA72FF16EF6FCEF6842CF6A4D64C800DD644DB2A5B0FB3B5EDA9778D656FB312',
              'source/current/Eplus/profile.json':'AD4FA55037326ACF49CE6633ABF212A1A63636882811E670E7AF77D59A4D7A63'}
    for relative,digest in expected.items():
        if hashlib.sha256((corrected/relative).read_bytes()).hexdigest().upper()!=digest:
            raise ValueError('Corrected E+ composition source differs: '+relative)
    original=api_path.read_text('utf-8-sig')
    parsed=ast.parse(original)
    analyze=next(n for n in parsed.body if isinstance(n,ast.FunctionDef) and n.name=='analyze_wave')
    version=next(n.value.value for n in parsed.body if isinstance(n,ast.Assign)
                 and any(isinstance(t,ast.Name) and t.id=='VERSION' for t in n.targets))
    profile=json.loads(profile_path.read_text('utf-8-sig'))
    inputs['q86']=('import copy\nimport hashlib\nimport numpy as np\n'
        'import q81, q84, q85, q80\nVERSION='+repr(version)+'\n'
        'SOURCE_IDENTITY="F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F"\n'
        'def _load_dependencies():\n'
        '    return {"eplus_core":q81,"pair_score":q84,"score_adjudication":q85,"eplus_research":q80}\n'
        'def default_config():\n    return copy.deepcopy('+repr(profile)+')\n'+
        '\n'.join(original.splitlines()[analyze.lineno-1:analyze.end_lineno])+'\n')
    return inputs
