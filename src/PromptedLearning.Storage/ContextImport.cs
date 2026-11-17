using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public sealed record ImportedContext(string ProjectId, string ContextId, IReadOnlyList<string> UnitIds,
    IReadOnlyList<string> DependencyIds, IReadOnlyList<string> ToolIds)
{
    public required string UnitCollectionState { get; init; }
    public string? UnitCollectionReason { get; init; }
}

/// <summary>Persists evaluated inputs. It does not observe a compiler invocation or finalize native includes.</summary>
public static class ContextImport
{
    public static ImportedContext ImportEvaluatedContext(this CanonicalWrite write,
        MsBuildEvaluation evaluation, CapturedInputs capture)
        => write.ImportOperation(() => new Importer(write, evaluation, capture).Run());

    private sealed class Importer(CanonicalWrite write, MsBuildEvaluation evaluation, CapturedInputs capture)
    {
        private readonly Dictionary<string, Dictionary<string, IReadOnlyDictionary<string, object?>>> existing = new(StringComparer.Ordinal);
        private readonly string snapshotId = capture.SnapshotId;
        private string contextId = "";
        private string contextFingerprint = "";
        private string evaluationEvidence = "";
        private string projectSpanId = "";
        private string projectId = "";

        public ImportedContext Run()
        {
            if (evaluation.Language is not ("csharp" or "cpp")) throw new NotSupportedException("Unknown evaluated language.");
            PilotInputGuard.ValidateCapture(evaluation, capture);
            var projectFile = CapturedFileAt(evaluation.ProjectPath);
            if (projectFile.ContentHash != evaluation.ProjectHash)
                throw new InvalidDataException("Captured project bytes differ from the evaluated project hash.");
            RequireStoredCapture(projectFile);
            var sourceKind = evaluation.Language == "csharp" ? "Compile" : "ClCompile";
            var requiredKinds = evaluation.Language == "csharp" ? new[] { "Compile", "ReferencePath" } : new[] { "ClCompile", "ClInclude" };
            if (requiredKinds.Any(kind => !evaluation.EvaluatedItemKinds.Contains(kind)))
                throw new InvalidDataException("Missing evaluated item categories cannot be interpreted as empty inputs.");
            var sources = evaluation.Items.Where(item => item.Kind == sourceKind)
                .Select(item => CapturedFileAt(ItemPath(item))).ToArray();
            if (sources.Length == 0 || sources.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != sources.Length)
                throw new InvalidDataException("Evaluated source inputs are empty or duplicate a captured file.");
            foreach (var source in sources) RequireStoredCapture(source);
            var references = evaluation.Items.Where(item => item.Kind == "ReferencePath").ToArray();
            if (references.Length != evaluation.Providers.Count)
                throw new InvalidDataException("Evaluated references and retained provider descriptors differ in count.");
            foreach (var provider in evaluation.Providers)
            {
                var matches = references.Where(item => SamePhysicalPath(ItemPath(item), provider.PhysicalPath)).ToArray();
                if (matches.Length != 1 || matches[0].Metadata.GetValueOrDefault("AssemblyName") != provider.Descriptor.GetProperty("logical_name").GetString())
                    throw new InvalidDataException("A provider descriptor does not match exactly one evaluated reference.");
            }

            var projectComponents = new
            {
                snapshot_id = snapshotId, definition_file_version_id = projectFile.FileVersionId,
                system_kind = "msbuild", logical_project_key = projectFile.LogicalPath
            };
            projectId = StableId.Create("project/1", projectComponents);
            var contextComponents = MsBuildEvaluator.ContextComponents(evaluation, snapshotId, projectId);
            contextId = StableId.Create("build_context/1", contextComponents);
            contextFingerprint = CanonicalJson.Sha256(CanonicalJson.Serialize(contextComponents));

            var sourceEvidence = write.ReadRows("evidence").SingleOrDefault(row =>
                Text(row, "kind") == "policy" && Text(row, "detail") == "source_snapshot_capture"
                && Text(row, "locator") == "capture:" + snapshotId);
            if (sourceEvidence is null) throw new InvalidDataException("ImportCapturedSources must precede evaluated context import.");
            var sourceEvidenceId = Text(sourceEvidence, "id")!;
            var spanComponents = new
            {
                file_version_id = projectFile.FileVersionId, start_byte = 0L, end_byte = projectFile.ByteLength!.Value,
                origin_kind = projectFile.Origin == "generated" ? "generated" : "written", origin_stack = Array.Empty<object>()
            };
            projectSpanId = StableId.Create("source_span/1", spanComponents);
            Primary("source_span", "BD0.3", projectSpanId, null, spanComponents,
                Row(("id", projectSpanId), ("file_version_id", projectFile.FileVersionId), ("start_byte", 0L),
                    ("end_byte", projectFile.ByteLength.Value), ("origin_kind", spanComponents.origin_kind), ("cause_span_id", null)));
            LinkEvidence(projectSpanId, sourceEvidenceId, "source_capture");
            Primary("project", "BDG.3", projectId, null, projectComponents,
                Row(("id", projectId), ("name", Path.GetFileNameWithoutExtension(evaluation.ProjectPath)),
                    ("definition_file_id", projectFile.Id), ("system_kind", "msbuild"), ("system_name", "MSBuild")), projectSpanId);
            Insert("project_language", Row(("project_id", projectId), ("language", evaluation.Language)));
            LinkEvidence(projectId, sourceEvidenceId, "definition");
            Collection(projectId, "project_language", "project_language", "known", null);

            var toolIds = new List<string>();
            var toolRoles = new Dictionary<string, string>(StringComparer.Ordinal);
            string? buildToolId = null;
            foreach (var tool in evaluation.Tools)
            {
                var components = new { snapshot_id = snapshotId, descriptor = tool.Descriptor };
                var id = StableId.Create("tool/1", components);
                var buildEngine = CanonicalJson.Serialize(tool.Descriptor).SequenceEqual(CanonicalJson.Serialize(evaluation.ToolDescriptor));
                var name = tool.Descriptor.GetProperty("name").GetString()!;
                var role = buildEngine ? "build_engine" : name.StartsWith("compiler:", StringComparison.Ordinal) ? "compiler"
                    : name.StartsWith("analyzer:", StringComparison.Ordinal) ? "analyzer" : "analysis";
                var content = tool.Descriptor.GetProperty("content_fingerprint");
                Primary("tool", "BDB.2", id, null, components,
                    Row(("id", id), ("role", role),
                        ("name", name), ("version", tool.Descriptor.GetProperty("version").GetString()),
                        ("path", tool.PhysicalPath), ("content_hash", KnowledgeText(content)), ("toolset", null)));
                State(id, "tool", [id], "toolset", "not_collected", "toolset_not_part_of_tool_descriptor");
                if (KnowledgeText(content) is null) State(id, "tool", [id], "content_hash", content.GetProperty("state").GetString()!, content.GetProperty("reason").GetString());
                var toolEvidence = Evidence(null, "evaluation", null, id, "record:" + id,
                    CanonicalJson.Sha256(CanonicalJson.Serialize(tool.Descriptor)));
                LinkEvidence(id, toolEvidence, "tool_identity");
                toolIds.Add(id);
                toolRoles.Add(id, role);
                if (buildEngine) buildToolId = id;
            }
            if (buildToolId is null) throw new InvalidDataException("Evaluated tools omit the identified MSBuild engine.");

            var c = contextComponents;
            Primary("build_context", "BDB.1", contextId, contextId, c,
                Row(("id", contextId), ("project_id", projectId), ("solution_id", null), ("configuration_mapping_id", null),
                    ("configuration", String(c, "configuration")), ("platform", String(c, "platform")), ("architecture", String(c, "architecture")),
                    ("target_framework", String(c, "target_framework")), ("runtime_identifier", String(c, "runtime_identifier")),
                    ("language_version", String(c, "language_version")), ("output_identity", String(c, "output_identity")),
                    ("effective_fingerprint", contextFingerprint), ("working_directory", Path.GetDirectoryName(evaluation.ProjectPath))), projectSpanId);
            if (String(c, "target_framework") is null) State(contextId, "build_context", [contextId], "target_framework", "not_applicable", "native_context");
            if (String(c, "runtime_identifier") is null) State(contextId, "build_context", [contextId], "runtime_identifier", "not_applicable", "runtime_identifier_not_selected");
            evaluationEvidence = Evidence(contextId, "evaluation", projectSpanId, buildToolId, "record:" + projectId, contextFingerprint);
            LinkEvidence(contextId, evaluationEvidence, "evaluation");
            foreach (var toolId in toolIds)
                Insert("context_tool", Row(("context_id", contextId), ("tool_id", toolId), ("role", toolRoles[toolId])));
            Collection(contextId, "context_tool", "context_tool", "known", null);

            var optionOrdinal = 0L;
            foreach (var option in c.GetProperty("options").EnumerateArray())
            {
                var path = option.GetProperty("kind").GetString() == "path";
                Insert("context_setting", Row(("context_id", contextId), ("category", "compiler_option"), ("ordinal", optionOrdinal++),
                    ("name", path ? option.GetProperty("option").GetString() : ""),
                    ("value", path ? "<" + option.GetProperty("root_key").GetString() + ">/" + option.GetProperty("relative_path").GetString() : option.GetProperty("value").GetString()),
                    ("evidence_id", evaluationEvidence)));
            }
            Collection(contextId, "context_setting", "context_setting:compiler_option", "known", null);

            var dependencies = new List<string>();
            for (var ordinal = 0; ordinal < evaluation.Providers.Count; ordinal++)
                dependencies.Add(ImportProvider(evaluation.Providers[ordinal].Descriptor, ordinal));
            var unitIds = new List<string>();
            var unitState = evaluation.Language == "csharp" ? "known" : "not_collected";
            var unitReason = evaluation.Language == "csharp" ? null : "native_input_discovery_not_run";
            if (evaluation.Language == "csharp") unitIds.Add(ImportManagedUnit(sources, dependencies, c));
            Collection(contextId, "compilation_unit", "compilation_units", unitState, unitReason);
            return new(projectId, contextId, unitIds.AsReadOnly(), dependencies.AsReadOnly(), toolIds.AsReadOnly())
            { UnitCollectionState = unitState, UnitCollectionReason = unitReason };
        }

