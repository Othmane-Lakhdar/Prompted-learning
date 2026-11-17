"""A005 schema/hash/selection/coverage/DDL design cases only; no compiler or extractor runs."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dependency-path', required=True)
parser.add_argument('--report')
args = parser.parse_args()
sys.path.insert(0, args.dependency_path)
from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError

HERE = Path(__file__).resolve().parent
CONTRACT = json.loads((HERE / 'contracts.json').read_text(encoding='utf8'))
CASES = json.loads((HERE / 'a005-cases.json').read_text(encoding='utf8'))
POLICY = CONTRACT['x-choose-cfg-policy']
checks, negatives = [], []


def require(value, reason):
    if not value:
        raise ValueError(reason)


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf8')


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def schema(name, value):
    Draft202012Validator({'$schema': CONTRACT['$schema'], '$defs': CONTRACT['$defs'], '$ref': '#/$defs/' + name}).validate(value)


def check(name, action):
    action()
    checks.append(name)


def negative(name, action):
    try:
        action()
    except (ValueError, ValidationError, sqlite3.IntegrityError):
        negatives.append(name)
        return
    raise AssertionError('Expected rejection: ' + name)


def native_fingerprint(request):
    recipe = CONTRACT['x-worker-fingerprints']['semantic_input_fingerprint_v2' if 'analysis_selection' in request else 'semantic_input_fingerprint']
    selected = {key: copy.deepcopy(request[key]) for key in recipe['properties']}
    for file in selected['files']:
        for key in recipe['files_exclude']:
            file.pop(key)
    for binding in selected['path_bindings']:
        for key in recipe['path_bindings_exclude']:
            binding.pop(key)
    return digest([recipe['domain'], selected])


def validate_native(request):
    schema('native-worker-request', request)
    require(request['semantic_input_fingerprint'] == native_fingerprint(request), 'feature/selection semantic fingerprint mismatch')
    require(request['request_fingerprint'] == digest({k: v for k, v in request.items() if k != 'request_fingerprint'}), 'request fingerprint mismatch')


def managed_fingerprint(components):
    recipe = CONTRACT['x-managed-fingerprints']['v2' if 'analysis_selection' in components else 'v1']
    require(set(components) == set(recipe['properties']), 'managed fingerprint fields differ')
    return digest([recipe['domain'], components])


def validate_managed(vector):
    components = vector['components']
    schema('managed-analysis-features', {k: components[k] for k in ('requested_features', 'analysis_selection') if k in components})
    require(managed_fingerprint(components) == vector['semantic_input_fingerprint'], 'managed feature/selection fingerprint mismatch')


def coverage(model, features, selection):
    schema('choose-analysis-selection', selection)
    bodies = model['bodies']
    selected = [body for body in bodies if body['selector'] == selection['selectors'][0]]
    require(len(selected) == 1, 'selection requires exactly one matching compiler identity/signature')
    chosen = selected[0]
    for body in bodies:
        require(body['source_status'] == 'complete', 'coverage fixture expects independent complete source facts')
        require(body['reason'] is None or body['reason'] in POLICY['reason_codes'], 'unregistered flow outcome reason')
        if body is not chosen:
            require(body['flow_status'] == 'not_attempted' and body['reason'] == 'not_selected', 'nonselected body falsely analyzed')
            require(body['entry'] is None and body['exit'] is None and not any(body[k] for k in ('blocks', 'edges', 'events')), 'nonselected graph fabricated payload')
        else:
            require(body['flow_status'] in ('complete', 'partial', 'failed'), 'selected body has no attempted outcome')
            if body['flow_status'] == 'complete':
                require(body['reason'] is None and body['entry'] and body['exit'] and body['blocks'], 'complete selected graph lacks required structure')
            else:
                require(body['reason'] is not None, 'incomplete selected graph lacks reason')
        if 'storage_events' not in features:
            require(not body['events'], 'unrequested storage events produced')
    expected_rows = {(file, feature) for file in model['files'] for feature in ('control_flow', 'storage_events')}
    actual_rows = [(row['file_key'], row['feature']) for row in model['file_coverage']]
    require(len(actual_rows) == len(expected_rows) and set(actual_rows) == expected_rows, 'file coverage missing/duplicate')
    selected_failure = chosen['flow_status'] != 'complete'
    for row in model['file_coverage']:
        local_bodies = [body for body in bodies if body['file_key'] == row['file_key']]
        if row['feature'] == 'storage_events' and 'storage_events' not in features:
            expected = ('not_attempted', 'not_requested')
        elif selected_failure and chosen in local_bodies:
            expected = ('partial', 'selected_body_' + chosen['flow_status'])
        elif any(body is not chosen for body in local_bodies):
            expected = ('partial', 'not_selected_bodies')
        elif not local_bodies:
            expected = ('not_attempted', 'no_selected_body')
        else:
            expected = ('complete', None)
        require((row['status'], row['reason']) == expected, 'file analysis coverage hides selection or selected failure')
    expected_reason = 'selected_body_' + chosen['flow_status'] if selected_failure else 'not_selected_bodies'
    require(model['status'] == 'partial' and model['reason'] == expected_reason, 'aggregate status exceeds selection coverage')


def changed(value, action):
    clone = copy.deepcopy(value)
    action(clone)
    return clone


check('schemas_are_valid_draft_2020_12', lambda: Draft202012Validator.check_schema(CONTRACT))
check('utf8_lf_contract_inputs', lambda: require(all(b'\r' not in (HERE / name).read_bytes() for name in ('contracts.json', 'contracts.md', 'schema.sql', 'field-dictionary.json', 'a005-cases.json')), 'CRLF contract drift'))
prior = CASES['prior_source_only']
check('native_source_v1_shape_and_exact_prior_hashes', lambda: validate_native(prior['native_source_request']))
check('native_source_prior_constants_unchanged', lambda: require(prior['native_semantic_hash'] == 'ebf3f6c8fbf90c7cacade52a30d02d175f0cb2006e30c30343181ab7beac09ce'
    and prior['native_request_hash'] == '679aa33fc9ed19054374fa35c364d553beffe8705f4326fa3bfed9daa05f486e', 'prior /1 hash was rewritten'))
check('managed_source_v1_exact_vector', lambda: validate_managed(CASES['managed_source_vector']))
check('managed_source_prior_constant_unchanged', lambda: require(CASES['managed_source_vector']['semantic_input_fingerprint'] == '6984228fce1598b3c16c86046e6184c0324370331c2ca1ae12a87be0b43becbc', 'managed /1 hash vector was rewritten'))
prior_contract = json.loads(subprocess.run(['git', 'show', '4f1e2659be63000a1a35fe0582cae5a39e5d0a6c:docs/m0/contracts.json'], cwd=HERE.parent.parent, check=True, capture_output=True).stdout)
check('native_v1_recipe_unchanged_from_published_contract', lambda: require(CONTRACT['x-worker-fingerprints']['semantic_input_fingerprint'] == prior_contract['x-worker-fingerprints']['semantic_input_fingerprint'], 'source-only /1 recipe drift'))
check('source_common_and_native_result_shapes_unchanged', lambda: require(all(CONTRACT['$defs'][name] == prior_contract['$defs'][name] for name in ('native-worker-result', 'semantic-facts', 'semantic-unit-result', 'semantic-entity', 'compiler-type-key', 'compiler-signature-key')), 'frozen source/result shape changed'))
check('original_native_request_fields_unchanged', lambda: require(CONTRACT['$defs']['native-worker-request']['required'] == prior_contract['$defs']['native-worker-request']['required']
    and {k: v for k, v in CONTRACT['$defs']['native-worker-request']['properties'].items() if k != 'analysis_selection'} == prior_contract['$defs']['native-worker-request']['properties'], 'original native request fields drift'))
for index, request in enumerate(CASES['native_flow_vectors']):
    check('native_v2_features_' + str(index), lambda request=request: validate_native(request))
for index, vector in enumerate(CASES['managed_flow_vectors']):
    check('managed_v2_features_' + str(index), lambda vector=vector: validate_managed(vector))
for language, selection in CASES['selection_by_language'].items():
    check(language + '_exact_observed_selector', lambda selection=selection: schema('choose-analysis-selection', selection))
    wrapper = {'schema': 'compiler-flow-analysis-settings/1', 'family': 'control_flow', 'policy': 'compiler_cfg_v1', 'requested_features': ['source_semantics', 'cfg', 'storage_events'], 'analysis_selection': selection}
    check(language + '_persisted_settings_shape', lambda wrapper=wrapper: schema('compiler-flow-analysis-settings', wrapper))
    negative(language + '_event_dump_in_settings', lambda wrapper=wrapper: schema('compiler-flow-analysis-settings', dict(wrapper, events=[])))
    for label, mutation in [('unknown_selector', lambda x: x['selectors'][0].update(compiler_key='Choose')),
        ('name_only', lambda x: x['selectors'][0].pop('compiler_signature_key')),
        ('wrong_signature', lambda x: x['selectors'][0]['compiler_signature_key'].update(generic_arity=1)),
        ('wrong_kind', lambda x: x['selectors'][0].update(compiler_identity_kind='source_local')),
        ('empty_selectors', lambda x: x.update(selectors=[])),
        ('multiple_selectors', lambda x: x['selectors'].append(copy.deepcopy(x['selectors'][0]))),
        ('unknown_setting', lambda x: x['settings'].update(assume_pure=True)),
        ('changed_limit', lambda x: x['settings']['limits'].update(max_cfg_blocks=65)),
        ('event_policy_change', lambda x: x['settings'].update(event_policy='infer_everything'))]:
        negative(language + '_' + label, lambda mutation=mutation, selection=selection: schema('choose-analysis-selection', changed(selection, mutation)))
source = prior['native_source_request']
negative('source_only_selection_forbidden', lambda: schema('native-worker-request', dict(source, analysis_selection=CASES['selection_by_language']['cpp'])))
negative('source_only_null_selection_forbidden', lambda: schema('native-worker-request', dict(source, analysis_selection=None)))
flow = CASES['native_flow_vectors'][1]
negative('cfg_without_selection', lambda: schema('native-worker-request', {k: v for k, v in flow.items() if k != 'analysis_selection'}))
negative('native_cannot_select_managed_body', lambda: schema('native-worker-request', dict(flow, analysis_selection=CASES['selection_by_language']['csharp'])))
negative('events_without_cfg', lambda: schema('native-worker-request', dict(flow, requested_features=['source_semantics', 'storage_events'])))
negative('managed_source_selection_forbidden', lambda: schema('managed-analysis-features', {'requested_features': ['source_semantics'], 'analysis_selection': selection}))
negative('managed_source_null_selection_forbidden', lambda: schema('managed-analysis-features', {'requested_features': ['source_semantics'], 'analysis_selection': None}))
negative('valid_feature_change_without_rehash', lambda: validate_native(dict(flow, requested_features=['source_semantics', 'cfg'])))
negative('managed_valid_feature_change_without_rehash', lambda: validate_managed(changed(CASES['managed_flow_vectors'][1], lambda x: x['components'].update(requested_features=['source_semantics', 'cfg']))))
negative('native_options_changed', lambda: schema('native-worker-request', changed(flow, lambda x: x['analysis_selection']['settings']['compiler_options'].update(PruneTriviallyFalseEdges=True))))

model = CASES['coverage_only_model']; selection = CASES['selection_by_language']['csharp']; features = ['source_semantics', 'cfg', 'storage_events']
check('selected_and_unselected_body_file_coverage', lambda: coverage(model, features, selection))
cfg_only = copy.deepcopy(model)
for body in cfg_only['bodies']:
    body['events'] = []
for row in cfg_only['file_coverage']:
    if row['feature'] == 'storage_events':
        row.update(status='not_attempted', reason='not_requested')
check('cfg_only_leaves_events_unrequested', lambda: coverage(cfg_only, ['source_semantics', 'cfg'], selection))
failed = copy.deepcopy(model); failed['bodies'][0].update(flow_status='partial', reason='cfg_correspondence_missing'); failed['reason'] = 'selected_body_partial'
for row in failed['file_coverage']:
    if row['file_key'] == 'file:main':
        row.update(status='partial', reason='selected_body_partial')
check('selected_failure_not_hidden_by_expected_partial', lambda: coverage(failed, features, selection))
for name, mutation in [('zero_matches', lambda x: x['bodies'].pop(0)),
    ('multiple_matches', lambda x: x['bodies'].append(copy.deepcopy(x['bodies'][0]))),
    ('name_matching_ignores_signature', lambda x: x['bodies'][0]['selector']['compiler_signature_key'].update(qualifiers=[])),
    ('aggregate_false_complete', lambda x: x.update(status='complete', reason=None)),
    ('unselected_has_blocks', lambda x: x['bodies'][1]['blocks'].append('block:invented')),
    ('unselected_has_entry', lambda x: x['bodies'][1].update(entry='block:invented')),
    ('unselected_claims_complete', lambda x: x['bodies'][1].update(flow_status='complete', reason=None)),
    ('unselected_file_false_complete', lambda x: x['file_coverage'][2].update(status='complete', reason=None)),
    ('header_false_complete', lambda x: x['file_coverage'][-1].update(status='complete', reason=None)),
    ('missing_file_coverage', lambda x: x['file_coverage'].pop()),
    ('unregistered_selected_reason', lambda x: x['bodies'][0].update(flow_status='partial', reason='unregistered_reason'))]:
    negative(name, lambda mutation=mutation: coverage(changed(model, mutation), features, selection))
negative('selected_failure_hidden', lambda: coverage(dict(failed, reason='not_selected_bodies'), features, selection))
negative('cfg_only_produces_events', lambda: coverage(model, ['source_semantics', 'cfg'], selection))

def limits(graphs, base_records, maximum_records):
    bound = POLICY['limits']
    require(len(graphs) <= bound['max_selected_bodies'], 'selected-body limit')
    total = base_records
    for graph in graphs:
        members = sum(len(block['operations']) for block in graph['blocks'])
        events = sum(len(block['storage_events']) for block in graph['blocks'])
        counts = {'max_cfg_blocks': len(graph['blocks']), 'max_cfg_edges': len(graph['edges']), 'max_cfg_members': members, 'max_storage_events': events}
        require(all(value <= bound[name] for name, value in counts.items()), 'per-body flow limit')
        total += sum(counts.values())
    require(total <= maximum_records, 'nested CFG data exceeds global record budget')
boundary = {'blocks': [{'operations': [{}] * 8, 'storage_events': [{}] * 2} for _ in range(64)], 'edges': [{}] * 128}
check('flow_limits_exact_boundary_includes_nested_records', lambda: limits([boundary], 100, 932))
negative('nested_cfg_rows_cannot_evade_total_budget', lambda: limits([boundary], 100, 931))
negative('selected_body_limit', lambda: limits([boundary, boundary], 0, 10000))
negative('cfg_block_limit', lambda: limits([dict(boundary, blocks=boundary['blocks'] + [{'operations': [], 'storage_events': []}])], 0, 10000))
negative('cfg_edge_limit', lambda: limits([dict(boundary, edges=boundary['edges'] + [{}])], 0, 10000))
negative('cfg_member_limit', lambda: limits([changed(boundary, lambda x: x['blocks'][0]['operations'].append({}))], 0, 10000))
negative('storage_event_limit', lambda: limits([changed(boundary, lambda x: x['blocks'][0]['storage_events'].append({}))], 0, 10000))
def correspondence(language, member):
    require(member['correspondence'] == ('clang_ast_pointer' if language == 'cpp' else 'roslyn_unique_source'), 'unsupported correspondence for this compiler')
    require(member['phase'] in ('evaluation', 'branch_value', 'terminator', 'initializer') and member['compiler_element_kind'].strip(), 'invalid typed member phase/kind')
check('native_member_pointer_policy', lambda: correspondence('cpp', {'correspondence': 'clang_ast_pointer', 'phase': 'evaluation', 'compiler_element_kind': 'Statement'}))
check('managed_member_unique_source_policy', lambda: correspondence('csharp', {'correspondence': 'roslyn_unique_source', 'phase': 'branch_value', 'compiler_element_kind': 'Binary'}))
negative('explicit_lowered_not_supported_by_pilot', lambda: correspondence('cpp', {'correspondence': 'explicit_lowered', 'phase': 'evaluation', 'compiler_element_kind': 'Statement'}))
negative('native_cannot_claim_roslyn_correspondence', lambda: correspondence('cpp', {'correspondence': 'roslyn_unique_source', 'phase': 'evaluation', 'compiler_element_kind': 'Statement'}))

ddl = (HERE / 'schema.sql').read_text(encoding='utf8'); connection = sqlite3.connect(':memory:'); connection.executescript(ddl); connection.execute('PRAGMA foreign_keys=OFF')
columns = {row[1]: row for row in connection.execute('PRAGMA table_info(cfg_operation)')}
check('typed_cfg_member_columns_required', lambda: require(all(columns[name][2] == 'TEXT' and columns[name][3] == 1 for name in ('correspondence', 'phase', 'compiler_element_kind')), 'missing typed CFG member columns'))
check('cfg_member_key_preserved', lambda: require([(row[1], row[5]) for row in columns.values() if row[5]] == [('block_id', 1), ('ordinal', 2)], 'CFG member key changed'))
def member(correspondence='clang_ast_pointer', phase='evaluation', kind='CFGStmt'):
    try:
        connection.execute('INSERT INTO cfg_operation VALUES (?,?,?,?,?,?)', ('block:fixture', 'operation:fixture', 0, correspondence, phase, kind))
    finally:
        connection.rollback()
check('typed_member_positive', member)
negative('member_missing_correspondence', lambda: member(None))
negative('member_unknown_correspondence', lambda: member('span_guess'))
negative('member_missing_phase', lambda: member(phase=None))
negative('member_unknown_phase', lambda: member(phase='preorder'))
negative('member_blank_compiler_kind', lambda: member(kind='  '))
old_sql = subprocess.run(['git', 'show', '4f1e2659be63000a1a35fe0582cae5a39e5d0a6c:docs/m0/schema.sql'], cwd=HERE.parent.parent, check=True, capture_output=True).stdout.decode('utf8')
old_db = sqlite3.connect(':memory:'); old_db.executescript(old_sql)
def require_current_schema(db):
    actual = db.execute("SELECT sql FROM sqlite_schema WHERE name='cfg_operation'").fetchone()[0]
    expected = connection.execute("SELECT sql FROM sqlite_schema WHERE name='cfg_operation'").fetchone()[0]
    require(actual == expected, 'historical schema lacks required typed member contract; fresh collecting generation required')
negative('historical_m1c0_schema_rejected', lambda: require_current_schema(old_db))
check('new_schema_definition_accepted', lambda: require_current_schema(connection))
ownership = CONTRACT['x-profile-table-ownership']['tables']['cfg_operation']
check('typed_members_follow_cfg_payload_ownership', lambda: require(ownership['mode'] == 'owned_child' and ownership['owner']['table'] == 'cfg_block'
    and set(ownership['typed_member_columns']) == {'operation_id', 'ordinal', 'correspondence', 'phase', 'compiler_element_kind'}, 'member payload could escape CFG ownership'))
report = {'status': 'passed_design_validation', 'scope': __doc__, 'positive_checks': checks, 'negative_cases_rejected': negatives,
    'inputs_sha256': {name: hashlib.sha256((HERE / name).read_bytes()).hexdigest() for name in ('contracts.json', 'schema.sql', 'field-dictionary.json', 'a005-cases.json')},
    'product_checks': 'not_run', 'native_oracle': 'separate prerequisite; not produced by this validator'}
encoded = (json.dumps(report, ensure_ascii=False, indent=2) + '\n').encode('utf8')
if args.report:
    Path(args.report).write_bytes(encoded)
sys.stdout.buffer.write(encoded)
