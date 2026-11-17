"""Reproduce M0 DDL/envelope checks without running or modifying an extractor.

Usage: python validate-contracts.py --dependency-path <private jsonschema directory>
The dependency directory is explicit; PL_M0_JSONSCHEMA_PATH is an alternative.
JSON is written to stdout. Exit 0 means these design checks passed, not M0/M1.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import sqlite3
import sys
from typing import Callable


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dependency-path", default=os.environ.get("PL_M0_JSONSCHEMA_PATH"))
    options = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    report: dict = {
        "validator": "m0-contracts/1",
        "status": "failed",
        "scope": "Design DDL and JSON envelopes only. No extractor, query service, "
        "archive reader, semantic finalizer, or milestone gate is executed.",
        "checks": [],
    }

    def check(name: str, action: Callable[[], None]) -> None:
        action()
        report["checks"].append({"name": name, "status": "passed"})

    def require(condition: bool, message: str) -> None:
        if not condition:
            raise AssertionError(message)

    try:
        require(bool(options.dependency_path), "Supply --dependency-path or PL_M0_JSONSCHEMA_PATH")
        dependency_path = Path(options.dependency_path).resolve(strict=True)
        sys.path.insert(0, str(dependency_path))
        import jsonschema
        from jsonschema import Draft202012Validator

        require(Path(jsonschema.__file__).resolve().is_relative_to(dependency_path),
                "jsonschema must load from the explicitly supplied private directory")
        report["validation_dependencies"] = {
            name: importlib.metadata.version(name)
            for name in ("jsonschema", "attrs", "jsonschema-specifications", "referencing",
                         "rpds-py", "typing-extensions")
        }
        report["python_version"] = sys.version.split()[0]
        report["sqlite_version"] = sqlite3.sqlite_version
        base = Path(__file__).resolve().parent
        file_bytes = {name: (base / name).read_bytes()
                      for name in ("schema.sql", "contracts.json", "contracts.md")}
        check("explicit_utf8_lf_inputs", lambda: require(
            all(b"\r" not in data and data.decode("utf-8").encode("utf-8") == data
                for data in file_bytes.values()), "Contract inputs must be valid UTF-8 with LF"))
        report["inputs_sha256"] = {
            name: hashlib.sha256(data).hexdigest() for name, data in file_bytes.items()
        }

        connection = sqlite3.connect(":memory:")
        connection.executescript(file_bytes["schema.sql"].decode("utf-8"))
        check("ddl_creates_with_foreign_keys_enabled", lambda: require(
            connection.execute("PRAGMA foreign_keys").fetchone()[0] == 1,
            "DDL did not enable foreign keys"))
        tables = [row[0] for row in connection.execute(
            "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name")]
        report["table_count"] = len(tables)
        report["column_count"] = sum(len(list(connection.execute(
            'PRAGMA table_info("' + table.replace('"', '""') + '")'))) for table in tables)
        check("empty_schema_integrity", lambda: require(
            connection.execute("PRAGMA integrity_check").fetchone()[0] == "ok",
            "SQLite integrity_check failed"))

        # This small population has complete SQL foreign keys. It is not a canonical
        # extraction dataset: product field-path/kind/coverage validators are later work.
        connection.execute("INSERT INTO snapshot VALUES ('s','repo',NULL,0,"
                           "'2026-11-17T00:00:00Z','internal')")
        connection.execute("INSERT INTO record VALUES ('r','fixture','BD0.16','s',NULL,1,NULL)")

        def reject_check(sql: str, arguments: tuple = ()) -> None:
            try:
                connection.execute(sql, arguments)
            except sqlite3.IntegrityError as error:
                require("CHECK constraint failed" in str(error),
                        "Expected CHECK rejection, received: " + str(error))
            else:
                raise AssertionError("SQL accepted an invalid CHECK case")

        for label, reason in (("null", None), ("empty", ""), ("whitespace", "   ")):
            check("unknown_field_rejects_" + label + "_reason", lambda value=reason: reject_check(
                "INSERT INTO field_state VALUES ('r','/fixture/key/value','unknown',?,NULL)",
                (value,)))
        connection.execute("INSERT INTO field_state VALUES "
                           "('r','/fixture/key/value','unknown','missing_dependency',NULL)")
        check("unknown_field_accepts_explained_state", lambda: require(
            connection.execute("SELECT reason FROM field_state").fetchone()[0]
            == "missing_dependency", "Valid reason was not retained"))
        connection.execute("INSERT INTO record VALUES ('s','snapshot','BD0.1','s',NULL,1,NULL)")
        connection.execute("INSERT INTO field_state VALUES "
                           "('s','/snapshot/WyJzIl0/vcs_revision','unknown','not_a_vcs_checkout',NULL)")
        connection.execute("INSERT INTO record VALUES "
                           "('e-snapshot','evidence','BD0.16','s',NULL,1,NULL)")
        connection.execute("INSERT INTO evidence(id,kind,detail) VALUES "
                           "('e-snapshot','policy','explicit_input_policy')")
        connection.execute("INSERT INTO record_evidence VALUES ('s','e-snapshot','acquisition')")
        snapshot_preimage = {
            "schema": "identity-input/1", "domain": "snapshot/1", "components": {
                "repository_identity": "repo", "roots": [{"root_key": "pilot",
                "boundary": "internal", "exclusions": []}], "files": [],
            },
        }
        connection.execute("INSERT INTO record_extension VALUES ('s','identity-input',1,?)",
                           (json.dumps(snapshot_preimage, separators=(",", ":")),))
        check("snapshot_registry_pair_supports_state_evidence_and_identity_input", lambda: require(
            connection.execute("SELECT kind,snapshot_id,context_independent FROM record WHERE id='s'")
            .fetchone() == ("snapshot", "s", 1)
            and connection.execute("SELECT COUNT(*) FROM record_evidence WHERE record_id='s'")
            .fetchone()[0] == 1, "Snapshot lacks a concrete metadata owner"))
        check("seed_foreign_key_integrity", lambda: require(
            connection.execute("PRAGMA foreign_key_check").fetchall() == [],
            "Unexpected dangling foreign key in seed"))
        connection.commit()

        # The following transaction isolates individual SQL checks and positive table
        # representation. Its dependency graph is deliberately incomplete, foreign
        # keys are deferred, and it is always rolled back. No valid populated dataset
        # or semantic-validator result is claimed from these inserts.
        connection.execute("BEGIN")
        connection.execute("PRAGMA defer_foreign_keys=ON")
        isolated_negatives = {
            "unsupported_operation_requires_reason":
                "INSERT INTO operation(id,body_id,representation,operation_kind,compiler_kind,"
                "implicit,semantic_role,supported,unsupported_reason) VALUES "
                "('op','body','semantic','unsupported','Other',0,'statement',0,NULL)",
            "unknown_effect_requires_reason":
                "INSERT INTO data_flow_fact(id,result_id,family,meaning,reason) VALUES "
                "('df','ar','unknown_effect','unknown',NULL)",
            "incomplete_call_requires_remainder":
                "INSERT INTO call_site(id,caller_id,occurrence_id,dispatch,"
                "candidate_completeness,remainder_reason) VALUES "
                "('call','caller','occ','dynamic','incomplete',NULL)",
            "partial_coverage_requires_reason":
                "INSERT INTO coverage(id,producer_id,subject_id,feature,status,reason) VALUES "
                "('cov','tool','subject','feature','partial',NULL)",
            "skipped_execution_requires_reason":
                "INSERT INTO action_execution(id,run_id,outcome,reason) VALUES "
                "('exec','run','skipped',NULL)",
            "omitted_reference_requires_reason":
                "INSERT INTO reference_availability VALUES ('target','omitted_by_profile',NULL)",
            "omitted_unit_requires_reason":
                "INSERT INTO export_unit VALUES "
                "('profile','BD0.7','body','operations','key','omitted_by_profile',NULL,1,1)",
            "membership_context_key_must_match":
                "INSERT INTO group_membership VALUES ('g','m','c1','c2','physical','e')",
            "item_metadata_requires_owner":
                "INSERT INTO build_setting(id,context_id,category,name,ordinal,value,stage,"
                "generic_schema_version) VALUES ('bs','ctx','metadata','Private',0,'true','evaluated',1)",
            "invalid_access_is_rejected":
                "INSERT INTO entity(id,language,entity_kind,identity_scheme,identity_version,"
                "origin,access,resolution) VALUES "
                "('badentity','cpp','function','usr','1','written','friend_only','resolved')",
            "invalid_return_passing_is_rejected":
                "INSERT INTO signature(id,callable_id,callable_kind,return_passing,body_state,display) "
                "VALUES ('badsig','e','function','magic','written','f()')",
            "invalid_file_role_is_rejected":
                "INSERT INTO source_file(id,root_id,relative_path,original_path,name,extension,role,origin) "
                "VALUES ('badfile','root','x','x','x','','unknown_magic','written')",
        }
        for name, statement in isolated_negatives.items():
            check(name, lambda sql=statement: reject_check(sql))
        connection.executemany("INSERT INTO group_membership VALUES (?,?,?,?,?,?)", [
            ("g", "m", "c1", "c1", "physical", "e"),
            ("g", "m", "c2", "c2", "physical", "e"),
            ("g", "m", None, "independent", "physical", "e"),
        ])
        check("same_membership_retains_two_contexts_and_independent_scope", lambda: require(
            connection.execute("SELECT COUNT(*) FROM group_membership").fetchone()[0] == 3,
            "Contextual memberships collided"))
        connection.execute("INSERT INTO file_version(id,file_id,content_hash,byte_length,encoding,"
                           "bom_bytes,available,extraction_status) VALUES "
                           "('binary','file','hash',7,NULL,NULL,1,'not_attempted')")
        check("retained_binary_bytes_do_not_require_encoding", lambda: require(
            connection.execute("SELECT available,encoding FROM file_version").fetchone() == (1, None),
            "Byte availability was conflated with decoding"))
        connection.executemany("INSERT INTO entity_unit VALUES (?,?,?)", [
            ("entity", "tu1", "declaration"), ("entity", "tu2", "definition")])
        check("one_entity_retains_multiple_compilation_units", lambda: require(
            connection.execute("SELECT COUNT(*) FROM entity_unit WHERE entity_id='entity'")
            .fetchone()[0] == 2, "Unit membership was overwritten"))
        connection.executemany("INSERT INTO build_item(id,context_id,item_type,include_value,ordinal,stage) "
                               "VALUES (?,?,?,?,?,?)", [
            ("i1", "ctx", "Reference", "A", 0, "evaluated"),
            ("i2", "ctx", "Reference", "B", 1, "evaluated"),
        ])
        connection.executemany("INSERT INTO build_setting(id,context_id,category,name,ordinal,value,"
                               "item_id,stage,generic_schema_version) VALUES (?,?,?,?,?,?,?,?,?)", [
            ("s1", "ctx", "metadata", "Private", 0, "true", "i1", "evaluated", 1),
            ("s2", "ctx", "metadata", "Private", 0, "false", "i2", "evaluated", 1),
        ])
        check("nonfile_item_metadata_retains_distinct_owners", lambda: require(
            connection.execute("SELECT item_id,value FROM build_setting ORDER BY item_id").fetchall()
            == [("i1", "true"), ("i2", "false")], "Non-file item metadata lost ownership"))
        connection.executemany("INSERT INTO parameter(id,signature_id,ordinal,name,passing_mode,optional,"
                               "variadic,default_value_kind,default_value) VALUES (?,?,?,?,?,?,?,?,?)", [
            ("p1", "sig", 0, "a", "value", 1, 0, "null", "null"),
            ("p2", "sig", 1, "b", "value", 1, 0, "string", "null"),
        ])
        check("known_null_and_string_null_defaults_stay_distinct", lambda: require(
            connection.execute("SELECT default_value_kind,default_value FROM parameter ORDER BY ordinal")
            .fetchall() == [("null", "null"), ("string", "null")], "Default tags were lost"))
        connection.rollback()
        check("isolated_inserts_rolled_back", lambda: require(
            connection.execute("SELECT COUNT(*) FROM group_membership").fetchone()[0] == 0
            and connection.execute("PRAGMA foreign_key_check").fetchall() == [],
            "Isolated test transaction was not rolled back"))
        connection.close()

        schemas = json.loads(file_bytes["contracts.json"].decode("utf-8"))
        check("draft202012_schema_is_well_formed", lambda: Draft202012Validator.check_schema(schemas))
        validator = Draft202012Validator(schemas)
        request = {
            "schema": "query/1", "profile_id": "P4", "dataset_fingerprint": "a" * 64,
            "snapshot_id": "snapshot:x", "context_id": "context:x", "operation": "capabilities",
            "arguments": {}, "limits": {"records": 100, "response_bytes": 65536, "time_ms": 5000},
        }
        check("capabilities_request_validates", lambda: validator.validate(request))
        mutations = {
            "query_unknown_top_level_rejected": lambda q: q.update(oops=True),
            "query_unknown_operation_rejected": lambda q: q.update(operation="sql"),
            "query_unknown_argument_rejected": lambda q: q.update(arguments={"sql": "select *"}),
            "query_negative_limit_rejected": lambda q: q["limits"].update(records=-1),
            "query_graph_without_limits_rejected": lambda q: q.update(
                operation="groups", arguments={"kinds": ["directory"]}),
        }
        for name, mutate in mutations.items():
            invalid = copy.deepcopy(request)
            mutate(invalid)
            check(name, lambda value=invalid: require(not validator.is_valid(value),
                                                      "Invalid query envelope accepted"))
        graph = copy.deepcopy(request)
        graph.update(operation="groups", arguments={"kinds": ["directory"]})
        graph["limits"].update(nodes=100, depth=3)
        check("bounded_groups_request_validates", lambda: validator.validate(graph))

        # Source-only envelopes must not offer a route to semantic/canonical facts.
        source_bindings = {
            "arm_id": "source", "capture_index_fingerprint": "b" * 64,
            "snapshot_id": "snapshot:x", "context_id": "context:x",
        }
        source_request = {
            "schema": "source-query/1", **source_bindings,
            "operation": "source_capabilities", "arguments": {},
            "limits": {"records": 100, "response_bytes": 65536, "time_ms": 5000,
                       "source_bytes": 1048576},
        }
        check("source_capabilities_request_validates", lambda: validator.validate(source_request))
        search = copy.deepcopy(source_request)
        search.update(operation="source_search", arguments={
            "text": "Add", "match_kind": "literal", "case_policy": "ordinal_case_sensitive",
            "path_prefix": "", "roles": "all", "file_version_ids": "all", "snippet_bytes": 256,
        })
        check("literal_source_search_request_validates", lambda: validator.validate(search))
        source_mutations = {
            "source_rejects_canonical_database_selector": lambda q: q.update(canonical_database="full.sqlite"),
            "source_rejects_semantic_operation": lambda q: q.update(operation="entity"),
            "source_rejects_regex_search": lambda q: q["arguments"].update(match_kind="regex"),
            "source_rejects_locale_case_policy": lambda q: q["arguments"].update(case_policy="ignore_case"),
            "source_rejects_negative_read_budget": lambda q: q["limits"].update(source_bytes=-1),
            "source_rejects_parent_path_filter": lambda q: q["arguments"].update(path_prefix="../outside"),
            "source_rejects_absolute_path_filter": lambda q: q["arguments"].update(path_prefix="C:\\outside"),
        }
        for name, mutate in source_mutations.items():
            invalid = copy.deepcopy(search)
            mutate(invalid)
            check(name, lambda value=invalid: require(not validator.is_valid(value),
                                                      "Invalid source request envelope accepted"))
        encoding = {"state": "known", "name": "utf-8", "bom_bytes": 0, "reason": None}
        source_hash = hashlib.sha256(b"int x;\n").hexdigest()
        captured_file = {
            "file_id": "file:x", "file_version_id": "file-version:x", "root_name": "pilot",
            "logical_path": "pilot/test.cpp", "role": "source", "language": "cpp",
            "origin": "written", "content_hash": source_hash, "byte_length": 7,
            "collection_status": "available", "collection_reason": None, "encoding": encoding,
            "decoding": "valid", "decoding_reason": None,
            "context_memberships": [{"context_id": "context:x", "selection": "included", "reason": None}],
        }
        collection_policy = {
            family: {"requested": False, "supplied_file_version_ids": [],
                     "collection_state": "not_collected",
                     "reason": "not_requested_and_no_supplied_evidence"}
            for family in ("build_observation", "artifact_inspection", "runtime_observation")
        }
        policy_fingerprint = hashlib.sha256((json.dumps(
            collection_policy, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")).hexdigest()
        capture = {
            "schema": "capture-index/1", "serializer_version": "pl-jsonl/1", "snapshot_id": "snapshot:x",
            "source_manifest_hash": "a" * 64, "context_ids": ["context:x"],
            "roots": [{"logical_name": "pilot", "boundary": "internal", "exclusion_rules": []}],
            "files": [captured_file], "collection_policy": collection_policy,
            "collection_policy_fingerprint": policy_fingerprint,
        }
        check("capture_index_with_source_metadata_validates", lambda: validator.validate(capture))
        invalid_capture = copy.deepcopy(capture)
        invalid_capture["files"][0]["entity_count"] = 4
        check("capture_index_rejects_semantic_facts", lambda: require(
            not validator.is_valid(invalid_capture), "Capture index accepted entity facts"))
        invalid_capture = copy.deepcopy(capture)
        invalid_capture["files"][0]["content_hash"] = None
        check("available_capture_requires_hash", lambda: require(
            not validator.is_valid(invalid_capture), "Available bytes accepted without hash"))
        invalid_capture = copy.deepcopy(capture)
        del invalid_capture["collection_policy"]
        check("capture_requires_explicit_collection_policy", lambda: require(
            not validator.is_valid(invalid_capture), "Capture inferred policy instead of requiring it"))
        invalid_capture = copy.deepcopy(capture)
        invalid_capture["collection_policy"]["build_observation"]["build_succeeded"] = True
        check("capture_policy_rejects_observation_conclusion", lambda: require(
            not validator.is_valid(invalid_capture), "Capture policy accepted an observed build conclusion"))
        invalid_capture = copy.deepcopy(capture)
        invalid_capture["collection_policy"]["artifact_inspection"]["collection_state"] = "evidence_supplied"
        check("supplied_evidence_policy_requires_file_ids", lambda: require(
            not validator.is_valid(invalid_capture), "Evidence-supplied policy accepted no evidence files"))
        known = lambda value: {"state": "known", "value": value, "reason": None}
        capabilities_item = {
            "kind": "capabilities", "api_version": "source-query/1", "serializer_version": "pl-jsonl/1",
            "source_only": True, "operations": ["source_capabilities", "source_files", "source_search", "source"],
            "formats": ["bytes", "text"], "files_total": known(1), "files_available": known(1),
            "files_undecodable": known(0), "hard_limits": {
                "records_max": 1000, "response_bytes_max": 1048576,
                "time_ms_max": 60000, "source_bytes_max": 1048576,
            }, "collection_policy": collection_policy, "collection_policy_fingerprint": policy_fingerprint,
            "collection_policy_citations": [{
                "family": family,
                "citation_id": "capture/" + source_bindings["capture_index_fingerprint"]
                + "/policy/" + policy_fingerprint + "/" + family,
            } for family in collection_policy],
        }
        source_response = {
            "schema": "source-response/1", **source_bindings, "request_fingerprint": "c" * 64,
            "operation": "source_capabilities", "status": "ok", "items": [capabilities_item],
            "coverage": {"status": "complete", "selected_files": known(1), "visited_files": known(0),
                         "unsearchable_files": known(0), "unvisited_files": known(1), "files": [], "reason": None},
            "delivery": {"state": "complete", "next_cursor": None, "reason": None},
            "metrics": {"elapsed_ns": known(1), "records": known(1), "response_bytes": known(1024),
                        "source_bytes": known(0)}, "diagnostics": [],
        }
        check("source_only_capability_response_validates", lambda: validator.validate(source_response))
        invalid_response = copy.deepcopy(source_response)
        invalid_response["items"][0]["semantic_features"] = ["call_graph"]
        check("source_capabilities_rejects_semantic_feature_inventory", lambda: require(
            not validator.is_valid(invalid_response), "Source capability response accepted semantic facts"))
        source_cursor = {
            "schema": "source-cursor/1", **source_bindings, "operation": "source_search",
            "request_fingerprint": "c" * 64, "logical_path": "pilot/test.cpp",
            "file_version_id": "file-version:x", "next_candidate_byte": 0,
        }
        check("bound_source_cursor_shape_validates", lambda: validator.validate(source_cursor))
        invalid_cursor = copy.deepcopy(source_cursor)
        invalid_cursor["filesystem_path"] = "unselected.cpp"
        check("source_cursor_rejects_arbitrary_filesystem_path", lambda: require(
            not validator.is_valid(invalid_cursor), "Source cursor accepted a filesystem path"))
        native_attribute = {
            "schema": "cpp-attribute/1", "written_name": "deprecated", "namespace": None,
            "syntax": "double_bracket",
        }
        check("builtin_cpp_attribute_spelling_validates_without_type", lambda: validator.validate(native_attribute))
        invalid_attribute = copy.deepcopy(native_attribute)
        invalid_attribute["attribute_type"] = "invented_class"
        check("builtin_cpp_extension_rejects_fabricated_type_field", lambda: require(
            not validator.is_valid(invalid_attribute), "Native spelling extension accepted a fabricated type field"))

        # Exact table ownership is a design input, not an implemented projector.
        # These checks validate the matrix and small adversarial ownership models.
        inspection = sqlite3.connect(":memory:")
        inspection.executescript(file_bytes["schema.sql"].decode("utf-8"))
        table_names = {row[0] for row in inspection.execute(
            "SELECT name FROM sqlite_master WHERE type='table'")}
        ownership = schemas["x-profile-table-ownership"]
        matrix = ownership["tables"]
        check("ownership_matrix_covers_every_table_exactly", lambda: require(
            set(matrix) == table_names, "Ownership matrix omits or invents tables"))
        for table, rule in matrix.items():
            columns = {row[1]: row for row in inspection.execute("PRAGMA table_info(" + table + ")")}
            actual_key = [row[1] for row in sorted(columns.values(), key=lambda row: row[5]) if row[5]]
            require(rule["primary_key"] == actual_key, "Wrong matrix key: " + table)
            join = rule.get("owner", rule.get("unit"))
            if join:
                require(join["local_column"] in columns, "Missing ownership column: " + table)
                target_columns = {row[1] for row in inspection.execute(
                    "PRAGMA table_info(" + join["table"] + ")")}
                require(join["owner_column"] in target_columns, "Missing owner key: " + table)
        check("ownership_keys_and_joins_match_ddl", lambda: None)
        inspection.close()
        check("optional_family_tables_are_exact", lambda: require(
            ownership["optional_sections"] == {
                "BD0.7": ["operation", "operation_child", "operation_operand"],
                "BD0.12": ["cfg_block", "cfg_edge", "cfg_operation"],
                "BD0.13": ["data_flow_fact"],
            }, "Unexpected optional family ownership"))

        def projection_fixture_valid(fixture: dict) -> bool:
            """Check a finite adversarial model; no canonical/profile DB is projected."""
            typed = set(fixture["typed_payload"])
            anchors = set(fixture["anchors"])
            retained_rows = set(fixture["retained_rows"])
            if typed & anchors:
                return False
            for row in fixture["owned_rows"]:
                rule = matrix[row["table"]]
                if rule["mode"] not in ("owned_child", "owned_metadata"):
                    return False
                if row["owner"] not in typed:
                    return False
                if row["table"] == "field_state" and row["addressed_row"] not in retained_rows:
                    return False
            allowed = set(ownership["policy_rules"]["analysis_dependency_allowed_target_kinds"])
            if any(kind not in allowed for kind in fixture["dependency_target_kinds"]):
                return False
            provenance = schemas["x-provenance-policy"]
            policy = provenance["analysis_policies"][fixture["analysis_policy"]]
            if fixture["guarantee_text"] != policy["guarantee_text"]:
                return False
            if any(value not in policy["assumptions"] for value in fixture["assumptions"]):
                return False
            if fixture["status_text_kind"] not in ("compiler_diagnostic", "acquisition_failure",
                                                    "unsupported_feature", "none"):
                return False
            for evidence in fixture["evidence"]:
                if evidence["owner"] not in typed:
                    return False
                if evidence["detail"] not in provenance["evidence_detail_allowed"]:
                    return False
                if evidence["blob_origin"] not in (None, "captured_input_before_analysis"):
                    return False
            return True

        projection = {
            "typed_payload": ["body:b", "analysis_result:r", "call_site:c"],
            "anchors": ["operation:o"], "retained_rows": ["call_site:c"],
            "owned_rows": [{"table": "field_state", "owner": "call_site:c",
                            "addressed_row": "call_site:c"}],
            "dependency_target_kinds": ["body", "build_context", "tool"],
            "analysis_policy": "direct_calls_v1",
            "guarantee_text": schemas["x-provenance-policy"]["analysis_policies"]
            ["direct_calls_v1"]["guarantee_text"],
            "assumptions": [],
            "status_text_kind": "compiler_diagnostic",
            "evidence": [{"owner": "call_site:c", "detail": "compiler_call_target",
                          "blob_origin": None}],
        }
        check("retained_conclusion_can_reference_minimal_operation_anchor", lambda: require(
            projection_fixture_valid(projection), "Valid minimal anchor model rejected"))
        for table in ("field_state", "record_evidence", "record_extension", "operation_child",
                      "operation_operand", "member_relation", "template_substitution"):
            invalid = copy.deepcopy(projection)
            invalid["owned_rows"].append({"table": table, "owner": "operation:o",
                                          "addressed_row": "operation:o"})
            check("anchor_cannot_pull_" + table, lambda case=invalid: require(
                not projection_fixture_valid(case), "Anchor expanded omitted payload/metadata"))
        invalid = copy.deepcopy(projection)
        invalid["owned_rows"][0]["addressed_row"] = "operation_operand:hidden_pk"
        check("field_state_cannot_reveal_omitted_child_key", lambda: require(
            not projection_fixture_valid(invalid), "Metadata exposed an omitted child key"))
        invalid = copy.deepcopy(projection)
        invalid["dependency_target_kinds"].append("operation")
        check("analysis_dependency_cannot_catalog_omitted_operations", lambda: require(
            not projection_fixture_valid(invalid), "Header dependencies revealed omitted node catalog"))
        invalid = copy.deepcopy(projection)
        invalid["guarantee_text"] = "operation:o assigns the literal 12345"
        check("analysis_header_cannot_embed_optional_findings", lambda: require(
            not projection_fixture_valid(invalid), "Header smuggled derived findings"))
        invalid = copy.deepcopy(projection)
        invalid["assumptions"] = ["block:1 -> block:2 -> block:3"]
        check("analysis_assumptions_cannot_embed_optional_graph", lambda: require(
            not projection_fixture_valid(invalid), "Assumptions smuggled omitted graph"))
        for content_kind in ("ast_dump", "cfg_dump", "dataflow_dump"):
            invalid = copy.deepcopy(projection)
            invalid["evidence"][0]["blob_origin"] = content_kind
            check("evidence_cannot_embed_" + content_kind, lambda case=invalid: require(
                not projection_fixture_valid(case), "Evidence restored omitted optional payload"))
        invalid = copy.deepcopy(projection)
        invalid["evidence"][0]["detail"] = "hidden literal 12345 and CFG edges"
        check("evidence_detail_rejects_unregistered_fact_text", lambda: require(
            not projection_fixture_valid(invalid), "Evidence detail restored hidden facts"))
        invalid = copy.deepcopy(projection)
        invalid["status_text_kind"] = "successful_optional_payload_dump"
        check("diagnostics_and_coverage_cannot_dump_omitted_successful_payload", lambda: require(
            not projection_fixture_valid(invalid), "Status text restored omitted payload"))

        manifest = {"schema": "source-manifest/1", "blobs": [{
            "hash": source_hash, "byte_length": 7,
            "storage_key": "sha256/" + source_hash[:2] + "/" + source_hash,
        }]}
        check("source_storage_key_is_relative_to_sources_root", lambda: validator.validate(manifest))
        invalid = copy.deepcopy(manifest)
        invalid["blobs"][0]["storage_key"] = "sources/" + invalid["blobs"][0]["storage_key"]
        check("source_storage_key_rejects_duplicate_sources_component", lambda: require(
            not validator.is_valid(invalid), "Ambiguous archive-relative storage key accepted"))

        preparation = {
            "schema": "source-archive-preparation/1", "capture_index_fingerprint": "a" * 64,
            "source_manifest_hash": "b" * 64, "mode": "windows_pinned_read_handles_v1",
            "max_open_blobs": 4096, "max_prepare_bytes": 8589934592,
            "max_prepare_time_ms": 120000, "hash_buffer_bytes": 65536,
            "verified_bytes": 7, "unique_blobs": 1, "peak_open_handles": 1,
            "elapsed_ms": 1, "outcome": "passed", "reason": None,
        }
        check("explicit_archive_preparation_report_validates", lambda: validator.validate(preparation))
        for name, changes in {
            "preparation_cannot_pass_over_byte_limit": {"verified_bytes": 8589934593},
            "preparation_cannot_pass_over_handle_limit": {"peak_open_handles": 4097},
            "preparation_cannot_pass_over_time_limit": {"elapsed_ms": 120001},
            "failed_preparation_requires_reason": {"outcome": "failed"},
            "preparation_disallows_unverified_path_reopen_mode": {"mode": "reopen_each_query"},
        }.items():
            invalid = {**preparation, **changes}
            check(name, lambda case=invalid: require(not validator.is_valid(case),
                                                     "Invalid preparation report accepted"))
        failed_preparation = {**preparation, "outcome": "failed", "reason": "blob_hash_mismatch"}
        check("same_length_tamper_failure_report_validates", lambda: validator.validate(failed_preparation))
        # This is a hash test, not an OS sharing-lock or archive-reader implementation test.
        check("same_length_changed_bytes_have_different_digest", lambda: require(
            len(b"int x;\n") == len(b"int y;\n")
            and hashlib.sha256(b"int x;\n").digest() != hashlib.sha256(b"int y;\n").digest(),
            "Same-length integrity example did not differ"))
        report["projection_test_boundary"] = (
            "Ownership keys/joins checked against DDL, then finite adversarial models checked. "
            "No exporter, semantic text classifier, OS pinning guarantee or archive reader was executed."
        )
        report["status"] = "passed"
        report["isolated_sql_test_boundary"] = (
            "Typed-table checks used a deliberately incomplete deferred-FK transaction, then rollback. "
            "Their positive inserts prove representability and constraints only, not semantic validity."
        )
    except Exception as error:
        report["error"] = {"type": type(error).__name__, "message": str(error)}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