        private string ImportProvider(JsonElement provider, int ordinal)
        {
            var kind = provider.GetProperty("kind").GetString() switch
            {
                "assembly" => "assembly", "package" => "package", "native_library" => "native_library",
                "header_library" => "header", "tool" => "tool",
                _ => throw new NotSupportedException("This provider kind has no declared M1A dependency mapping.")
            };
            var name = provider.GetProperty("logical_name").GetString()!;
            var components = Generic("dependency", contextId, contextFingerprint,
                [projectId, null, contextId, name, null, "compile", ordinal]);
            var id = StableId.Create("dependency/1", components);
            var version = provider.GetProperty("version"); var content = provider.GetProperty("content_fingerprint");
            Primary("dependency", "BDB.5", id, contextId, components,
                Row(("id", id), ("requesting_project_id", projectId), ("requesting_action_id", null), ("context_id", contextId),
                    ("requested_name", name), ("version_constraint", null), ("provider_kind", kind), ("selected_name", name),
                    ("selected_version", KnowledgeText(version)), ("selected_path", "<" + provider.GetProperty("root_key").GetString() + ">/" + provider.GetProperty("relative_location").GetString()),
                    ("content_hash", KnowledgeText(content)), ("direct", null), ("purpose", "compile"), ("resolution", "resolved"),
                    ("internal_project_id", null), ("reason", null)), projectSpanId);
            State(id, "dependency", [id], "requesting_action_id", "not_collected", "build_action_graph_not_collected");
            State(id, "dependency", [id], "version_constraint", "not_collected", "requested_version_constraint_not_collected");
            State(id, "dependency", [id], "direct", "unknown", "reference_directness_not_established");
            State(id, "dependency", [id], "internal_project_id", "not_applicable", "external_provider");
            State(id, "dependency", [id], "reason", "not_applicable", "resolved_provider_has_no_failure");
            if (KnowledgeText(version) is null) State(id, "dependency", [id], "selected_version", version.GetProperty("state").GetString()!, version.GetProperty("reason").GetString());
            if (KnowledgeText(content) is null) State(id, "dependency", [id], "content_hash", content.GetProperty("state").GetString()!, content.GetProperty("reason").GetString());
            LinkEvidence(id, evaluationEvidence, "provider_resolution");
            return id;
        }

