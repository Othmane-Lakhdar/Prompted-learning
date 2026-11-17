"""Synthetic M0 wire/identity contract probes; this is not an extractor test.

The semantic checks below exercise the declared acceptance rules on hand-authored
messages. They do not prove compiler correspondence, production IPC, or CFG facts.
"""
import argparse
import base64
import copy
import hashlib
import json
import pathlib
import re
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--dependency-path')
args = parser.parse_args()
if args.dependency_path:
    sys.path.insert(0, args.dependency_path)
from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError

HERE = pathlib.Path(__file__).resolve().parent
CONTRACT = json.loads((HERE / 'contracts.json').read_text(encoding='utf8'))
ID_PATTERN = re.compile(r'^[a-z][a-z0-9_-]*:[0-9a-f]{64}$')
LOCAL_PATTERN = re.compile(r'^(file|span|entity|external|unresolved|type|signature|parameter|appearance|body|operation|cfg|block|edge|access|annotation|preprocessing):[A-Za-z0-9_./-]+$')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'), allow_nan=False).encode('utf8')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def stable(domain, components):
    return domain.split('/')[0] + ':' + digest(canonical([domain, components]))


def schema(name, instance):
    document = {'$schema': CONTRACT['$schema'], '$defs': CONTRACT['$defs'], '$ref': '#/$defs/' + name}
    Draft202012Validator(document).validate(instance)


def ref(value, allowed, index, nullable=False):
    if value is None:
        require(nullable, 'required local reference is null')
        return
    require(LOCAL_PATTERN.fullmatch(value) is not None and ID_PATTERN.fullmatch(value) is None, 'canonical ID used as a worker-local key')
    require(value in index, 'dangling local reference: ' + value)
    require(value.split(':', 1)[0] in allowed, 'wrong local-reference target kind: ' + value)


def ordinals(rows, field='ordinal'):
    require([row[field] for row in rows] == list(range(len(rows))), 'ordered collection has duplicate/gapped/out-of-order ordinals')


def acyclic(edges, label):
    active, done = set(), set()
    def walk(key):
        if key in done:
            return
        require(key not in active, label + ' cycle')
        active.add(key)
        for child in edges.get(key, []):
            walk(child)
        active.remove(key)
        done.add(key)
    for key in edges:
        walk(key)


def byte_boundaries(file):
    raw = base64.b64decode(file['bytes_base64'], validate=True)
    require(len(raw) == file['byte_length'] and digest(raw) == file['original_sha256'], 'input bytes/hash/length mismatch')
    encoding, bom = file['encoding'], file['bom_bytes']
    expected = {'utf-8': b'\xef\xbb\xbf', 'utf-16le': b'\xff\xfe', 'utf-16be': b'\xfe\xff'}[encoding]
    require((encoding == 'utf-8' and bom in (0, 3)) or (encoding != 'utf-8' and bom == 2), 'encoding/BOM mismatch')
    require(not bom or raw[:bom] == expected, 'wrong BOM bytes')
    text = raw[bom:].decode(encoding, errors='strict')
    boundaries, position = {0, bom}, bom
    for char in text:
        position += len(char.encode(encoding))
        boundaries.add(position)
    return raw, boundaries


def semantic_fingerprint(request):
    selected = {key: copy.deepcopy(request[key]) for key in ('snapshot_id', 'context_id', 'unit_id', 'capture_index_fingerprint', 'worker', 'target', 'main_file_key', 'files', 'options', 'path_bindings', 'requested_features', 'expected_consumed_file_keys')}
    for file in selected['files']:
        del file['bytes_base64']
    for binding in selected['path_bindings']:
        del binding['physical_path']
    domain = 'native-worker-semantic-input/1'
    if 'analysis_selection' in request:
        selected['analysis_selection'] = copy.deepcopy(request['analysis_selection'])
        domain = 'native-worker-semantic-input/2'
    return digest(canonical([domain, selected]))


def rehash(request, result=None):
    request['semantic_input_fingerprint'] = semantic_fingerprint(request)
    request['request_fingerprint'] = digest(canonical({key: value for key, value in request.items() if key != 'request_fingerprint'}))
    if result is not None:
        for key in ('request_fingerprint', 'semantic_input_fingerprint'):
            result[key] = request[key]


def validate_request(request):
    schema('native-worker-request', request)
    unsigned = {key: value for key, value in request.items() if key != 'request_fingerprint'}
    require(request['request_fingerprint'] == digest(canonical(unsigned)), 'request fingerprint mismatch')
    require(request['semantic_input_fingerprint'] == semantic_fingerprint(request), 'semantic input fingerprint mismatch')
    require('storage_events' not in request['requested_features'] or 'cfg' in request['requested_features'], 'storage events require CFG')
    files = {}
    total = 0
    for file in request['files']:
        require(file['key'] not in files, 'duplicate input file key')
        require(not ID_PATTERN.fullmatch(file['key']), 'canonical file key in local namespace')
        raw, boundaries = byte_boundaries(file)
        files[file['key']] = (file, raw, boundaries)
        total += len(raw)
    require(request['main_file_key'] in files, 'main file is absent')
    require(set(request['expected_consumed_file_keys']) <= set(files) and request['main_file_key'] in request['expected_consumed_file_keys'], 'expected consumed-input set is not captured/main-bound')
    require(total <= request['budgets']['max_input_bytes'], 'input byte budget exceeded')
    return files


