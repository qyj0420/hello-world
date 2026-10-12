"""E research adjudication: DAC proposals, A6-derived ownership and A4-derived echo review.

This is not A6 or DAC binary equivalence. A6 actually enables packet ownership
and competing assignments, but does NOT enable the shared A4 causal echo code.
Here the final complete-packet score is the linear research objective and the
local-margin metric; it is neither A's engineering score nor a probability. Raw supports and signed
full-band correlation substitute for A4's guarded low-band/CAL measurements.
No known position, cluster count, spatial density or wide blanking is used.
"""
from __future__ import annotations

import copy
import hashlib
import math
from collections import Counter, defaultdict
from functools import lru_cache

import numpy as np

VERSION = 'Eplus-final-pair-linear-score-ownership-r01'
REQUIRED = ('e_max_component_nodes', 'e_max_component_edges',
            'e_solver_state_budget', 'e_local_relative_margin',
            'e_global_relative_gap', 'e_max_echo_order',
            'e_echo_correlation_floor')


def _integer(value, name, minimum=0):
    if isinstance(value, (bool, np.bool_)) or not isinstance(value, (int, np.integer)) or value < minimum:
        raise ValueError(name + ' must be an integer >= ' + str(minimum))
    return int(value)


def _finite(value, name, minimum=None, maximum=None):
    if isinstance(value, (bool, np.bool_)):
        raise ValueError(name + ' must be finite numeric')
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ValueError(name + ' must be finite numeric') from exc
    if not math.isfinite(result) or minimum is not None and result < minimum or maximum is not None and result > maximum:
        raise ValueError(name + ' outside finite permitted range')
    return result


def _settings(config):
    missing = [key for key in REQUIRED if key not in config]
    if missing:
        raise ValueError('Explicit E adjudication settings missing: ' + ', '.join(missing))
    s = {key: config[key] for key in REQUIRED}
    for key in ('e_max_component_nodes', 'e_max_component_edges', 'e_solver_state_budget'):
        s[key] = _integer(s[key], key, 1)
    s['e_max_echo_order'] = _integer(s['e_max_echo_order'], 'e_max_echo_order', 0)
    for key in ('e_local_relative_margin', 'e_global_relative_gap', 'e_echo_correlation_floor'):
        s[key] = _finite(s[key], key, 0, 1)
    # Explicit limits protect recursion/graph memory even if an external profile is malformed.
    if s['e_max_component_nodes'] > 24 or s['e_max_component_edges'] > 4096 or s['e_solver_state_budget'] > 2000000:
        raise ValueError('E exact matching resource limits exceed supported research budget')
    return s


def _window(value, peak, count):
    if not isinstance(value, (list, tuple)) or len(value) != 2:
        raise ValueError('Two inclusive raw support indices required')
    left, right = (_integer(v, 'window index') for v in value)
    if not 0 <= left <= peak <= right < count:
        raise ValueError('Raw peak must be inside inclusive support within record')
    return left, right


def _packets(refs):
    """Merge overlapping representations; a non-clique overlap chain is unresolved.

    Transitive A-B/B-C overlap does not silently assert that disjoint A/C peaks
    are one real packet. The complete component is kept, flagged unresolved and
    excluded from independent ownership. Identical raw peak identity is always
    conserved, even when extraction gave unequal windows.
    """
    by_peak = defaultdict(list)
    for ref in refs:
        by_peak[ref['peak']].append(ref)
    support = []
    for peak, items in sorted(by_peak.items()):
        support.append({'peak': peak, 'left': min(r['left'] for r in items),
                        'right': max(r['right'] for r in items), 'refs': items})
    groups, active, end = [], [], -1
    for item in sorted(support, key=lambda p: (p['left'], p['right'], p['peak'])):
        if active and item['left'] > end:
            groups.append(active); active = []; end = -1
        active.append(item); end = max(end, item['right'])
    if active:
        groups.append(active)
    inventory, assignments = [], {}
    for packet_id, items in enumerate(groups):
        overlap_resolved = max(p['left'] for p in items) <= min(p['right'] for p in items)
        inventory.append({'packet_id': packet_id,
                          'raw_support': [min(p['left'] for p in items), max(p['right'] for p in items)],
                          'raw_peak_representations': sorted(p['peak'] for p in items),
                          'representation_count': sum(len(p['refs']) for p in items),
                          'overlap_identity_resolved': overlap_resolved,
                          'physical_packet_identity_confirmed': False})
        for item in items:
            for ref in item['refs']:
                assignments[(ref['event'], ref['role'])] = (packet_id, overlap_resolved)
    return inventory, assignments