        private string ImportManagedUnit(CapturedFile[] sources, IReadOnlyList<string> dependencies, JsonElement context)
        {
            const string role = "source";
            var inputs = sources.Select(file => (object)new { role, file_version_id = file.FileVersionId, provider = (object?)null })
                .Concat(evaluation.Providers.Select(provider => (object)new { role = "reference", file_version_id = (string?)null, provider = provider.Descriptor })).ToArray();
            var components = new { context_id = contextId, language = evaluation.Language, inputs, output_identity = String(context, "output_identity") };
            var id = StableId.Create("compilation_unit/1", components);
            Primary("compilation_unit", "BDB.4", id, contextId, components,
                Row(("id", id), ("context_id", contextId), ("language", evaluation.Language),
                    ("main_file_version_id", null),
                    ("output_identity", String(context, "output_identity")), ("effective_fingerprint", CanonicalJson.Sha256(CanonicalJson.Serialize(components))),
                    ("status", "not_attempted")), projectSpanId);
            for (var ordinal = 0; ordinal < sources.Length; ordinal++) UnitInput(id, role, ordinal, sources[ordinal].FileVersionId, null);
            for (var ordinal = 0; ordinal < dependencies.Count; ordinal++) UnitInput(id, "reference", ordinal, null, dependencies[ordinal]);
            Collection(id, "unit_input", "unit_input:" + role, "known", null);
            Collection(id, "unit_input", "unit_input:reference", "known", null);
            LinkEvidence(id, evaluationEvidence, "evaluated_inputs");
            return id;
        }