def validate_result(request, result, correspondence_candidates, publication=True):
    files = validate_request(request)
    schema('native-worker-result', result)
    for name in ('request_id', 'request_fingerprint', 'semantic_input_fingerprint', 'snapshot_id', 'context_id', 'unit_id', 'worker'):
        require(result[name] == request[name], 'result correlation/scope/tool mismatch: ' + name)
    require(set(result['consumed_file_keys']) <= set(files), 'worker reports uncaptured consumed file key')
    if result['status'] in ('complete', 'partial'):
        require(request['main_file_key'] in result['consumed_file_keys'], 'successful parse did not consume main file')
    if publication:
        require(request['phase'] == 'final', 'discovery facts cannot be published')
        require(result['status'] in ('complete', 'partial'), 'failed/cancelled worker result cannot publish facts')
        require(not result['discovery_requests'] and set(result['consumed_file_keys']) == set(request['expected_consumed_file_keys']), 'final consumed-input set is unstable or discovery remains')
    require(len(canonical(result)) + 1 <= request['budgets']['max_result_bytes'], 'result byte budget exceeded')
    index = {key: value[0] for key, value in files.items()}
    def register(rows):
        for row in rows:
            key = row['key']
            require(key not in index, 'duplicate local key: ' + key)
            require(not ID_PATTERN.fullmatch(key), 'canonical ID emitted as discovered local key')
            index[key] = row
    for family in ('spans', 'entities', 'types', 'signatures', 'appearances', 'bodies', 'operations', 'external_symbols', 'unresolved_references', 'control_flow', 'annotations', 'preprocessing'):
        register(result[family])
    for signature in result['signatures']:
        register(signature['parameters'])
    for cfg in result['control_flow']:
        register(cfg['blocks'])
        register(cfg['edges'])
        for block in cfg['blocks']:
            register(block['storage_events'])
    require(len(index) - len(files) <= request['budgets']['max_records'], 'record budget exceeded')
    require(len(result['operations']) <= request['budgets']['max_operations'], 'operation budget exceeded')
    span_edges = {}
    def span_bounds(span):
        ref(span['file_key'], {'file'}, index)
        file, _, boundaries = files[span['file_key']]
        require(0 <= span['start_byte'] <= span['end_byte'] <= file['byte_length'], 'invalid byte span')
        require(span['start_byte'] in boundaries and span['end_byte'] in boundaries, 'span splits encoded scalar')
    for span in result['spans']:
        span_bounds(span)
        for origin in span['origin_stack']:
            span_bounds(origin)
        ref(span['cause_span_key'], {'span'}, index, True)
        span_edges[span['key']] = [] if span['cause_span_key'] is None else [span['cause_span_key']]
    acyclic(span_edges, 'source origin')
    entity_edges = {}
    for entity in result['entities']:
        ref(entity['owner_entity_key'], {'entity'}, index, True)
        ref(entity['origin_span_key'], {'span'}, index, True)
        entity_edges[entity['key']] = [] if entity['owner_entity_key'] is None else [entity['owner_entity_key']]
        if entity['compiler_identity_kind'] == 'parameter':
            require(entity['owner_entity_key'] is not None and entity['declared_ordinal'] is not None, 'parameter identity lacks owner/ordinal')
        if entity['compiler_identity_kind'] == 'compiler':
            require(entity['compiler_key'] is not None, 'named entity lacks compiler key')
    acyclic(entity_edges, 'entity containment')
    for type_row in result['types']:
        ref(type_row['named_entity_key'], {'entity', 'external', 'unresolved'}, index, True)
        ref(type_row['element_type_key'], {'type'}, index, True)
        for key in type_row['argument_type_keys']:
            ref(key, {'type'}, index)
    for signature in result['signatures']:
        ref(signature['owner_entity_key'], {'entity'}, index)
        ref(signature['return_type_key'], {'type'}, index)
        ordinals(signature['parameters'])
        for parameter in signature['parameters']:
            ref(parameter['entity_key'], {'entity'}, index)
            ref(parameter['type_key'], {'type'}, index)
            entity = index[parameter['entity_key']]
            require(entity['entity_kind'] == 'parameter' and entity['owner_entity_key'] == signature['owner_entity_key'] and entity['declared_ordinal'] == parameter['ordinal'], 'parameter detail/identity owner mismatch')
    for appearance in result['appearances']:
        ref(appearance['entity_key'], {'entity'}, index)
        ref(appearance['span_key'], {'span'}, index)
    for modifier in result['entity_modifiers']:
        ref(modifier['entity_key'], {'entity'}, index)
        ref(modifier['span_key'], {'span'}, index, True)
    for annotation in result['annotations']:
        ref(annotation['owner_entity_key'], {'entity'}, index)
        ref(annotation['span_key'], {'span'}, index)
        ref(annotation['attribute_type_key'], {'entity', 'external', 'unresolved'}, index, True)
        ordinals(annotation['arguments'])
        for argument in annotation['arguments']:
            ref(argument['expression_key'], {'operation'}, index, True)
    for event in result['preprocessing']:
        ref(event['file_key'], {'file'}, index)
        ref(event['span_key'], {'span'}, index)
        ref(event['resolved_file_key'], {'file'}, index, True)
    for body in result['bodies']:
        ref(body['owner_entity_key'], {'entity'}, index)
        ref(body['span_key'], {'span'}, index, True)
        ref(body['root_operation_key'], {'operation'}, index, True)
        require(body['status'] == 'complete' or bool(body['reason']), 'incomplete body lacks reason')
        if body['status'] == 'complete':
            require(body['root_operation_key'] is not None, 'complete body lacks root operation')
        if body['root_operation_key']:
            root = index[body['root_operation_key']]
            require(root['body_key'] == body['key'] and root['parent_operation_key'] is None, 'body root ownership mismatch')
    operation_edges, children, paths = {}, {}, set()
    for operation in result['operations']:
        ref(operation['body_key'], {'body'}, index)
        ref(operation['parent_operation_key'], {'operation'}, index, True)
        ref(operation['span_key'], {'span'}, index, True)
        ref(operation['cause_span_key'], {'span'}, index, True)
        ref(operation['type_key'], {'type'}, index, True)
        ref(operation['referenced_entity_key'], {'entity', 'external', 'unresolved'}, index, True)
        path_key = (operation['body_key'], tuple(operation['structural_path']))
        require(path_key not in paths, 'duplicate operation structural path')
        paths.add(path_key)
        parent = operation['parent_operation_key']
        operation_edges[operation['key']] = [] if parent is None else [parent]
        if parent:
            require(index[parent]['body_key'] == operation['body_key'], 'cross-body child')
            require(operation['structural_path'] == index[parent]['structural_path'] + [operation['child_ordinal']], 'child structural path mismatch')
            children.setdefault(parent, []).append(operation)
        else:
            require(operation['child_ordinal'] is None, 'root has child ordinal')
        require(operation['supported'] or bool(operation['unsupported_reason']), 'unsupported operation lacks reason')
        for operand in operation['operands']:
            ref(operand['target_key'], {'operation', 'entity', 'external', 'unresolved', 'type'}, index)
            ref(operand['parameter_entity_key'], {'entity'}, index, True)
    acyclic(operation_edges, 'operation tree')
    for body in result['bodies']:
        owned = [operation for operation in result['operations'] if operation['body_key'] == body['key']]
        roots = [operation['key'] for operation in owned if operation['parent_operation_key'] is None]
        if owned:
            require(roots == [body['root_operation_key']], 'body operations have a detached or unregistered root')
            for operation in owned:
                cursor = operation['key']
                while index[cursor]['parent_operation_key'] is not None:
                    cursor = index[cursor]['parent_operation_key']
                require(cursor == body['root_operation_key'], 'body operation is unreachable from its root')
        else:
            require(body['status'] != 'complete' and body['root_operation_key'] is None, 'complete body has no operation tree')
    for rows in children.values():
        ordinals(sorted(rows, key=lambda row: row['child_ordinal']), 'child_ordinal')
    def read_nodes(operation_key):
        operation = index[operation_key]
        found = {operation_key} if operation['semantic_role'] == 'read' and operation['referenced_entity_key'] else set()
        for child in children.get(operation_key, []):
            found.update(read_nodes(child['key']))
        return found
    cfg_body_keys = [cfg['body_key'] for cfg in result['control_flow']]
    require(len(cfg_body_keys) == len(set(cfg_body_keys)), 'duplicate CFG outcome for body')
    if 'cfg' in request['requested_features']:
        require(set(cfg_body_keys) == {body['key'] for body in result['bodies']}, 'requested CFG lacks per-body outcome')
    for cfg in result['control_flow']:
        ref(cfg['body_key'], {'body'}, index)
        if cfg['status'] in ('failed', 'not_attempted'):
            require(cfg['entry_block_key'] is None and cfg['exit_block_key'] is None and not cfg['blocks'] and not cfg['edges'] and bool(cfg['reason']), 'unavailable CFG has fabricated structure or lacks reason')
            continue
        block_keys = {block['key'] for block in cfg['blocks']}
        require(cfg['entry_block_key'] in block_keys and cfg['exit_block_key'] in block_keys, 'CFG entry/exit ownership mismatch')
        require(index[cfg['entry_block_key']]['kind'] == 'entry' and index[cfg['exit_block_key']]['kind'] == 'exit', 'CFG entry/exit kind mismatch')
        ordinals(cfg['blocks'])
        for block in cfg['blocks']:
            ordinals(block['operations'])
            ordinals(block['storage_events'])
            required_reads, reads, event_keys = set(), set(), set()
            for member in block['operations']:
                ref(member['operation_key'], {'operation'}, index)
                require(index[member['operation_key']]['body_key'] == cfg['body_key'], 'cross-body CFG member')
                if member['correspondence'] == 'roslyn_unique_source':
                    candidates = correspondence_candidates.get(member['operation_key'], [])
                    require(candidates == [member['operation_key']], 'CFG source correspondence is absent/ambiguous')
                if member['phase'] in ('branch_value', 'terminator'):
                    required_reads.update(read_nodes(member['operation_key']))
            for event in block['storage_events']:
                ref(event['storage_entity_key'], {'entity'}, index, event['event_kind'] == 'unknown_effect')
                ref(event['operation_key'], {'operation'}, index, event['event_kind'] in ('unknown_effect', 'parameter_definition'))
                ref(event['span_key'], {'span'}, index, True)
                key = (event['operation_key'], event['event_kind'], event['storage_entity_key'])
                require(key not in event_keys, 'duplicate storage evaluation event')
                event_keys.add(key)
                if event['event_kind'] == 'read':
                    require(index[event['operation_key']]['semantic_role'] != 'destination', 'assignment destination incorrectly read')
                    reads.add(event['operation_key'])
                if event['event_kind'] == 'unknown_effect':
                    require(bool(event['reason']), 'unknown effect lacks reason')
            if 'storage_events' in request['requested_features']:
                require(required_reads <= reads, 'branch/return value read omitted from flow events')
            else:
                require(not block['storage_events'], 'unrequested storage events were produced')
        for edge in cfg['edges']:
            require(edge['source_block_key'] in block_keys and edge['destination_block_key'] in block_keys, 'cross-CFG edge')
            ref(edge['condition_operation_key'], {'operation'}, index, edge['kind'] not in ('true_branch', 'false_branch'))
            if edge['kind'] == 'return':
                require(edge['destination_block_key'] == cfg['exit_block_key'], 'return edge does not reach exit')
        branch_groups = {}
        for edge in cfg['edges']:
            if edge['kind'] in ('true_branch', 'false_branch'):
                branch_groups.setdefault((edge['source_block_key'], edge['condition_operation_key']), set()).add(edge['kind'])
        require(all(kinds == {'true_branch', 'false_branch'} for kinds in branch_groups.values()), 'conditional branch labels are not explicit complements')
    coverage_keys = []
    feature_policy = CONTRACT['x-worker-feature-policy']
    all_features = [feature for name in ('source_semantics', 'cfg', 'storage_events') for feature in feature_policy[name]]
    requested = {feature for name in request['requested_features'] for feature in feature_policy[name]}
    for coverage in result['coverage']:
        ref(coverage['file_key'], {'file'}, index)
        coverage_keys.append((coverage['file_key'], coverage['feature']))
        for key in coverage['unparsed_span_keys']:
            ref(key, {'span'}, index)
        require(coverage['status'] == 'complete' or bool(coverage['reason']), 'partial coverage lacks reason')
        if coverage['feature'] not in requested:
            require(coverage['status'] == 'not_attempted' and coverage['reason'] == 'not_requested', 'unrequested feature has misleading coverage')
    require(set(coverage_keys) == {(file, feature) for file in files for feature in all_features} and len(coverage_keys) == len(set(coverage_keys)), 'missing/duplicate file-feature coverage')
    for discovery in result['discovery_requests']:
        ref(discovery['including_file_key'], {'file'}, index)
    for diagnostic in result['diagnostics']:
        ref(diagnostic['span_key'], {'span'}, index, True)
        if diagnostic['subject_key'] is not None:
            require(diagnostic['subject_key'] in index, 'dangling diagnostic subject')
    if result['status'] == 'complete':
        require(result['stop_reason'] is None and not result['discovery_requests'], 'complete result contains discovery/stop reason')
        require(all(c['status'] == 'complete' and not c['unparsed_span_keys'] for c in result['coverage'] if c['feature'] in requested), 'complete result has incomplete input coverage')
        require(all(o['supported'] for o in result['operations']), 'complete result has unsupported operation')
        require(all(b['status'] == 'complete' for b in result['bodies']), 'complete result contains incomplete body')
        require(all(c['status'] == ('complete' if 'cfg' in request['requested_features'] else 'not_attempted') for c in result['control_flow']), 'complete result contains incomplete requested CFG')
        require(not any(d['severity'] == 'error' for d in result['diagnostics']), 'complete result has error diagnostic')
    else:
        require(bool(result['stop_reason']), 'noncomplete result lacks stop reason')


