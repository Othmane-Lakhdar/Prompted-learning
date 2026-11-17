-- M0 design contract v1. This file specifies storage; it is not an extractor.
-- All databases use this same DDL. Post-write invariants in contracts.md are mandatory.
PRAGMA foreign_keys = ON;
PRAGMA user_version = 1;

CREATE TABLE schema_info (
    version INTEGER PRIMARY KEY CHECK(version=1),
    vocabulary_version TEXT NOT NULL,
    serializer_version TEXT NOT NULL
) STRICT;

CREATE TABLE dataset (
    id TEXT PRIMARY KEY,
    snapshot_id TEXT NOT NULL REFERENCES snapshot(id) DEFERRABLE INITIALLY DEFERRED,
    semantic_fingerprint TEXT,
    configuration_fingerprint TEXT NOT NULL,
    configuration_json TEXT NOT NULL CHECK(json_valid(configuration_json)),
    state TEXT NOT NULL CHECK(state IN ('collecting','validating','finalized','failed','cancelled')),
    source_manifest_hash TEXT,
    created_utc TEXT NOT NULL
) STRICT;

CREATE TABLE snapshot (
    id TEXT PRIMARY KEY,
    repository_identity TEXT NOT NULL,
    vcs_revision TEXT,
    local_modifications_included INTEGER CHECK(local_modifications_included IN (0,1)),
    created_utc TEXT NOT NULL,
    boundary_policy TEXT NOT NULL
) STRICT;

CREATE TABLE record (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    section TEXT NOT NULL CHECK(section IN ('BD0.1','BD0.2','BD0.3','BD0.4','BD0.5','BD0.6','BD0.7','BD0.8','BD0.9','BD0.10','BD0.11','BD0.12','BD0.13','BD0.14','BD0.15','BD0.16','BDG.1','BDG.2','BDG.3','BDG.4','BDG.5','BDG.6','BDG.7','BDB.1','BDB.2','BDB.3','BDB.4','BDB.5','BDB.6','BDB.7','BDB.8','BDB.9','BDB.10')),
    snapshot_id TEXT NOT NULL REFERENCES snapshot(id),
    context_id TEXT REFERENCES build_context(id) DEFERRABLE INITIALLY DEFERRED,
    context_independent INTEGER NOT NULL CHECK(context_independent IN (0,1)),
    origin_span_id TEXT REFERENCES source_span(id) DEFERRABLE INITIALLY DEFERRED,
    CHECK((context_independent=1 AND context_id IS NULL) OR (context_independent=0 AND context_id IS NOT NULL))
) STRICT;