        private void UnitInput(string unit, string role, int ordinal, string? fileVersion, string? dependency)
        {
            Insert("unit_input", Row(("unit_id", unit), ("ordinal", ordinal), ("role", role), ("file_version_id", fileVersion),
                ("dependency_id", dependency), ("observed_consumed", null), ("evidence_id", evaluationEvidence)));
            State(unit, "unit_input", [unit, role, ordinal], "observed_consumed", "not_collected", "compiler_consumption_not_observed");
        }

        private string Evidence(string? scope, string kind, string? span, string tool, string locator, string fingerprint)
        {
            var components = Generic("evidence", scope, fingerprint,
                [kind, span, tool, null, null, locator, CanonicalJson.Sha256(CanonicalJson.Serialize("build_evaluation"))]);
            var id = StableId.Create("evidence/1", components);
            Primary("evidence", "BD0.16", id, scope, components,
                Row(("id", id), ("kind", kind), ("source_span_id", span), ("tool_id", tool), ("build_run_id", null),
                    ("blob_hash", null), ("locator", locator), ("detail", "build_evaluation")), span);
            if (span is null) State(id, "evidence", [id], "source_span_id", "not_applicable", "tool_identity_evidence");
            State(id, "evidence", [id], "build_run_id", "not_collected", "build_execution_not_observed");
            State(id, "evidence", [id], "blob_hash", "not_collected", "evaluation_log_not_retained");
            return id;
        }

        private object Generic(string family, string? scope, string fingerprint, object?[] parts) => new
        {
            family, scope = new { snapshot_id = snapshotId, context_id = scope, context_independent = scope is null },
            producer_contract = "msbuild-evaluation/1", producer_version = "1", input_fingerprint = fingerprint,
            local_key = new { rule = family + "-local-v1", parts }
        };