def make_fixture():
    raw = '// café 😀\r\nnamespace pilot { int Choose(int value) { return value; } }\r\n'.encode('utf8')
    request = {'schema': 'native-worker-request/1', 'request_id': 'synthetic-contract-case', 'snapshot_id': 'snapshot:' + '1' * 64, 'context_id': 'build_context:' + '2' * 64, 'unit_id': 'compilation_unit:' + '3' * 64, 'capture_index_fingerprint': '4' * 64, 'semantic_input_fingerprint': '5' * 64, 'worker': {'name': 'prompted-learning-clang-worker', 'version': 'contract-fixture-1', 'clang_version': '19.1.7', 'binary_sha256': '6' * 64}, 'target': {'triple': 'x86_64-pc-windows-msvc', 'language_standard': 'c++17', 'ms_compatibility_version': '19.44'}, 'main_file_key': 'file:main', 'files': [{'key': 'file:main', 'file_version_id': 'file_version:' + '7' * 64, 'logical_path': 'pilot/main.cpp', 'original_sha256': digest(raw), 'byte_length': len(raw), 'encoding': 'utf-8', 'bom_bytes': 0, 'bytes_base64': base64.b64encode(raw).decode('ascii')}], 'options': [], 'path_bindings': [], 'budgets': {'time_ms': 10000, 'memory_bytes': 1048576, 'max_input_bytes': 1048576, 'max_result_bytes': 1048576, 'max_records': 100, 'max_operations': 100}}
    request['requested_features'] = ['source_semantics', 'cfg', 'storage_events']
    request['analysis_selection'] = copy.deepcopy(CONTRACT['x-choose-cfg-policy']['selection_by_language']['cpp'])
    request['phase'] = 'final'
    request['expected_consumed_file_keys'] = ['file:main']
    rehash(request)
    result = {key: copy.deepcopy(request[key]) for key in ('request_id', 'request_fingerprint', 'semantic_input_fingerprint', 'snapshot_id', 'context_id', 'unit_id', 'worker')}
    result.update(schema='native-worker-result/1', status='complete', stop_reason=None)
    result['consumed_file_keys'] = ['file:main']
    for family in ('spans', 'entities', 'types', 'signatures', 'appearances', 'bodies', 'operations', 'control_flow', 'external_symbols', 'unresolved_references', 'coverage', 'diagnostics', 'discovery_requests', 'entity_modifiers', 'annotations', 'preprocessing'):
        result[family] = []
    result['spans'] = [{'key': 'span:body', 'file_key': 'file:main', 'start_byte': raw.index(b'{', raw.index(b'Choose')), 'end_byte': raw.index(b'}') + 1, 'origin_kind': 'written', 'origin_stack': [], 'cause_span_key': None}]
    def entity(key, kind, owner=None, ordinal=None):
        return {'key': key, 'entity_kind': kind, 'written_name': key.split(':')[1], 'qualified_name': key.split(':')[1], 'compiler_identity_kind': 'parameter' if owner else 'compiler', 'compiler_key': None if owner else 'c:@N@pilot@F@Choose#I#', 'owner_entity_key': owner, 'declared_ordinal': ordinal, 'origin_span_key': 'span:body', 'linkage': 'no_linkage' if owner else 'external', 'access': None, 'compiler_signature_key': None, 'compiler_type_key': None, 'origin': 'written', 'resolution': 'resolved'}
    result['entities'] = [entity('entity:f', 'function'), entity('entity:value', 'parameter', 'entity:f', 0)]
    int_type = {'tag': 'builtin', 'name': 'int'}
    signature_key = copy.deepcopy(request['analysis_selection']['selectors'][0]['compiler_signature_key'])
    result['entities'][0].update(written_name='Choose', qualified_name='pilot::Choose')
    result['entities'][0]['compiler_signature_key'] = signature_key
    result['entities'][1]['compiler_type_key'] = int_type
    result['types'] = [{'key': 'type:int', 'compiler_type_key': int_type, 'named_entity_key': None, 'element_type_key': None, 'argument_type_keys': []}]
    result['signatures'] = [{'key': 'signature:f', 'owner_entity_key': 'entity:f', 'compiler_signature_key': signature_key, 'return_type_key': 'type:int', 'parameters': [{'key': 'parameter:value', 'entity_key': 'entity:value', 'ordinal': 0, 'name': 'value', 'type_key': 'type:int', 'passing_mode': 'value', 'optional': False, 'variadic': False}]}]
    result['bodies'] = [{'key': 'body:f', 'owner_entity_key': 'entity:f', 'span_key': 'span:body', 'representation': 'semantic', 'kind': 'function', 'root_operation_key': 'operation:root', 'status': 'complete', 'reason': None}]
    def operation(key, kind, path, parent, ordinal, role):
        return {'key': key, 'body_key': 'body:f', 'structural_path': path, 'parent_operation_key': parent, 'child_ordinal': ordinal, 'child_role': role, 'representation': 'semantic', 'operation_kind': kind, 'compiler_kind': {'block': 'CompoundStmt', 'return': 'ReturnStmt', 'reference': 'DeclRefExpr'}[kind], 'span_key': 'span:body', 'cause_span_key': None, 'implicit': False, 'semantic_role': role, 'execution_point': None, 'type_key': None, 'referenced_entity_key': 'entity:value' if kind == 'reference' else None, 'operator_kind': None, 'constant': None, 'operands': [], 'supported': True, 'unsupported_reason': None}
    result['operations'] = [operation('operation:root', 'block', [0], None, None, 'body'), operation('operation:return', 'return', [0, 0], 'operation:root', 0, 'return'), operation('operation:read', 'reference', [0, 0, 0], 'operation:return', 0, 'read')]
    def event(key, ordinal, kind, operation_key):
        return {'key': key, 'ordinal': ordinal, 'event_kind': kind, 'storage_entity_key': 'entity:value', 'operation_key': operation_key, 'span_key': 'span:body', 'phase': 'entry' if kind == 'parameter_definition' else 'return', 'reason': None}
    result['control_flow'] = [{'key': 'cfg:f', 'body_key': 'body:f', 'status': 'complete', 'reason': None, 'entry_block_key': 'block:entry', 'exit_block_key': 'block:exit', 'blocks': [{'key': 'block:entry', 'ordinal': 0, 'kind': 'entry', 'operations': [], 'storage_events': [event('access:parameter', 0, 'parameter_definition', None)]}, {'key': 'block:return', 'ordinal': 1, 'kind': 'ordinary', 'operations': [{'ordinal': 0, 'operation_key': 'operation:return', 'correspondence': 'clang_ast_pointer', 'phase': 'branch_value', 'compiler_element_kind': 'CFGStmt'}], 'storage_events': [event('access:read', 0, 'read', 'operation:read')]}, {'key': 'block:exit', 'ordinal': 2, 'kind': 'exit', 'operations': [], 'storage_events': []}], 'edges': [{'key': 'edge:entry', 'source_block_key': 'block:entry', 'destination_block_key': 'block:return', 'kind': 'continuation', 'condition_operation_key': None, 'feasibility': 'not_established'}, {'key': 'edge:return', 'source_block_key': 'block:return', 'destination_block_key': 'block:exit', 'kind': 'return', 'condition_operation_key': None, 'feasibility': 'not_established'}]}]
    result['operations'][2]['type_key'] = 'type:int'
    result['coverage'] = [{'file_key': 'file:main', 'feature': feature, 'status': 'complete', 'reason': None, 'unparsed_span_keys': []} for name in ('source_semantics', 'cfg', 'storage_events') for feature in CONTRACT['x-worker-feature-policy'][name]]
    return request, result, {'operation:return': ['operation:return']}