def _components(edges):
    adjacency = defaultdict(list)
    for edge in edges:
        adjacency[edge['u']].append(edge); adjacency[edge['v']].append(edge)
    seen, result = set(), []
    for edge in edges:
        if edge['id'] in seen:
            continue
        stack, component = [edge], []
        while stack:
            current = stack.pop()
            if current['id'] in seen:
                continue
            seen.add(current['id']); component.append(current)
            stack.extend(n for v in (current['u'], current['v']) for n in adjacency[v] if n['id'] not in seen)
        result.append(sorted(component, key=lambda e: e['id']))
    return result


class _BudgetExceeded(Exception):
    pass


def _solve(edges, forbidden, budget, limit):
    nodes = sorted({e[v] for e in edges for v in ('u', 'v')})
    positions = {node: i for i, node in enumerate(nodes)}
    adjacency = defaultdict(list)
    for edge in edges:
        if edge['id'] == forbidden:
            continue
        u, v = positions[edge['u']], positions[edge['v']]
        adjacency[u].append((v, edge)); adjacency[v].append((u, edge))

    @lru_cache(None)
    def search(mask):
        budget[0] += 1
        if budget[0] > limit:
            raise _BudgetExceeded
        if mask == 0:
            return 0.0, ()
        candidates = [u for u in range(len(nodes)) if mask & (1 << u)]
        u = max(candidates, key=lambda k: sum(bool(mask & (1 << v)) for v, _ in adjacency[k]))
        remaining = mask & ~(1 << u)
        best_weight, best_ids = search(remaining)
        for v, edge in adjacency[u]:
            if not remaining & (1 << v):
                continue
            weight, ids = search(remaining & ~(1 << v))
            weight += edge['weight']; ids = tuple(sorted(ids + (edge['id'],)))
            if weight > best_weight + 1e-12 or abs(weight - best_weight) <= 1e-12 and ids < best_ids:
                best_weight, best_ids = weight, ids
        return best_weight, best_ids

    return search((1 << len(nodes)) - 1)


def _signed_shape(y, left_event, right_event, role):
    a, b = left_event[role], right_event[role]
    aw, bw = left_event[role + '_window'], right_event[role + '_window']
    lo = max(aw[0] - a, bw[0] - b); hi = min(aw[1] - a, bw[1] - b)
    if hi - lo + 1 < 5 or y[a] * y[b] <= 0:
        return None
    first = y[a + lo:a + hi + 1]; second = y[b + lo:b + hi + 1]
    raw_scale_a, raw_scale_b = np.max(np.abs(first)), np.max(np.abs(second))
    if raw_scale_a == 0 or raw_scale_b == 0:
        return None
    first, second = first / raw_scale_a, second / raw_scale_b
    first = first - np.mean(first); second = second - np.mean(second)
    # Scaling avoids overflow for valid large finite waveform amplitudes.
    scale_a, scale_b = np.max(np.abs(first)), np.max(np.abs(second))
    if scale_a == 0 or scale_b == 0:
        return None
    first, second = first / scale_a, second / scale_b
    den = float(np.linalg.norm(first) * np.linalg.norm(second))
    return float(np.clip(np.dot(first, second) / den, -1, 1)) if den > 0 else None