        private void Primary(string table, string section, string id, string? scope, object components,
            Dictionary<string, object?> values, string? origin = null)
        {
            var envelope = StableId.Envelope(table + "/1", components);
            StableId.Verify(id, envelope);
            Insert("record", Row(("id", id), ("kind", table), ("section", section), ("snapshot_id", snapshotId),
                ("context_id", scope), ("context_independent", scope is null), ("origin_span_id", origin)));
            Insert(table, values);
            Insert("record_extension", Row(("record_id", id), ("schema_name", "identity-input"), ("schema_version", 1L),
                ("payload_json", Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope)))));
            Collection(id, "record_extension", "record_extension", "known", null);
        }
        private void LinkEvidence(string owner, string evidence, string role)
            => Insert("record_evidence", Row(("record_id", owner), ("evidence_id", evidence), ("role", role)));
        private void State(string owner, string table, object?[] key, string column, string state, string? reason)
            => Insert("field_state", Row(("record_id", owner), ("field_path", "/" + table + "/" + Key(key) + "/" + column),
                ("state", state), ("reason", reason), ("evidence_id", null)));
        private void Collection(string owner, string table, string role, string state, string? reason)
            => Insert("field_state", Row(("record_id", owner), ("field_path", "/" + table + "/@collection/" + role),
                ("state", state), ("reason", reason), ("evidence_id", null)));

        private void Insert(string table, Dictionary<string, object?> row)
        {
            var definition = write.Schema.Tables[table];
            if (!existing.TryGetValue(table, out var index))
                existing.Add(table, index = write.ReadRows(table).ToDictionary(r => Key(definition.PrimaryKey.Select(column => r[column]).ToArray()), StringComparer.Ordinal));
            var key = Key(definition.PrimaryKey.Select(column => SqlValue(row[column])).ToArray());
            if (index.TryGetValue(key, out var prior))
            {
                if (row.Any(pair => !Equals(prior[pair.Key], SqlValue(pair.Value))))
                    throw new InvalidDataException("A stable " + table + " identity already has different retained values.");
                return;
            }
            write.Insert(table, row);
            index.Add(key, row.ToDictionary(pair => pair.Key, pair => SqlValue(pair.Value), StringComparer.Ordinal));
        }
        private void RequireStoredCapture(CapturedFile file)
        {
            if (file.CollectionStatus != "available" || file.ContentHash is null || file.ByteLength is null)
                throw new InvalidDataException("An evaluated input does not have retained source bytes: " + file.LogicalPath);
            if (!write.ReadRows("file_version").Any(row => Text(row, "id") == file.FileVersionId && Text(row, "content_hash") == file.ContentHash))
                throw new InvalidDataException("ImportCapturedSources must precede evaluated context import.");
        }
        private CapturedFile CapturedFileAt(string physicalPath)
        {
            var path = Path.GetFullPath(physicalPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var matches = capture.Files.Where(file => !file.OriginalPath.StartsWith("virtual:", StringComparison.Ordinal)
                && (string.Equals(Path.GetFullPath(file.OriginalPath), path, comparison)
                    || file.Aliases.Any(alias => string.Equals(Path.GetFullPath(alias), path, comparison)))).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException("Evaluated internal input must map to exactly one captured file: " + path);
        }
        private string ItemPath(EvaluatedItem item) => item.Metadata.TryGetValue("FullPath", out var path)
            ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(evaluation.ProjectPath)!, item.Identity));
        private static bool SamePhysicalPath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        private static object? SqlValue(object? value) => value switch { bool b => b ? 1L : 0L, int i => (long)i, _ => value };
        private static string Key(object?[] values) => Convert.ToBase64String(CanonicalJson.Serialize(values)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string? KnowledgeText(JsonElement knowledge) => knowledge.GetProperty("state").GetString() == "known" ? knowledge.GetProperty("value").GetString() : null;
        private static string? String(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetString();
        private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row[column] as string;
        private static Dictionary<string, object?> Row(params (string Name, object? Value)[] fields)
            => fields.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }
}