def validate_identities(records):
    edges = {}
    def references(value, key=''):
        if isinstance(value, dict):
            return [ref_id for name, child in value.items() for ref_id in references(child, name)]
        if isinstance(value, list):
            return [ref_id for child in value for ref_id in references(child, key)]
        return [value] if key.endswith('_id') and isinstance(value, str) and ID_PATTERN.fullmatch(value) else []
    for record_id, envelope in records.items():
        schema('identity-input', envelope)
        require(envelope['domain'] in CONTRACT['x-identity-recipes'], 'unregistered identity domain')
        edges[record_id] = [value for value in references(envelope['components']) if value in records]
    acyclic(edges, 'identity preimage')
    for record_id, envelope in records.items():
        require(record_id == stable(envelope['domain'], envelope['components']), 'identity preimage does not match ID')


def identity_fixture():
    root = {'root_key': 'pilot', 'boundary': 'internal', 'exclusions': []}
    source = {'root_key': 'pilot', 'relative_path': 'main.cpp', 'content_hash': 'a' * 64, 'availability': 'available', 'origin': 'written'}
    records = {}
    def add(domain, components):
        record_id = stable(domain, components)
        records[record_id] = {'schema': 'identity-input/1', 'domain': domain, 'components': components}
        return record_id
    snapshot = add('snapshot/1', {'repository_identity': 'fixture', 'roots': [root], 'files': [source]})
    add('source_root/1', {'snapshot_id': snapshot, 'root': root})
    file_id = add('source_file/1', {'snapshot_id': snapshot, 'root_key': 'pilot', 'relative_path': 'main.cpp'})
    version = add('file_version/1', {'file_id': file_id, 'content_hash': 'a' * 64, 'availability': 'available', 'unavailable_reason': None})
    add('source_span/1', {'file_version_id': version, 'start_byte': 0, 'end_byte': 1, 'origin_kind': 'written', 'origin_stack': []})
    return records