def _echo_review(events, selected, y, D, settings):
    """Shared-A4-derived complete-pair causal review, using explicit research substitutes."""
    ordered = sorted(selected, key=lambda i: (events[i]['incident'], events[i]['reflection'], i))
    chains, strong, ambiguous = [], [], set()
    for offset, source_id in enumerate(ordered):
        source = events[source_id]
        for later_id in ordered[offset + 1:]:
            later = events[later_id]
            if source['reflection_window'][1] >= later['incident_window'][0]:
                continue
            shifts = [later[r] - source[r] for r in ('incident', 'reflection')]
            orders = [int(round(shift / D)) for shift in shifts]
            if orders[0] != orders[1] or not 1 <= orders[0] <= settings['e_max_echo_order']:
                continue
            widths = [event[r + '_window'][1] - event[r + '_window'][0] + 1
                      for event in (source, later) for r in ('incident', 'reflection')]
            tolerance = max(2, min(10, int(round(min(widths) / 3))))
            if any(abs(shift - orders[0] * D) > tolerance for shift in shifts):
                continue
            chain = {'source_event_id': source_id, 'later_event_id': later_id,
                     'order': orders[0], 'timing_tolerance_samples': tolerance,
                     'state': 'evidence_unavailable', 'reason': 'raw_shape_or_snr_unavailable',
                     'engineering_echo_hypothesis': False, 'physical_identity_confirmed': False}
            correlations = [_signed_shape(y, source, later, role) for role in ('incident', 'reflection')]
            chain['signed_raw_correlations'] = correlations
            amplitudes = [[abs(float(y[event[role]])) for role in ('incident', 'reflection')]
                          for event in (source, later)]
            sigmas = [event['noise_sigma_pc'] for event in (source, later)]
            tiny = max(np.finfo(float).tiny, np.finfo(float).eps * max(max(a) for a in amplitudes))
            resolved_sigmas = [max(sigma, tiny) for sigma in sigmas]
            snrs = [a / sigma for amps, sigma in zip(amplitudes, resolved_sigmas) for a in amps]
            chain['minimum_snr'] = min(snrs)
            if any(c is None or c < settings['e_echo_correlation_floor'] for c in correlations) or min(snrs) < 8:
                chains.append(chain); continue
            noise = 2 * math.hypot(*resolved_sigmas)
            ratios = [b / a if a > 0 else math.inf for a, b in zip(*amplitudes)]
            decays = [a - b > noise for a, b in zip(*amplitudes)]
            chain.update(amplitude_ratios=ratios, noise_resolved_decay=decays,
                         decay_noise_allowance_pc=noise)
            if not any(decays):
                chain.update(state='not_echo', reason='no_noise_resolved_two_endpoint_decay')
                chains.append(chain); continue
            difference = abs(20 * math.log10(ratios[0] / ratios[1])) if min(ratios) > 0 else math.inf
            allowance = 3 + 20 * math.log10(1 + noise / max(amplitudes[1][0], tiny)
                                           + noise / max(amplitudes[1][1], tiny))
            chain.update(ratio_difference_db=difference, allowed_ratio_difference_db=allowance)
            if not all(decays) or difference > allowance:
                chain.update(state='review', reason='two_endpoint_decay_evidence_ambiguous')
                ambiguous.update((source_id, later_id)); chains.append(chain); continue
            chain.update(state='conditional', reason='complete_pair_same_order_noise_resolved_consistent_decay')
            chains.append(chain); strong.append(chain)
    incoming = defaultdict(list)
    for chain in strong:
        incoming[chain['later_event_id']].append(chain)
    roots = {}
    for event_id in ordered:
        roots[event_id] = ({event_id} if not incoming[event_id] else
                           set().union(*(roots[c['source_event_id']] for c in incoming[event_id])))
    canonical = []
    for chain in strong:
        source_id, later_id = chain['source_event_id'], chain['later_event_id']
        if len(roots[source_id]) != 1 or len(roots[later_id]) != 1:
            chain.update(state='review', reason='multiple_causal_source_explanations')
            ambiguous.update((source_id, later_id)); continue
        root = next(iter(roots[source_id]))
        if root != source_id:
            chain.update(state='derived_subchain', reason='later_echo_cannot_restart_order_count'); continue
        canonical.append(chain)
    # Any ambiguity in a causal connected episode withholds the whole episode;
    # no weaker/new source is promoted after removing the ambiguous explanation.
    changed = True
    while changed:
        changed = False
        for chain in canonical:
            source_id, later_id = chain['source_event_id'], chain['later_event_id']
            if source_id in ambiguous or later_id in ambiguous:
                previous = len(ambiguous); ambiguous.update((source_id, later_id))
                changed |= len(ambiguous) != previous
    echoes = {}
    for chain in canonical:
        source_id, later_id = chain['source_event_id'], chain['later_event_id']
        if source_id in ambiguous or later_id in ambiguous:
            chain.update(state='review', reason='competing_or_ambiguous_causal_episode'); continue
        chain.update(state='causal_echo', engineering_echo_hypothesis=True,
                     reason='unique_earliest_source_complete_pair_consistent_decay')
        echoes[later_id] = source_id
    return chains, echoes, ambiguous