CREATE TABLE field_state (
    record_id TEXT NOT NULL REFERENCES record(id),
    field_path TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('known','unknown','not_applicable','not_collected')),
    reason TEXT,
    evidence_id TEXT REFERENCES evidence(id) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY(record_id,field_path),
    CHECK(state='known' OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE evidence (
    id TEXT PRIMARY KEY REFERENCES record(id),
    kind TEXT NOT NULL CHECK(kind IN ('source','compiler','evaluation','build_log','artifact_inspection','derivation','policy')),
    source_span_id TEXT REFERENCES source_span(id),
    tool_id TEXT REFERENCES tool(id),
    build_run_id TEXT REFERENCES build_run(id),
    blob_hash TEXT REFERENCES source_blob(hash),
    locator TEXT,
    detail TEXT
) STRICT;

CREATE TABLE record_evidence (
    record_id TEXT NOT NULL REFERENCES record(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    role TEXT NOT NULL,
    PRIMARY KEY(record_id,evidence_id,role)
) STRICT;

CREATE TABLE record_extension (
    record_id TEXT NOT NULL REFERENCES record(id),
    schema_name TEXT NOT NULL,
    schema_version INTEGER NOT NULL CHECK(schema_version>0),
    payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
    PRIMARY KEY(record_id,schema_name,schema_version)
) STRICT;


CREATE TABLE source_root (
    id TEXT PRIMARY KEY REFERENCES record(id),
    logical_name TEXT NOT NULL,
    original_path TEXT NOT NULL,
    root_kind TEXT NOT NULL CHECK(root_kind IN ('physical','virtual')),
    boundary TEXT NOT NULL CHECK(boundary IN ('internal','external','vendor')),
    policy TEXT NOT NULL
) STRICT;

CREATE TABLE exclusion (
    id TEXT PRIMARY KEY REFERENCES record(id),
    root_id TEXT NOT NULL REFERENCES source_root(id),
    rule TEXT NOT NULL,
    reason TEXT NOT NULL,
    selection_state TEXT NOT NULL CHECK(selection_state IN ('excluded','undetermined')),
    matched_count INTEGER CHECK(matched_count>=0)
) STRICT;

CREATE TABLE source_blob (
    hash TEXT PRIMARY KEY CHECK(length(hash)=64),
    byte_length INTEGER NOT NULL CHECK(byte_length>=0),
    storage_key TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE source_file (
    id TEXT PRIMARY KEY REFERENCES record(id),
    root_id TEXT NOT NULL REFERENCES source_root(id),
    relative_path TEXT NOT NULL,
    original_path TEXT NOT NULL,
    name TEXT NOT NULL,
    extension TEXT NOT NULL,
    language TEXT,
    role TEXT NOT NULL CHECK(role IN ('source','header','build_definition','resource','schema','template','script','documentation','support_other')),
    origin TEXT NOT NULL CHECK(origin IN ('written','generated','external')),
    generation_id TEXT REFERENCES generation(id) DEFERRABLE INITIALLY DEFERRED,
    UNIQUE(root_id,relative_path)
) STRICT;

CREATE TABLE file_path (
    file_id TEXT NOT NULL REFERENCES source_file(id),
    path TEXT NOT NULL,
    path_kind TEXT NOT NULL CHECK(path_kind IN ('original','alias','virtual')),
    PRIMARY KEY(file_id,path)
) STRICT;

CREATE TABLE file_version (
    id TEXT PRIMARY KEY REFERENCES record(id),
    file_id TEXT NOT NULL REFERENCES source_file(id),
    content_hash TEXT REFERENCES source_blob(hash),
    byte_length INTEGER CHECK(byte_length>=0),
    encoding TEXT,
    bom_bytes INTEGER CHECK(bom_bytes IN (0,2,3,4)),
    available INTEGER NOT NULL CHECK(available IN (0,1)),
    extraction_status TEXT NOT NULL CHECK(extraction_status IN ('complete','partial','failed','not_attempted')),
    virtual_uri TEXT,
    CHECK(available=0 OR (content_hash IS NOT NULL AND byte_length IS NOT NULL))
) STRICT;

CREATE TABLE source_span (
    id TEXT PRIMARY KEY REFERENCES record(id),
    file_version_id TEXT NOT NULL REFERENCES file_version(id),
    start_byte INTEGER NOT NULL CHECK(start_byte>=0),
    end_byte INTEGER NOT NULL CHECK(end_byte>=start_byte),
    origin_kind TEXT NOT NULL CHECK(origin_kind IN ('written','generated','implicit_cause','macro_spelling','macro_expansion')),
    cause_span_id TEXT REFERENCES source_span(id),
    CHECK(cause_span_id IS NULL OR cause_span_id<>id)
) STRICT;


CREATE TABLE project (
    id TEXT PRIMARY KEY REFERENCES record(id),
    name TEXT NOT NULL,
    definition_file_id TEXT REFERENCES source_file(id),
    system_kind TEXT NOT NULL CHECK(system_kind IN ('msbuild','cmake','other')),
    system_name TEXT NOT NULL
) STRICT;

CREATE TABLE project_language (
    project_id TEXT NOT NULL REFERENCES project(id),
    language TEXT NOT NULL,
    PRIMARY KEY(project_id,language)
) STRICT;

CREATE TABLE solution (
    id TEXT PRIMARY KEY REFERENCES record(id),
    name TEXT NOT NULL,
    definition_file_id TEXT REFERENCES source_file(id)
) STRICT;

CREATE TABLE solution_project (
    solution_id TEXT NOT NULL REFERENCES solution(id),
    project_id TEXT NOT NULL REFERENCES project(id),
    logical_folder_id TEXT REFERENCES grouping(id),
    PRIMARY KEY(solution_id,project_id)
) STRICT;

CREATE TABLE configuration_mapping (
    id TEXT PRIMARY KEY REFERENCES record(id),
    solution_id TEXT NOT NULL REFERENCES solution(id),
    project_id TEXT NOT NULL REFERENCES project(id),
    solution_configuration TEXT NOT NULL,
    solution_platform TEXT NOT NULL,
    project_configuration TEXT NOT NULL,
    project_platform TEXT NOT NULL,
    selected_for_build INTEGER NOT NULL CHECK(selected_for_build IN (0,1))
) STRICT;

CREATE TABLE build_context (
    id TEXT PRIMARY KEY REFERENCES record(id) DEFERRABLE INITIALLY DEFERRED,
    project_id TEXT NOT NULL REFERENCES project(id),
    solution_id TEXT REFERENCES solution(id),
    configuration_mapping_id TEXT REFERENCES configuration_mapping(id),
    configuration TEXT NOT NULL,
    platform TEXT NOT NULL,
    architecture TEXT NOT NULL,
    target_framework TEXT,
    runtime_identifier TEXT,
    language_version TEXT NOT NULL,
    output_identity TEXT NOT NULL,
    effective_fingerprint TEXT NOT NULL,
    working_directory TEXT NOT NULL
) STRICT;

CREATE TABLE context_setting (
    context_id TEXT NOT NULL REFERENCES build_context(id),
    category TEXT NOT NULL CHECK(category IN ('define','feature','goal','compiler_option','include_path','reference_path','environment')),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    name TEXT NOT NULL,
    value TEXT NOT NULL,
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(context_id,category,ordinal)
) STRICT;

CREATE TABLE tool (
    id TEXT PRIMARY KEY REFERENCES record(id),
    role TEXT NOT NULL,
    name TEXT NOT NULL,
    version TEXT,
    path TEXT,
    content_hash TEXT,
    toolset TEXT
) STRICT;

CREATE TABLE context_tool (
    context_id TEXT NOT NULL REFERENCES build_context(id),
    tool_id TEXT NOT NULL REFERENCES tool(id),
    role TEXT NOT NULL,
    PRIMARY KEY(context_id,tool_id,role)
) STRICT;

CREATE TABLE context_input (
    context_id TEXT NOT NULL REFERENCES build_context(id),
    role TEXT NOT NULL CHECK(role IN ('response_file','definition','environment_file')),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    file_version_id TEXT NOT NULL REFERENCES file_version(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(context_id,role,ordinal)
) STRICT;

CREATE TABLE compilation_unit (
    id TEXT PRIMARY KEY REFERENCES record(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    language TEXT NOT NULL CHECK(language IN ('csharp','cpp')),
    main_file_version_id TEXT REFERENCES file_version(id),
    output_identity TEXT NOT NULL,
    effective_fingerprint TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('complete','partial','failed','not_attempted'))
) STRICT;

CREATE TABLE unit_input (
    unit_id TEXT NOT NULL REFERENCES compilation_unit(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    role TEXT NOT NULL CHECK(role IN ('main','source','included_header','module','generated','reference','response_file')),
    file_version_id TEXT REFERENCES file_version(id),
    dependency_id TEXT REFERENCES dependency(id),
    observed_consumed INTEGER CHECK(observed_consumed IN (0,1)),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(unit_id,role,ordinal),
    CHECK((file_version_id IS NOT NULL)+(dependency_id IS NOT NULL)=1)
) STRICT;


CREATE TABLE entity (
    id TEXT PRIMARY KEY REFERENCES record(id),
    language TEXT NOT NULL CHECK(language IN ('csharp','cpp')),
    entity_kind TEXT NOT NULL,
    written_name TEXT,
    qualified_name TEXT,
    compiler_identity TEXT,
    identity_scheme TEXT NOT NULL,
    identity_version TEXT NOT NULL,
    containing_entity_id TEXT REFERENCES record(id),
    lexical_scope_id TEXT REFERENCES record(id),
    identity_unit_id TEXT REFERENCES compilation_unit(id),
    origin TEXT NOT NULL CHECK(origin IN ('written','generated','implicit')),
    access TEXT CHECK(access IN ('public','private','protected','internal','protected_internal','private_protected','file_local')),
    linkage TEXT CHECK(linkage IN ('external','internal','no_linkage','module','unique_external')),
    availability_condition TEXT,
    resolution TEXT NOT NULL CHECK(resolution IN ('resolved','partial','unresolved'))
) STRICT;

CREATE TABLE entity_unit (
    entity_id TEXT NOT NULL REFERENCES entity(id),
    unit_id TEXT NOT NULL REFERENCES compilation_unit(id),
    role TEXT NOT NULL CHECK(role IN ('definition','declaration','use')),
    PRIMARY KEY(entity_id,unit_id,role)
) STRICT;

CREATE TABLE entity_modifier (
    entity_id TEXT NOT NULL REFERENCES entity(id),
    modifier TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    PRIMARY KEY(entity_id,modifier)
) STRICT;

CREATE TABLE appearance (
    id TEXT PRIMARY KEY REFERENCES record(id),
    entity_id TEXT REFERENCES record(id),
    span_id TEXT NOT NULL REFERENCES source_span(id),
    unit_id TEXT NOT NULL REFERENCES compilation_unit(id),
    appearance_kind TEXT NOT NULL CHECK(appearance_kind IN ('declaration','definition','reference','generated','implicit')),
    surrounding_id TEXT REFERENCES record(id),
    active INTEGER CHECK(active IN (0,1)),
    spelling_span_id TEXT REFERENCES source_span(id),
    expansion_span_id TEXT REFERENCES source_span(id)
) STRICT;

CREATE TABLE type_node (
    id TEXT PRIMARY KEY REFERENCES record(id),
    type_kind TEXT NOT NULL CHECK(type_kind IN ('named','builtin','pointer','lvalue_reference','rvalue_reference','array','tuple','function','type_parameter','dependent','unresolved')),
    named_entity_id TEXT REFERENCES record(id),
    element_type_id TEXT REFERENCES type_node(id),
    builtin_name TEXT,
    rank INTEGER CHECK(rank>=0),
    bound_expression_id TEXT REFERENCES record(id),
    nullable TEXT NOT NULL CHECK(nullable IN ('annotated','not_annotated','oblivious','not_applicable','unknown')),
    resolution TEXT NOT NULL CHECK(resolution IN ('explicit','inferred','dependent','unresolved')),
    written_span_id TEXT REFERENCES source_span(id),
    compiler_display TEXT,
    function_signature_id TEXT REFERENCES signature(id)
) STRICT;

CREATE TABLE type_argument (
    type_id TEXT NOT NULL REFERENCES type_node(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    argument_kind TEXT NOT NULL CHECK(argument_kind IN ('type','value','template','pack','unresolved')),
    argument_type_id TEXT REFERENCES type_node(id),
    expression_id TEXT REFERENCES record(id),
    value_text TEXT,
    label TEXT,
    PRIMARY KEY(type_id,ordinal)
) STRICT;

CREATE TABLE type_qualifier (
    type_id TEXT NOT NULL REFERENCES type_node(id),
    qualifier TEXT NOT NULL,
    PRIMARY KEY(type_id,qualifier)
) STRICT;

CREATE TABLE signature (
    id TEXT PRIMARY KEY REFERENCES record(id),
    callable_id TEXT NOT NULL REFERENCES record(id),
    callable_kind TEXT NOT NULL,
    return_type_id TEXT REFERENCES type_node(id),
    return_passing TEXT NOT NULL CHECK(return_passing IN ('value','ref','ref_readonly','lvalue_reference','rvalue_reference','none')),
    calling_convention TEXT,
    language_linkage TEXT,
    exception_specification TEXT,
    body_state TEXT NOT NULL CHECK(body_state IN ('written','implicit','absent','unavailable')),
    display TEXT NOT NULL
) STRICT;

CREATE TABLE signature_qualifier (
    signature_id TEXT NOT NULL REFERENCES signature(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    qualifier TEXT NOT NULL,
    PRIMARY KEY(signature_id,ordinal)
) STRICT;

CREATE TABLE parameter (
    id TEXT PRIMARY KEY REFERENCES record(id),
    signature_id TEXT NOT NULL REFERENCES signature(id),
    entity_id TEXT REFERENCES entity(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    name TEXT,
    type_id TEXT REFERENCES type_node(id),
    passing_mode TEXT NOT NULL CHECK(passing_mode IN ('value','ref','out','in','ref_readonly','lvalue_reference','rvalue_reference')),
    optional INTEGER NOT NULL CHECK(optional IN (0,1)),
    variadic INTEGER NOT NULL CHECK(variadic IN (0,1)),
    default_expression_id TEXT REFERENCES record(id),
    default_value_kind TEXT,
    default_value TEXT,
    UNIQUE(signature_id,ordinal)
) STRICT;

CREATE TABLE entity_detail (
    entity_id TEXT PRIMARY KEY REFERENCES entity(id),
    type_id TEXT REFERENCES type_node(id),
    signature_id TEXT REFERENCES signature(id),
    initializer_id TEXT REFERENCES record(id),
    constant_kind TEXT,
    constant_value TEXT,
    bit_field_width TEXT,
    underlying_type_id TEXT REFERENCES type_node(id),
    operator_kind TEXT,
    accessor_kind TEXT,
    backing_field_id TEXT REFERENCES record(id),
    parameter_position INTEGER CHECK(parameter_position>=0),
    parameter_kind TEXT,
    default_type_id TEXT REFERENCES type_node(id),
    alias_written_target TEXT,
    alias_target_id TEXT REFERENCES record(id),
    original_definition_id TEXT REFERENCES record(id),
    layout_kind TEXT,
    alignment_expression TEXT,
    requirement_expression_id TEXT REFERENCES record(id),
    associated_statement_id TEXT REFERENCES record(id),
    mutable INTEGER CHECK(mutable IN (0,1))
) STRICT;

CREATE TABLE member_relation (
    owner_id TEXT NOT NULL REFERENCES record(id),
    member_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL CHECK(role IN ('declared_member','parameter','type_parameter','constraint','base','interface','accessor','capture','import','export','specialization','template_argument','substitution','initialization','implicit_receiver')),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(owner_id,role,ordinal,member_id)
) STRICT;

CREATE TABLE template_substitution (
    owner_id TEXT NOT NULL REFERENCES record(id),
    parameter_id TEXT NOT NULL REFERENCES record(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    pack_index INTEGER CHECK(pack_index>=0),
    argument_kind TEXT NOT NULL CHECK(argument_kind IN ('type','value','template','pack','unresolved')),
    argument_type_id TEXT REFERENCES type_node(id),
    argument_entity_id TEXT REFERENCES record(id),
    expression_id TEXT REFERENCES record(id),
    value_text TEXT,
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(owner_id,parameter_id,ordinal)
) STRICT;


-- body identity and origins are mandatory metadata; detailed operations belong to BD0.7.
CREATE TABLE body (
    id TEXT PRIMARY KEY REFERENCES record(id),
    owner_entity_id TEXT NOT NULL REFERENCES record(id),
    unit_id TEXT NOT NULL REFERENCES compilation_unit(id),
    body_kind TEXT NOT NULL CHECK(body_kind IN ('function','initializer','accessor','lambda','implicit')),
    span_id TEXT REFERENCES source_span(id),
    representation TEXT NOT NULL CHECK(representation IN ('syntax','semantic','lowered')),
    status TEXT NOT NULL CHECK(status IN ('complete','partial','failed','not_attempted'))
) STRICT;

CREATE TABLE operation (
    id TEXT PRIMARY KEY REFERENCES record(id),
    body_id TEXT NOT NULL REFERENCES body(id),
    representation TEXT NOT NULL CHECK(representation IN ('syntax','semantic','lowered')),
    operation_kind TEXT NOT NULL,
    compiler_kind TEXT NOT NULL,
    span_id TEXT REFERENCES source_span(id),
    cause_span_id TEXT REFERENCES source_span(id),
    implicit INTEGER NOT NULL CHECK(implicit IN (0,1)),
    semantic_role TEXT NOT NULL,
    execution_point TEXT,
    type_id TEXT REFERENCES record(id),
    referenced_entity_id TEXT REFERENCES record(id),
    operator_kind TEXT,
    constant_kind TEXT,
    constant_value TEXT,
    supported INTEGER NOT NULL CHECK(supported IN (0,1)),
    unsupported_reason TEXT,
    CHECK(supported=1 OR (unsupported_reason IS NOT NULL AND length(trim(unsupported_reason))>0))
) STRICT;

CREATE TABLE operation_child (
    parent_id TEXT NOT NULL REFERENCES operation(id),
    child_id TEXT NOT NULL REFERENCES operation(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    role TEXT NOT NULL,
    PRIMARY KEY(parent_id,ordinal),
    UNIQUE(child_id),
    CHECK(parent_id<>child_id)
) STRICT;

CREATE TABLE operation_operand (
    operation_id TEXT NOT NULL REFERENCES operation(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    role TEXT NOT NULL CHECK(role IN ('operand','argument','receiver','destination','condition','return','capture','handler','conversion')),
    target_id TEXT NOT NULL REFERENCES record(id),
    parameter_id TEXT REFERENCES record(id),
    PRIMARY KEY(operation_id,role,ordinal)
) STRICT;


CREATE TABLE preprocessing (
    id TEXT PRIMARY KEY REFERENCES record(id),
    event_kind TEXT NOT NULL CHECK(event_kind IN ('macro_definition','macro_invocation','conditional','inactive_region','include','import','forced_include','pragma','generated_origin')),
    span_id TEXT REFERENCES source_span(id),
    name TEXT,
    expression_text TEXT,
    selected INTEGER CHECK(selected IN (0,1)),
    resolved_target_id TEXT REFERENCES record(id),
    definition_id TEXT REFERENCES record(id),
    spelling_span_id TEXT REFERENCES source_span(id),
    expansion_span_id TEXT REFERENCES source_span(id),
    generation_id TEXT REFERENCES generation(id),
    origin_setting_id TEXT REFERENCES build_setting(id)
) STRICT;

CREATE TABLE preprocessing_parameter (
    preprocessing_id TEXT NOT NULL REFERENCES preprocessing(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    name TEXT NOT NULL,
    replacement_text TEXT,
    PRIMARY KEY(preprocessing_id,ordinal)
) STRICT;

CREATE TABLE documentation (
    id TEXT PRIMARY KEY REFERENCES record(id),
    entity_id TEXT NOT NULL REFERENCES entity(id),
    span_id TEXT NOT NULL REFERENCES source_span(id),
    format TEXT NOT NULL,
    part TEXT NOT NULL CHECK(part IN ('summary','parameter','return','remarks','other')),
    parameter_id TEXT REFERENCES record(id),
    text TEXT NOT NULL,
    claim_status TEXT NOT NULL CHECK(claim_status='documented_claim'),
    resolved_reference_id TEXT REFERENCES record(id)
) STRICT;

CREATE TABLE annotation (
    id TEXT PRIMARY KEY REFERENCES record(id),
    owner_id TEXT NOT NULL REFERENCES record(id),
    attribute_type_id TEXT REFERENCES record(id),
    span_id TEXT REFERENCES source_span(id),
    resolution TEXT NOT NULL CHECK(resolution IN ('resolved','partial','unresolved'))
) STRICT;

CREATE TABLE annotation_argument (
    annotation_id TEXT NOT NULL REFERENCES annotation(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    name TEXT,
    expression_id TEXT REFERENCES record(id),
    constant_kind TEXT,
    constant_value TEXT,
    PRIMARY KEY(annotation_id,ordinal)
) STRICT;

CREATE TABLE external_symbol (
    id TEXT PRIMARY KEY REFERENCES record(id),
    language TEXT NOT NULL CHECK(language IN ('csharp','cpp')),
    qualified_identity TEXT NOT NULL,
    signature TEXT NOT NULL,
    library_name TEXT
) STRICT;

CREATE TABLE relationship (
    id TEXT PRIMARY KEY REFERENCES record(id),
    source_id TEXT NOT NULL REFERENCES record(id),
    kind TEXT NOT NULL,
    destination_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL,
    resolution TEXT NOT NULL CHECK(resolution IN ('resolved','candidate','unresolved')),
    analysis_result_id TEXT REFERENCES record(id),
    meaning TEXT NOT NULL CHECK(meaning IN ('structural','resolved_reference','may','must'))
) STRICT;

CREATE TABLE occurrence (
    id TEXT PRIMARY KEY REFERENCES record(id),
    span_id TEXT REFERENCES source_span(id),
    cause_span_id TEXT REFERENCES source_span(id),
    enclosing_id TEXT NOT NULL REFERENCES record(id),
    operation_id TEXT REFERENCES record(id),
    call_site_id TEXT REFERENCES record(id),
    condition_text TEXT,
    CHECK(span_id IS NOT NULL OR cause_span_id IS NOT NULL)
) STRICT;

CREATE TABLE occurrence_argument (
    occurrence_id TEXT NOT NULL REFERENCES occurrence(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    argument_kind TEXT NOT NULL CHECK(argument_kind IN ('value','type')),
    target_id TEXT NOT NULL REFERENCES record(id),
    PRIMARY KEY(occurrence_id,argument_kind,ordinal)
) STRICT;

CREATE TABLE relationship_occurrence (
    relationship_id TEXT NOT NULL REFERENCES relationship(id),
    occurrence_id TEXT NOT NULL REFERENCES occurrence(id),
    PRIMARY KEY(relationship_id,occurrence_id)
) STRICT;


CREATE TABLE analysis_run (
    id TEXT PRIMARY KEY REFERENCES record(id),
    producer_tool_id TEXT NOT NULL REFERENCES tool(id),
    family TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    settings_json TEXT NOT NULL CHECK(json_valid(settings_json)),
    started_utc TEXT,
    ended_utc TEXT,
    outcome TEXT NOT NULL CHECK(outcome IN ('complete','partial','failed','cancelled','not_attempted')),
    stop_reason TEXT
) STRICT;

CREATE TABLE analysis_result (
    id TEXT PRIMARY KEY REFERENCES record(id),
    run_id TEXT NOT NULL REFERENCES analysis_run(id),
    unit_id TEXT NOT NULL REFERENCES record(id),
    family TEXT NOT NULL,
    version INTEGER NOT NULL CHECK(version>0),
    status TEXT NOT NULL CHECK(status IN ('complete','partial','failed','not_attempted')),
    final INTEGER NOT NULL CHECK(final IN (0,1)),
    input_fingerprint TEXT NOT NULL,
    guarantee_text TEXT NOT NULL,
    unknown_boundary_count INTEGER CHECK(unknown_boundary_count>=0),
    UNIQUE(run_id,unit_id,family,version)
) STRICT;

CREATE TABLE analysis_dependency (
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    input_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL,
    PRIMARY KEY(result_id,input_id,role),
    CHECK(result_id<>input_id)
) STRICT;

CREATE TABLE analysis_assumption (
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    assumption TEXT NOT NULL,
    evidence_id TEXT REFERENCES evidence(id),
    PRIMARY KEY(result_id,ordinal)
) STRICT;

CREATE TABLE cfg_block (
    id TEXT PRIMARY KEY REFERENCES record(id),
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    body_id TEXT NOT NULL REFERENCES body(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    block_kind TEXT NOT NULL CHECK(block_kind IN ('entry','exit','ordinary','exception','cleanup','suspension')),
    UNIQUE(result_id,ordinal)
) STRICT;

CREATE TABLE cfg_operation (
    block_id TEXT NOT NULL REFERENCES cfg_block(id),
    operation_id TEXT NOT NULL REFERENCES record(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    PRIMARY KEY(block_id,ordinal)
) STRICT;

CREATE TABLE cfg_edge (
    id TEXT PRIMARY KEY REFERENCES record(id),
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    source_block_id TEXT NOT NULL REFERENCES cfg_block(id),
    destination_block_id TEXT NOT NULL REFERENCES cfg_block(id),
    edge_kind TEXT NOT NULL CHECK(edge_kind IN ('continuation','true_branch','false_branch','loop','return','exception','cleanup','suspend','resume')),
    condition_id TEXT REFERENCES record(id),
    feasibility TEXT NOT NULL CHECK(feasibility IN ('not_established','established_possible','established_impossible')),
    feasibility_evidence_id TEXT REFERENCES evidence(id)
) STRICT;

CREATE TABLE data_flow_fact (
    id TEXT PRIMARY KEY REFERENCES record(id),
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    family TEXT NOT NULL CHECK(family IN ('definition_use','argument_parameter','return_caller','capture','shared_storage','alias','possible_target','constant','range','unknown_effect')),
    source_id TEXT REFERENCES record(id),
    target_id TEXT REFERENCES record(id),
    storage_id TEXT REFERENCES record(id),
    meaning TEXT NOT NULL CHECK(meaning IN ('may','must','unknown')),
    source_span_id TEXT REFERENCES source_span(id),
    target_span_id TEXT REFERENCES source_span(id),
    value_kind TEXT,
    value_text TEXT,
    lower_bound TEXT,
    upper_bound TEXT,
    reason TEXT,
    CHECK(meaning<>'unknown' OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE call_site (
    id TEXT PRIMARY KEY REFERENCES record(id),
    caller_id TEXT NOT NULL REFERENCES record(id),
    operation_id TEXT REFERENCES record(id),
    occurrence_id TEXT NOT NULL REFERENCES occurrence(id),
    selected_declaration_id TEXT REFERENCES record(id),
    dispatch TEXT NOT NULL CHECK(dispatch IN ('direct','virtual','interface','delegate','function_pointer','dynamic','unknown')),
    receiver_id TEXT REFERENCES record(id),
    candidate_completeness TEXT NOT NULL CHECK(candidate_completeness IN ('complete_under_assumptions','incomplete','not_analyzed')),
    remainder_reason TEXT,
    analysis_result_id TEXT REFERENCES analysis_result(id),
    CHECK(candidate_completeness='complete_under_assumptions' OR (remainder_reason IS NOT NULL AND length(trim(remainder_reason))>0))
) STRICT;

CREATE TABLE call_target (
    call_site_id TEXT NOT NULL REFERENCES call_site(id),
    target_id TEXT NOT NULL REFERENCES record(id),
    target_kind TEXT NOT NULL CHECK(target_kind IN ('selected_declaration','runtime_candidate')),
    meaning TEXT NOT NULL CHECK(meaning IN ('resolved_reference','may','must')),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(call_site_id,target_id,target_kind)
) STRICT;

CREATE TABLE call_argument (
    call_site_id TEXT NOT NULL REFERENCES call_site(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    expression_id TEXT NOT NULL REFERENCES record(id),
    parameter_id TEXT REFERENCES record(id),
    passing_mode TEXT NOT NULL CHECK(passing_mode IN ('value','ref','out','in','ref_readonly','lvalue_reference','rvalue_reference')),
    PRIMARY KEY(call_site_id,ordinal)
) STRICT;

CREATE TABLE root_policy (
    id TEXT PRIMARY KEY REFERENCES record(id),
    name TEXT NOT NULL,
    version TEXT NOT NULL,
    callback_policy TEXT NOT NULL,
    initialization_policy TEXT NOT NULL,
    cleanup_policy TEXT NOT NULL,
    external_policy TEXT NOT NULL,
    completeness TEXT NOT NULL CHECK(completeness IN ('complete_under_assumptions','incomplete'))
) STRICT;

CREATE TABLE reachability_root (
    policy_id TEXT NOT NULL REFERENCES root_policy(id),
    entity_id TEXT NOT NULL REFERENCES record(id),
    reason TEXT NOT NULL,
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(policy_id,entity_id)
) STRICT;

CREATE TABLE reachability_edge_kind (
    policy_id TEXT NOT NULL REFERENCES root_policy(id),
    relationship_kind TEXT NOT NULL,
    PRIMARY KEY(policy_id,relationship_kind)
) STRICT;

CREATE TABLE reachability_member (
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    entity_id TEXT NOT NULL REFERENCES record(id),
    policy_id TEXT NOT NULL REFERENCES root_policy(id),
    membership TEXT NOT NULL CHECK(membership IN ('reachable','outside_computed_set')),
    feasibility TEXT NOT NULL CHECK(feasibility IN ('not_established','established_possible','established_impossible')),
    evidence_id TEXT REFERENCES evidence(id),
    PRIMARY KEY(result_id,entity_id)
) STRICT;

CREATE TABLE reachability_witness (
    result_id TEXT NOT NULL REFERENCES analysis_result(id),
    entity_id TEXT NOT NULL REFERENCES record(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    relationship_id TEXT NOT NULL REFERENCES record(id),
    PRIMARY KEY(result_id,entity_id,ordinal)
) STRICT;

CREATE TABLE coverage (
    id TEXT PRIMARY KEY REFERENCES record(id),
    producer_id TEXT NOT NULL REFERENCES tool(id),
    subject_id TEXT NOT NULL REFERENCES record(id),
    feature TEXT NOT NULL,
    span_id TEXT REFERENCES source_span(id),
    status TEXT NOT NULL CHECK(status IN ('complete','partial','failed','not_attempted')),
    reason TEXT,
    unresolved_count INTEGER CHECK(unresolved_count>=0),
    CHECK(status='complete' OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE diagnostic (
    id TEXT PRIMARY KEY REFERENCES record(id),
    producer_id TEXT REFERENCES tool(id),
    subject_id TEXT REFERENCES record(id),
    severity TEXT NOT NULL CHECK(severity IN ('info','warning','error')),
    code TEXT NOT NULL,
    message TEXT NOT NULL,
    span_id TEXT REFERENCES source_span(id),
    cause_id TEXT REFERENCES record(id)
) STRICT;


CREATE TABLE grouping (
    id TEXT PRIMARY KEY REFERENCES record(id),
    group_kind TEXT NOT NULL CHECK(group_kind IN ('source_root','directory','file','namespace','module','project','solution','ide_folder','architecture')),
    name TEXT NOT NULL,
    parent_id TEXT REFERENCES grouping(id),
    selected_root_id TEXT REFERENCES grouping(id),
    relative_path TEXT,
    depth INTEGER CHECK(depth>=0),
    definition TEXT NOT NULL,
    source_object_id TEXT REFERENCES record(id),
    CHECK(parent_id IS NULL OR parent_id<>id)
) STRICT;

CREATE TABLE group_membership (
    group_id TEXT NOT NULL REFERENCES grouping(id),
    member_id TEXT NOT NULL REFERENCES record(id),
    context_id TEXT REFERENCES build_context(id),
    context_key TEXT NOT NULL,
    basis TEXT NOT NULL CHECK(basis IN ('physical','semantic','evaluated_participation','ide_definition','explicit_definition')),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(group_id,member_id,basis,context_key),
    CHECK((context_key='independent' AND context_id IS NULL) OR (context_id IS NOT NULL AND context_key=context_id))
) STRICT;

CREATE TABLE view_definition (
    id TEXT PRIMARY KEY REFERENCES record(id),
    name TEXT NOT NULL,
    grouping_kind TEXT NOT NULL,
    selected_root_id TEXT REFERENCES grouping(id),
    depth INTEGER CHECK(depth>=0),
    generated_policy TEXT NOT NULL,
    implicit_policy TEXT NOT NULL,
    unresolved_policy TEXT NOT NULL,
    candidate_policy TEXT NOT NULL,
    relation_scope TEXT NOT NULL CHECK(relation_scope IN ('within','between','both')),
    counting_policy TEXT NOT NULL,
    aggregation_version TEXT NOT NULL,
    input_fingerprint TEXT NOT NULL
) STRICT;

CREATE TABLE view_context (
    view_id TEXT NOT NULL REFERENCES view_definition(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    PRIMARY KEY(view_id,context_id)
) STRICT;

CREATE TABLE view_filter (
    view_id TEXT NOT NULL REFERENCES view_definition(id),
    filter_kind TEXT NOT NULL CHECK(filter_kind IN ('entity_kind','relationship_kind')),
    value TEXT NOT NULL,
    PRIMARY KEY(view_id,filter_kind,value)
) STRICT;

CREATE TABLE group_relationship (
    id TEXT PRIMARY KEY REFERENCES record(id),
    view_id TEXT NOT NULL REFERENCES view_definition(id),
    source_group_id TEXT NOT NULL REFERENCES grouping(id),
    destination_group_id TEXT NOT NULL REFERENCES grouping(id),
    kind TEXT NOT NULL,
    resolution TEXT NOT NULL,
    pair_count INTEGER NOT NULL CHECK(pair_count>=0),
    occurrence_count INTEGER NOT NULL CHECK(occurrence_count>=0),
    source_entity_count INTEGER NOT NULL CHECK(source_entity_count>=0),
    destination_entity_count INTEGER NOT NULL CHECK(destination_entity_count>=0),
    candidate_edge_count INTEGER NOT NULL CHECK(candidate_edge_count>=0),
    coverage_status TEXT NOT NULL CHECK(coverage_status IN ('complete','partial','failed','not_attempted'))
) STRICT;

CREATE TABLE group_relationship_source (
    group_relationship_id TEXT NOT NULL REFERENCES group_relationship(id),
    source_record_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL CHECK(role IN ('relationship','occurrence','source_entity','destination_entity','candidate_edge')),
    PRIMARY KEY(group_relationship_id,source_record_id,role)
) STRICT;

CREATE TABLE summary (
    id TEXT PRIMARY KEY REFERENCES record(id),
    view_id TEXT NOT NULL REFERENCES view_definition(id),
    group_id TEXT REFERENCES grouping(id),
    summary_kind TEXT NOT NULL,
    dimension TEXT NOT NULL,
    count_value INTEGER CHECK(count_value>=0),
    analysis_result_id TEXT REFERENCES analysis_result(id),
    calculation_version TEXT NOT NULL,
    input_fingerprint TEXT NOT NULL,
    coverage_status TEXT NOT NULL CHECK(coverage_status IN ('complete','partial','failed','not_attempted'))
) STRICT;

CREATE TABLE summary_source (
    summary_id TEXT NOT NULL REFERENCES summary(id),
    source_record_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL,
    ordinal INTEGER CHECK(ordinal>=0),
    PRIMARY KEY(summary_id,source_record_id,role)
) STRICT;


CREATE TABLE build_definition (
    id TEXT PRIMARY KEY REFERENCES record(id),
    project_id TEXT REFERENCES project(id),
    file_version_id TEXT NOT NULL REFERENCES file_version(id),
    definition_kind TEXT NOT NULL CHECK(definition_kind IN ('solution','project','props','targets','directory_build','cmake','preset','other'))
) STRICT;

CREATE TABLE build_import (
    id TEXT PRIMARY KEY REFERENCES record(id),
    parent_definition_id TEXT NOT NULL REFERENCES build_definition(id),
    imported_definition_id TEXT REFERENCES build_definition(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    written_path TEXT NOT NULL,
    condition_text TEXT,
    condition_result INTEGER CHECK(condition_result IN (0,1)),
    resolution TEXT NOT NULL CHECK(resolution IN ('resolved','unresolved','not_selected'))
) STRICT;

CREATE TABLE build_setting (
    id TEXT PRIMARY KEY REFERENCES record(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    category TEXT NOT NULL CHECK(category IN ('property','item','metadata','compiler_option','include_path','define','reference_path','library_path','output_path','instruction')),
    name TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    value TEXT,
    item_id TEXT REFERENCES build_item(id),
    stage TEXT NOT NULL CHECK(stage IN ('declared','evaluated','after_execution')),
    definition_id TEXT REFERENCES build_definition(id),
    responsible_span_id TEXT REFERENCES source_span(id),
    condition_text TEXT,
    generic_schema_version INTEGER NOT NULL CHECK(generic_schema_version=1),
    CHECK(category<>'metadata' OR item_id IS NOT NULL)
) STRICT;

CREATE TABLE build_item (
    id TEXT PRIMARY KEY REFERENCES record(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    item_type TEXT NOT NULL,
    include_value TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    stage TEXT NOT NULL CHECK(stage IN ('declared','evaluated','after_execution')),
    definition_id TEXT REFERENCES build_definition(id),
    condition_text TEXT,
    participation_id TEXT REFERENCES file_participation(id),
    dependency_id TEXT REFERENCES dependency(id),
    UNIQUE(context_id,item_type,stage,ordinal)
) STRICT;

CREATE TABLE build_setting_history (
    setting_id TEXT NOT NULL REFERENCES build_setting(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    prior_value TEXT,
    next_value TEXT,
    definition_id TEXT REFERENCES build_definition(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(setting_id,ordinal)
) STRICT;

CREATE TABLE file_participation (
    id TEXT PRIMARY KEY REFERENCES record(id),
    file_id TEXT NOT NULL REFERENCES source_file(id),
    project_id TEXT NOT NULL REFERENCES project(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    declared_item INTEGER CHECK(declared_item IN (0,1)),
    role TEXT NOT NULL,
    selection TEXT NOT NULL CHECK(selection IN ('included','excluded','undetermined')),
    condition_text TEXT,
    reason TEXT,
    unit_id TEXT REFERENCES compilation_unit(id),
    action_id TEXT REFERENCES build_action(id),
    observed_consumed INTEGER CHECK(observed_consumed IN (0,1))
) STRICT;

CREATE TABLE participation_setting (
    participation_id TEXT NOT NULL REFERENCES file_participation(id),
    setting_id TEXT NOT NULL REFERENCES build_setting(id),
    PRIMARY KEY(participation_id,setting_id)
) STRICT;

CREATE TABLE dependency (
    id TEXT PRIMARY KEY REFERENCES record(id),
    requesting_project_id TEXT REFERENCES project(id),
    requesting_action_id TEXT REFERENCES build_action(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    requested_name TEXT NOT NULL,
    version_constraint TEXT,
    provider_kind TEXT NOT NULL CHECK(provider_kind IN ('package','assembly','native_library','header','module','tool','resource','unresolved')),
    selected_name TEXT,
    selected_version TEXT,
    selected_path TEXT,
    content_hash TEXT,
    direct INTEGER CHECK(direct IN (0,1)),
    purpose TEXT NOT NULL CHECK(purpose IN ('compile','link','generator','resource','copy','package')),
    resolution TEXT NOT NULL CHECK(resolution IN ('resolved','unresolved','conflict')),
    internal_project_id TEXT REFERENCES project(id),
    reason TEXT
) STRICT;

CREATE TABLE dependency_edge (
    request_id TEXT NOT NULL REFERENCES dependency(id),
    selected_through_id TEXT NOT NULL REFERENCES dependency(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(request_id,selected_through_id)
) STRICT;

CREATE TABLE build_action (
    id TEXT PRIMARY KEY REFERENCES record(id),
    project_id TEXT NOT NULL REFERENCES project(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    action_kind TEXT NOT NULL CHECK(action_kind IN ('restore','generate','compile','assemble','archive','link','resource','copy','package','sign','custom')),
    tool_id TEXT REFERENCES tool(id),
    task_name TEXT,
    target_name TEXT,
    command TEXT,
    working_directory TEXT,
    condition_text TEXT,
    planned_status TEXT NOT NULL CHECK(planned_status IN ('expected','not_selected','unknown'))
) STRICT;

CREATE TABLE action_argument (
    action_id TEXT NOT NULL REFERENCES build_action(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    value TEXT NOT NULL,
    PRIMARY KEY(action_id,ordinal)
) STRICT;

CREATE TABLE action_io (
    action_id TEXT NOT NULL REFERENCES build_action(id),
    object_id TEXT NOT NULL REFERENCES record(id),
    direction TEXT NOT NULL CHECK(direction IN ('input','expected_output')),
    role TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    PRIMARY KEY(action_id,direction,ordinal)
) STRICT;

CREATE TABLE action_dependency (
    action_id TEXT NOT NULL REFERENCES build_action(id),
    predecessor_id TEXT NOT NULL REFERENCES build_action(id),
    reason_kind TEXT NOT NULL CHECK(reason_kind IN ('consumes_output','explicit_order')),
    via_object_id TEXT REFERENCES record(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(action_id,predecessor_id,reason_kind),
    CHECK(action_id<>predecessor_id)
) STRICT;

CREATE TABLE generation (
    id TEXT PRIMARY KEY REFERENCES record(id),
    action_id TEXT REFERENCES build_action(id),
    generator_tool_id TEXT REFERENCES tool(id),
    output_id TEXT NOT NULL REFERENCES record(id),
    output_state TEXT NOT NULL CHECK(output_state IN ('expected','generated','cache_restored','existing')),
    source_entity_id TEXT REFERENCES record(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id)
) STRICT;

CREATE TABLE generation_mapping (
    generation_id TEXT NOT NULL REFERENCES generation(id),
    input_id TEXT NOT NULL REFERENCES record(id),
    output_id TEXT NOT NULL REFERENCES record(id),
    input_span_id TEXT REFERENCES source_span(id),
    output_span_id TEXT REFERENCES source_span(id),
    mapping_quality TEXT NOT NULL CHECK(mapping_quality IN ('exact','approximate','unknown')),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    PRIMARY KEY(generation_id,ordinal)
) STRICT;

CREATE TABLE build_run (
    id TEXT PRIMARY KEY REFERENCES record(id),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    started_utc TEXT,
    ended_utc TEXT,
    outcome TEXT NOT NULL CHECK(outcome IN ('succeeded','failed','cancelled','partial','not_observed')),
    logging_coverage TEXT NOT NULL,
    cancellation_reason TEXT,
    failure_reason TEXT
) STRICT;

CREATE TABLE build_run_goal (
    run_id TEXT NOT NULL REFERENCES build_run(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    goal TEXT NOT NULL,
    PRIMARY KEY(run_id,ordinal)
) STRICT;

CREATE TABLE build_run_log (
    run_id TEXT NOT NULL REFERENCES build_run(id),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    log_kind TEXT NOT NULL,
    PRIMARY KEY(run_id,evidence_id)
) STRICT;

CREATE TABLE action_execution (
    id TEXT PRIMARY KEY REFERENCES record(id),
    run_id TEXT NOT NULL REFERENCES build_run(id),
    action_id TEXT REFERENCES build_action(id),
    tool_id TEXT REFERENCES tool(id),
    command TEXT,
    working_directory TEXT,
    started_utc TEXT,
    ended_utc TEXT,
    duration_ns INTEGER CHECK(duration_ns>=0),
    outcome TEXT NOT NULL CHECK(outcome IN ('succeeded','failed','cancelled','skipped','cache_restored','unknown')),
    exit_code INTEGER,
    reason TEXT,
    observed_sequence INTEGER CHECK(observed_sequence>=0),
    concurrency_group TEXT,
    CHECK(outcome NOT IN ('skipped','cache_restored') OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE execution_argument (
    execution_id TEXT NOT NULL REFERENCES action_execution(id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    value TEXT NOT NULL,
    PRIMARY KEY(execution_id,ordinal)
) STRICT;

CREATE TABLE execution_io (
    execution_id TEXT NOT NULL REFERENCES action_execution(id),
    object_id TEXT NOT NULL REFERENCES record(id),
    role TEXT NOT NULL CHECK(role IN ('input','produced','recovered','existing')),
    evidence_id TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY(execution_id,object_id,role)
) STRICT;

CREATE TABLE artifact (
    id TEXT PRIMARY KEY REFERENCES record(id),
    artifact_kind TEXT NOT NULL,
    path TEXT NOT NULL,
    content_hash TEXT,
    byte_length INTEGER CHECK(byte_length>=0),
    context_id TEXT NOT NULL REFERENCES build_context(id),
    producer_action_id TEXT REFERENCES build_action(id),
    producer_run_id TEXT REFERENCES build_run(id),
    existence TEXT NOT NULL CHECK(existence IN ('expected','observed','missing','unknown')),
    contents_available INTEGER CHECK(contents_available IN (0,1)),
    retained_blob_hash TEXT REFERENCES source_blob(hash),
    output_role TEXT NOT NULL CHECK(output_role IN ('intermediate','final','debug','package','input')),
    source_file_id TEXT REFERENCES source_file(id),
    inspection_status TEXT NOT NULL CHECK(inspection_status IN ('complete','partial','failed','not_attempted')),
    format TEXT,
    architecture TEXT,
    managed_identity TEXT
) STRICT;

CREATE TABLE artifact_symbol (
    id TEXT PRIMARY KEY REFERENCES record(id),
    artifact_id TEXT NOT NULL REFERENCES artifact(id),
    symbol_kind TEXT NOT NULL CHECK(symbol_kind IN ('defined','exported','imported','assembly_reference','library_reference')),
    name TEXT NOT NULL,
    signature TEXT,
    source_entity_id TEXT REFERENCES record(id),
    emitted_origin TEXT CHECK(emitted_origin IN ('written','template','synthesized','unknown')),
    calling_convention TEXT,
    binary_interface TEXT
) STRICT;

CREATE TABLE artifact_mapping (
    id TEXT PRIMARY KEY REFERENCES record(id),
    artifact_id TEXT NOT NULL REFERENCES artifact(id),
    source_id TEXT NOT NULL REFERENCES record(id),
    emitted_id TEXT REFERENCES record(id),
    mapping_kind TEXT NOT NULL CHECK(mapping_kind IN ('source_output','inlining','removal','combination','other')),
    quality TEXT NOT NULL CHECK(quality IN ('exact','approximate','unknown')),
    gap_reason TEXT,
    evidence_id TEXT NOT NULL REFERENCES evidence(id)
) STRICT;

CREATE TABLE artifact_layout (
    id TEXT PRIMARY KEY REFERENCES record(id),
    artifact_id TEXT NOT NULL REFERENCES artifact(id),
    entity_id TEXT NOT NULL REFERENCES record(id),
    byte_size INTEGER CHECK(byte_size>=0),
    alignment_bytes INTEGER CHECK(alignment_bytes>0),
    member_offset_bits INTEGER CHECK(member_offset_bits>=0),
    evidence_id TEXT NOT NULL REFERENCES evidence(id)
) STRICT;

CREATE TABLE rebuild_comparison (
    id TEXT PRIMARY KEY REFERENCES record(id),
    before_dataset_id TEXT NOT NULL,
    after_dataset_id TEXT NOT NULL,
    before_run_id TEXT REFERENCES build_run(id),
    after_run_id TEXT REFERENCES build_run(id),
    dependency_coverage TEXT NOT NULL CHECK(dependency_coverage IN ('complete','partial','unknown')),
    policy_version TEXT NOT NULL
) STRICT;

CREATE TABLE rebuild_decision (
    id TEXT PRIMARY KEY REFERENCES record(id),
    comparison_id TEXT NOT NULL REFERENCES rebuild_comparison(id),
    subject_id TEXT NOT NULL REFERENCES record(id),
    decision_kind TEXT NOT NULL CHECK(decision_kind IN ('source_affected','predicted_rebuild','actually_rebuilt','skipped','cache_restored','artifact_changed','artifact_unchanged','input_changed')),
    rule TEXT,
    reason TEXT NOT NULL,
    before_hash TEXT,
    after_hash TEXT,
    evidence_id TEXT NOT NULL REFERENCES evidence(id)
) STRICT;


-- Export-only metadata is separate from canonical semantic records.
CREATE TABLE reference_availability (
    target_id TEXT PRIMARY KEY REFERENCES record(id),
    availability TEXT NOT NULL CHECK(availability IN ('included','omitted_by_profile','omitted_by_selection','omitted_by_budget','unresolved')),
    reason TEXT,
    CHECK(availability='included' OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE unresolved_reference (
    id TEXT PRIMARY KEY REFERENCES record(id),
    spelling TEXT,
    expected_kind TEXT NOT NULL,
    reason TEXT NOT NULL
) STRICT;

CREATE TABLE export_profile (
    id TEXT PRIMARY KEY,
    baseline TEXT NOT NULL CHECK(baseline IN ('P1','P2','P3','P4')),
    canonical_fingerprint TEXT NOT NULL,
    policy_fingerprint TEXT NOT NULL,
    normalized_policy_json TEXT NOT NULL CHECK(json_valid(normalized_policy_json)),
    source_manifest_hash TEXT NOT NULL,
    outcome TEXT NOT NULL CHECK(outcome IN ('succeeded','failed','cancelled')),
    schema_version INTEGER NOT NULL CHECK(schema_version=1)
) STRICT;

CREATE TABLE export_section (
    profile_id TEXT NOT NULL REFERENCES export_profile(id),
    section TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    canonical_record_count INTEGER CHECK(canonical_record_count>=0),
    retained_record_count INTEGER NOT NULL CHECK(retained_record_count>=0),
    payload_bytes INTEGER NOT NULL CHECK(payload_bytes>=0),
    metadata_bytes INTEGER NOT NULL CHECK(metadata_bytes>=0),
    PRIMARY KEY(profile_id,section)
) STRICT;

CREATE TABLE export_unit (
    profile_id TEXT NOT NULL REFERENCES export_profile(id),
    section TEXT NOT NULL,
    unit_id TEXT NOT NULL REFERENCES record(id),
    family TEXT NOT NULL,
    stable_key TEXT NOT NULL,
    decision TEXT NOT NULL CHECK(decision IN ('included','omitted_by_profile','omitted_by_selection','omitted_by_budget')),
    reason TEXT,
    record_count INTEGER CHECK(record_count>=0),
    payload_bytes INTEGER CHECK(payload_bytes>=0),
    PRIMARY KEY(profile_id,section,unit_id,family),
    CHECK(decision='included' OR (reason IS NOT NULL AND length(trim(reason))>0))
) STRICT;

CREATE TABLE export_omission (
    profile_id TEXT NOT NULL REFERENCES export_profile(id),
    section TEXT NOT NULL,
    reason TEXT NOT NULL,
    unit_count INTEGER CHECK(unit_count>=0),
    record_count INTEGER CHECK(record_count>=0),
    count_state TEXT NOT NULL CHECK(count_state IN ('known','unknown')),
    PRIMARY KEY(profile_id,section,reason),
    CHECK(count_state='known' OR (unit_count IS NULL AND record_count IS NULL))
) STRICT;


CREATE INDEX record_scope_kind ON record(snapshot_id,context_id,kind,id);
CREATE INDEX entity_qualified_name ON entity(qualified_name,id);
CREATE INDEX appearance_entity ON appearance(entity_id,id);
CREATE INDEX relationship_source ON relationship(source_id,kind,id);
CREATE INDEX relationship_destination ON relationship(destination_id,kind,id);
CREATE INDEX occurrence_enclosing ON occurrence(enclosing_id,id);
CREATE INDEX call_site_caller ON call_site(caller_id,id);
CREATE INDEX call_target_destination ON call_target(target_id,call_site_id);
CREATE INDEX operation_body ON operation(body_id,id);
CREATE INDEX source_span_file ON source_span(file_version_id,start_byte,end_byte);
CREATE INDEX coverage_subject ON coverage(subject_id,feature);