negative_results = []
def negative(name, mutate, identity=False):
    request, result, candidates = make_fixture()
    records = identity_fixture()
    mutate(records if identity else (request, result, candidates))
    try:
        validate_identities(records) if identity else validate_result(request, result, candidates)
    except (ValueError, ValidationError) as error:
        negative_results.append({'name': name, 'rejected_by': type(error).__name__, 'reason': str(error).splitlines()[0]})
        return
    raise AssertionError('Negative case accepted: ' + name)


request, result, candidates = make_fixture()
validate_result(request, result, candidates)
managed_mapping = copy.deepcopy(result)
managed_mapping['control_flow'][0]['blocks'][1]['operations'][0]['correspondence'] = 'roslyn_unique_source'
validate_result(request, managed_mapping, candidates)
partial = copy.deepcopy(result)
partial.update(status='partial', stop_reason='uncaptured_internal_include')
partial['coverage'][0].update(status='partial', reason='uncaptured_internal_include')
partial['discovery_requests'] = [{'including_file_key': 'file:main', 'written_include': 'later.hpp', 'logical_candidate_path': 'pilot/later.hpp', 'reason': 'uncaptured_internal_include'}]
discovery_request = copy.deepcopy(request)
discovery_request['phase'] = 'discovery'
rehash(discovery_request, partial)
validate_result(discovery_request, partial, candidates, publication=False)
validate_identities(identity_fixture())
source_request, source_result, source_candidates = make_fixture()
source_request['requested_features'] = ['source_semantics']
source_request.pop('analysis_selection')
rehash(source_request, source_result)
source_result['control_flow'][0].update(status='not_attempted', reason='not_requested', entry_block_key=None, exit_block_key=None, blocks=[], edges=[])
for coverage in source_result['coverage']:
    if coverage['feature'] in ('control_flow', 'storage_events'):
        coverage.update(status='not_attempted', reason='not_requested')