def adjudicate(events, y, fs, D, config):
    """Return every input candidate annotated, plus exact ownership/echo diagnostics.

    Independent graph decisions and echo exclusions are engineering suggestions;
    no event is physically confirmed. Malformed candidates remain explicit review
    rows. Nonfinite raw data, invalid global geometry or configuration raise.
    The caller must mark candidate-discovery budget exhaustion separately.
    """
    settings = _settings(config)
    raw = np.asarray(y, dtype=np.float64)
    if raw.ndim != 1 or len(raw) < 16 or not np.isfinite(raw).all():
        raise ValueError('Finite one-dimensional raw waveform with >=16 samples required')
    _finite(fs, 'fs', 1e3, 5e9); D = _integer(D, 'D', 1)
    if D >= len(raw):
        raise ValueError('Round trip must fit inside waveform record')
    before_hash = hashlib.sha256(raw.tobytes()).hexdigest()
    annotated, refs, valid = [], [], []
    for event_id, original in enumerate(events):
        event = copy.deepcopy(original)
        event.update(e_event_id=event_id, e_decision='review', engineering_eligible=False,
                     physical_identity_confirmed=False, e_reasons=[],
                     incident_packet_id=None, reflection_packet_id=None)
        annotated.append(event)
        try:
            incident = _integer(event['incident'], 'incident'); reflection = _integer(event['reflection'], 'reflection')
            if not 0 <= incident < reflection < len(raw) or reflection - incident > D:
                raise ValueError('Ordered nonself raw pair must fit cable round trip')
            ratio = _finite(event['matching_ratio'], 'matching_ratio', np.finfo(float).tiny)
            _finite(event['x_raw_m'], 'x_raw_m'); _finite(event['q_pc'], 'q_pc')
            event['noise_sigma_pc'] = _finite(event['noise_sigma_pc'], 'noise_sigma_pc', 0)
            windows = [_window(event[role + '_window'], event[role], len(raw)) for role in ('incident', 'reflection')]
            if windows[0][1] >= windows[1][0]:
                raise ValueError('Incident/reflection raw supports overlap')
            for role, window in zip(('incident', 'reflection'), windows):
                refs.append({'event': event_id, 'role': role, 'peak': event[role], 'left': window[0], 'right': window[1]})
            score = _finite(event['final_pair_score']['score'], 'final_pair_score', np.finfo(float).tiny)
            if score > 1 or event['final_pair_score'].get('state') != 'measured':
                raise ValueError('Finite measured final-pair score in (0,1] required')
            event['e_objective_weight'] = score
            valid.append(event_id)
        except (KeyError, ValueError, TypeError, OverflowError) as exc:
            event['e_reasons'].append('invalid_candidate:' + str(exc))
    packets, assignments = _packets(refs)
    candidates = []
    for event_id in valid:
        event = annotated[event_id]
        u, ru = assignments[(event_id, 'incident')]; v, rv = assignments[(event_id, 'reflection')]
        event.update(incident_packet_id=u, reflection_packet_id=v)
        if not ru or not rv or u == v:
            event['e_reasons'].append('overlap_packet_identity_unresolved'); continue
        candidates.append({'id': event_id, 'u': u, 'v': v, 'weight': event['e_objective_weight']})
    by_identity = defaultdict(list)
    for edge in candidates:
        by_identity[(edge['u'], edge['v'])].append(edge)
    edges = []
    for representations in by_identity.values():
        winner = max(representations, key=lambda e: (e['weight'], -e['id']))
        edges.append(winner)
        for edge in representations:
            if edge is not winner:
                annotated[edge['id']].update(e_decision='duplicate_representation', duplicate_of_event_id=winner['id'])
                annotated[edge['id']]['e_reasons'].append('same_raw_packet_pair_already_represented')
    local = defaultdict(list)
    for edge in edges:
        local[edge['u']].append(edge)
    local_ambiguous = set()
    for incident_id, choices in local.items():
        ranked = sorted(choices, key=lambda e: (-annotated[e['id']]['e_objective_weight'], e['id']))
        if len(ranked) > 1:
            top, second = (float(annotated[e['id']]['e_objective_weight']) for e in ranked[:2])
            relative_gap = (top - second) / top
            for edge in choices:
                annotated[edge['id']]['e_local_relative_gap'] = relative_gap
            if relative_gap <= settings['e_local_relative_margin'] + 1e-12:
                local_ambiguous.add(incident_id)
    diagnostics_components, selected = [], []
    for component_id, component in enumerate(_components(edges)):
        nodes = {e[v] for e in component for v in ('u', 'v')}
        receipt = {'component_id': component_id, 'node_count': len(nodes), 'edge_count': len(component),
                   'event_ids': [e['id'] for e in component], 'status': 'review', 'exact': False,
                   'solver_states': 0, 'selected_event_ids': [], 'alternative_edge_solves': []}
        diagnostics_components.append(receipt)
        reason = None
        if len(nodes) > settings['e_max_component_nodes'] or len(component) > settings['e_max_component_edges']:
            reason = 'component_size_budget_incomplete_no_prefix_acceptance'
        elif any(e['u'] in local_ambiguous for e in component):
            reason = 'local_competing_reflections_within_relative_margin'
        else:
            budget = [0]
            try:
                best_weight, best = _solve(component, None, budget, settings['e_solver_state_budget'])
                receipt.update(best_weight=best_weight, provisional_best_event_ids=list(best))
                unstable = False
                for event_id in best:
                    weight, alternate = _solve(component, event_id, budget, settings['e_solver_state_budget'])
                    gap = max(0, best_weight - weight) / best_weight if best_weight > 0 else 0
                    receipt['alternative_edge_solves'].append({'forbidden_event_id': event_id,
                        'weight': weight, 'relative_gap': gap, 'event_ids': list(alternate)})
                    if gap <= settings['e_global_relative_gap'] + 1e-12:
                        unstable = True
                receipt['exact'] = True
                if unstable or not best:
                    reason = 'near_equal_global_alternative_no_weaker_promotion'
                else:
                    selected.extend(best); receipt.update(status='global_unique', selected_event_ids=list(best))
            except _BudgetExceeded:
                reason = 'exact_solver_budget_incomplete_no_prefix_acceptance'
            receipt['solver_states'] = budget[0]
        receipt['reason'] = reason or 'exact_unique_packet_ownership_engineering_only'
        for edge in component:
            event = annotated[edge['id']]; event['e_component_id'] = component_id
            if reason:
                event['e_reasons'].append(reason)
            elif edge['id'] in receipt['selected_event_ids']:
                event.update(e_decision='global_unique', engineering_eligible=True)
            else:
                event['e_reasons'].append('not_selected_in_unique_packet_ownership')
    chains, echoes, ambiguous = _echo_review(annotated, selected, raw, D, settings)
    for event_id in ambiguous:
        annotated[event_id].update(e_decision='review', engineering_eligible=False)
        annotated[event_id]['e_reasons'].append('causal_echo_episode_ambiguous_no_source_promotion')
    for event_id, source_id in echoes.items():
        annotated[event_id].update(e_decision='causal_echo', engineering_eligible=False,
                                  causal_source_event_id=source_id, engineering_echo_hypothesis=True)
        annotated[event_id]['e_reasons'].append('unique_complete_pair_repetition_engineering_echo_only')
    ownership = []
    for event in annotated:
        if event['engineering_eligible']:
            ownership.extend((event['incident_packet_id'], event['reflection_packet_id']))
    if len(ownership) != len(set(ownership)):
        raise RuntimeError('E independent candidate ownership reused a raw packet')
    after_hash = hashlib.sha256(raw.tobytes()).hexdigest()
    if before_hash != after_hash:
        raise RuntimeError('E adjudication mutated original waveform')
    diagnostics = {'version': VERSION, 'input_candidates': len(annotated), 'config': settings,
        'packet_inventory': packets, 'components': diagnostics_components, 'causal_echo_review': chains,
        'counts': dict(Counter(e['e_decision'] for e in annotated)),
        'independent_eligible_events': sum(e['engineering_eligible'] for e in annotated),
        'raw_sha256_before': before_hash, 'raw_sha256_after': after_hash, 'raw_unchanged': True,
        'all_input_candidates_retained': True, 'wide_interval_blanking_applied': False,
        'objective': 'sum(final_pair_score.score); local margin compares linear final scores; research weight not probability',
        'source_scope': {'A6_actual': 'AstraSixthMatching packet ownership/alternative solves; AstraLocalReflectionAmbiguity distinct packet competitors',
                         'shared_A4_not_A6_enabled': 'AstraCausalEchoes complete-pair repetition and consistent noise-resolved decay'},
        'physical_identity_confirmed': False, 'physical_accuracy_verified': False,
        'known_location_used': False, 'spatial_density_used': False,
        'limitations': ['Full-band raw shape/noise and width-derived tolerance replace A4 guarded low-band/CAL evidence; not equivalent.',
                        'Independent periodic decaying discharges can mimic a two-endpoint echo; engineering hypothesis is not physical proof.',
                        'Candidate discovery completeness must be audited by caller; exact ownership does not prove physical accuracy.']}
    return annotated, diagnostics