validate_result(source_request, source_result, source_candidates)
failed_cfg = copy.deepcopy(result)
failed_cfg.update(status='partial', stop_reason='cfg_unavailable')
failed_cfg['control_flow'][0].update(status='failed', reason='cfg_unavailable', entry_block_key=None, exit_block_key=None, blocks=[], edges=[])
for coverage in failed_cfg['coverage']:
    if coverage['feature'] == 'control_flow':
        coverage.update(status='failed', reason='cfg_unavailable')
    if coverage['feature'] == 'storage_events':
        coverage.update(status='not_attempted', reason='required_cfg_unavailable')
validate_result(request, failed_cfg, candidates)

negative('duplicate_local_key', lambda x: x[1]['entities'].append(copy.deepcopy(x[1]['entities'][0])))
negative('dangling_entity_reference', lambda x: x[1]['operations'][2].update(referenced_entity_key='entity:missing'))
negative('wrong_kind_reference', lambda x: x[1]['operations'][2].update(referenced_entity_key='body:f'))
negative('canonical_ID_in_local_namespace', lambda x: x[1]['operations'][2].update(referenced_entity_key=x[0]['snapshot_id']))
negative('wrong_context_echo', lambda x: x[1].update(context_id='build_context:' + 'f' * 64))
negative('wrong_request_hash_echo', lambda x: x[1].update(request_fingerprint='f' * 64))
def discovery_publication(x):
    x[0]['phase'] = 'discovery'
    rehash(x[0], x[1])
negative('discovery_facts_published', discovery_publication)
negative('final_consumed_set_missing_main', lambda x: x[1].update(consumed_file_keys=[]))
def newly_consumed_captured_header(x):
    extra = dict(x[0]['files'][0], key='file:header', file_version_id='file_version:' + '9' * 64, logical_path='pilot/extra.hpp')
    x[0]['files'].append(extra)
    x[1]['consumed_file_keys'].append('file:header')
    rehash(x[0], x[1])
negative('newly_consumed_already_captured_header_requires_rerun', newly_consumed_captured_header)
def changed_bytes(x):
    file = x[0]['files'][0]
    original = base64.b64decode(file['bytes_base64'])
    file['bytes_base64'] = base64.b64encode(b'x' + original[1:]).decode('ascii')
    rehash(x[0], x[1])
negative('wrong_input_bytes_with_valid_request_hash', changed_bytes)
negative('canonical_form_discovered_key', lambda x: x[1]['entities'][0].update(key='entity:' + 'a' * 64))
negative('named_type_points_to_existing_file', lambda x: x[1]['types'][0].update(named_entity_key='file:main'))
def invalid_integer(x):
    x[0]['budgets']['max_records'] = 9223372036854775808
    rehash(x[0], x[1])
negative('integer_exceeds_int64', invalid_integer)
negative('out_of_bounds_span', lambda x: x[1]['spans'][0].update(end_byte=100000))
negative('reversed_span', lambda x: x[1]['spans'][0].update(start_byte=x[1]['spans'][0]['end_byte'] + 1))
negative('span_inside_utf8_scalar', lambda x: x[1]['spans'][0].update(start_byte=base64.b64decode(x[0]['files'][0]['bytes_base64']).index('😀'.encode('utf8')) + 1))
negative('source_origin_cycle', lambda x: x[1]['spans'][0].update(cause_span_key='span:body'))
negative('operation_parent_cycle', lambda x: x[1]['operations'][0].update(parent_operation_key='operation:read', child_ordinal=0))
negative('complete_with_discovered_input', lambda x: x[1]['discovery_requests'].append({'including_file_key': 'file:main', 'written_include': 'later.hpp', 'logical_candidate_path': None, 'reason': 'uncaptured'}))
negative('complete_with_partial_coverage', lambda x: x[1]['coverage'][0].update(status='partial', reason='unsupported'))
negative('complete_with_unsupported_operation', lambda x: x[1]['operations'][2].update(supported=False, unsupported_reason='unsupported'))
negative('missing_file_coverage', lambda x: x[1]['coverage'].clear())
negative('requested_cfg_outcome_omitted', lambda x: x[1]['control_flow'].clear())
negative('complete_body_root_omitted', lambda x: x[1]['bodies'][0].update(root_operation_key=None))
negative('detached_body_root', lambda x: x[1]['operations'].append(dict(x[1]['operations'][0], key='operation:detached', structural_path=[9])))
negative('missing_parameter_owner', lambda x: x[1]['entities'][1].update(owner_entity_key=None))
negative('return_read_omitted', lambda x: x[1]['control_flow'][0]['blocks'][1]['storage_events'].clear())
negative('duplicate_storage_event', lambda x: x[1]['control_flow'][0]['blocks'][1]['storage_events'].append(dict(x[1]['control_flow'][0]['blocks'][1]['storage_events'][0], key='access:duplicate', ordinal=1)))
negative('assignment_destination_read', lambda x: x[1]['operations'][2].update(semantic_role='destination'))
negative('return_not_to_exit', lambda x: x[1]['control_flow'][0]['edges'][1].update(destination_block_key='block:return'))
negative('conditional_edge_without_complement', lambda x: x[1]['control_flow'][0]['edges'][0].update(kind='true_branch', condition_operation_key='operation:read'))
def wrong_body_cfg(x):
    body = dict(x[1]['bodies'][0], key='body:other', root_operation_key='operation:other')
    operation = dict(x[1]['operations'][0], key='operation:other', body_key='body:other')
    x[1]['bodies'].append(body)
    x[1]['operations'].append(operation)
    x[1]['control_flow'][0]['blocks'][1]['operations'][0]['operation_key'] = 'operation:other'
negative('cross_body_cfg_reference', wrong_body_cfg)
def corrupt_semantic_hash(x):
    x[0]['semantic_input_fingerprint'] = 'f' * 64
    x[0]['request_fingerprint'] = digest(canonical({key: value for key, value in x[0].items() if key != 'request_fingerprint'}))
    x[1]['request_fingerprint'] = x[0]['request_fingerprint']
    x[1]['semantic_input_fingerprint'] = x[0]['semantic_input_fingerprint']
negative('wrong_semantic_fingerprint_with_valid_request_hash', corrupt_semantic_hash)
def ambiguous(x):
    x[1]['control_flow'][0]['blocks'][1]['operations'][0]['correspondence'] = 'roslyn_unique_source'
    x[2]['operation:return'] = ['operation:return', 'operation:read']
negative('ambiguous_cfg_source_correspondence', ambiguous)
negative('identity_wrong_preimage', lambda records: next(iter(records.values()))['components'].update(repository_identity='changed'), True)
negative('identity_span_depends_on_body', lambda records: next(value for value in records.values() if value['domain'] == 'source_span/1')['components'].update(body_id='body:' + '8' * 64), True)
negative('identity_own_id_component', lambda records: next(iter(records.values()))['components'].update(own_canonical_id=next(iter(records))), True)
def identity_cycle(records):
    file_key = next(key for key, value in records.items() if value['domain'] == 'source_file/1')
    version_key = next(key for key, value in records.items() if value['domain'] == 'file_version/1')
    records[file_key]['components']['snapshot_id'] = version_key
negative('identity_dependency_cycle', identity_cycle, True)

print(json.dumps({'status': 'passed_design_validation', 'positive_cases': ['synthetic_native_wire', 'synthetic_managed_correspondence_semantics', 'truthful_partial_discovery', 'acyclic_captured_source_identities', 'source_only_features_leave_cfg_unattempted', 'failed_cfg_without_fabricated_blocks'], 'negative_cases_rejected': negative_results, 'scope': 'Hand-authored message/schema and semantic-contract mutation checks only. No compiler extraction, canonical persistence, profile export, IPC, source archive implementation or AI benchmark is executed.'}, indent=2))
