using System.Globalization;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

/// <summary>Frozen compiler evidence plus explicit shared-tree expectations, independent of adapter traversal.</summary>
public static class ManagedChooseOracle
{
    private const string ChooseIdentity = "M:Pilot.Arithmetic.Choose(System.Int32)";
    private const string AddIdentity = "M:Pilot.Arithmetic.Add(System.Int32,System.Int32)";
    private sealed record Expected(string Path, string Kind, string ChildRole, string SemanticRole, string[] OperandRoles);

    public static void Assert(SemanticUnitResult result, JsonElement oracle)
    {
        Check.Equal(0, oracle.GetProperty("compilation_errors").GetInt32());
        Check.Equal("4.14.0.0", Text(oracle, "roslyn"));
        Check.Equal(22, oracle.GetProperty("node_count").GetInt32());
        var entities = Index(result, "entities");
        var types = Index(result, "types");
        var spans = Index(result, "spans");
        var choose = entities.Values.Single(row => Text(row, "compiler_key") == ChooseIdentity);
        var add = entities.Values.Single(row => Text(row, "compiler_key") == AddIdentity);
        var chooseParameter = entities.Values.Single(row => Text(row, "entity_kind") == "parameter"
            && Text(row, "owner_entity_key") == Text(choose, "key") && row.GetProperty("declared_ordinal").GetInt32() == 0);
        var local = entities.Values.Single(row => Text(row, "entity_kind") == "local_variable"
            && Text(row, "owner_entity_key") == Text(choose, "key") && Text(row, "written_name") == "result");
        var addParameters = entities.Values.Where(row => Text(row, "entity_kind") == "parameter"
            && Text(row, "owner_entity_key") == Text(add, "key")).OrderBy(row => row.GetProperty("declared_ordinal").GetInt32()).ToArray();
        Check.Equal(2, addParameters.Length);
        Check.Equal("value", Text(chooseParameter, "written_name"));
        Check.Equal("parameter", Text(chooseParameter, "compiler_identity_kind"));
        Check.Equal("source_local", Text(local, "compiler_identity_kind"));
        Check.True(local.GetProperty("compiler_key").ValueKind == JsonValueKind.Null);
        Check.True(chooseParameter.GetProperty("compiler_key").ValueKind == JsonValueKind.Null);
        Check.Equal("System.Int32", Text(local.GetProperty("compiler_type_key"), "name"));
        Check.Equal("System.Int32", Text(chooseParameter.GetProperty("compiler_type_key"), "name"));

        var body = Rows(result, "bodies").Single(row => Text(row, "owner_entity_key") == Text(choose, "key"));
        var operations = Rows(result, "operations").Where(row => Text(row, "body_key") == Text(body, "key")).ToArray();
        var byPath = operations.ToDictionary(NodePath, StringComparer.Ordinal);
        var oracleNodes = oracle.GetProperty("nodes").EnumerateArray().ToDictionary(row => Text(row, "path")!, StringComparer.Ordinal);
        Expected[] expected =
        [
            new("0", "block", "root", "block", ["operand", "operand", "operand"]),
            new("0/0", "variable_declaration_group", "statement", "evaluation", ["operand"]),
            new("0/0/0", "variable_declaration", "declaration", "evaluation", ["operand"]),
            new("0/0/0/0", "variable_declarator", "declarator", "declaration", []),
            new("0/1", "if", "statement", "condition", ["condition", "operand", "operand"]),
            new("0/1/0", "binary", "condition", "evaluation", ["operand", "operand"]),
            new("0/1/0/0", "reference", "left", "read", []),
            new("0/1/0/1", "literal", "right", "constant", []),
            new("0/1/1", "expression_statement", "true", "evaluation", ["operand"]),
            new("0/1/1/0", "assignment", "expression", "assignment", ["destination", "operand"]),
            new("0/1/1/0/0", "reference", "destination", "destination", []),
            new("0/1/1/0/1", "invocation", "value", "call", ["argument", "argument"]),
            new("0/1/1/0/1/0", "argument", "argument", "argument", ["argument"]),
            new("0/1/1/0/1/0/0", "reference", "value", "read", []),
            new("0/1/1/0/1/1", "argument", "argument", "argument", ["argument"]),
            new("0/1/1/0/1/1/0", "literal", "value", "constant", []),
            new("0/1/2", "expression_statement", "false", "evaluation", ["operand"]),
            new("0/1/2/0", "assignment", "expression", "assignment", ["destination", "operand"]),
            new("0/1/2/0/0", "reference", "destination", "destination", []),
            new("0/1/2/0/1", "literal", "value", "constant", []),
            new("0/2", "return", "statement", "return", ["return"]),
            new("0/2/0", "reference", "return", "read", [])
        ];
        Check.Equal(expected.Length, operations.Length);
        Check.Equal(expected.Length, oracleNodes.Count);
        Check.True(expected.Select(node => node.Path).ToHashSet(StringComparer.Ordinal).SetEquals(byPath.Keys));
        Check.True(expected.Select(node => node.Path).ToHashSet(StringComparer.Ordinal).SetEquals(oracleNodes.Keys));
        Check.Equal(Text(byPath["0"], "key"), Text(body, "root_operation_key"));
        Check.Equal("complete", Text(body, "status"));
        Check.Equal("semantic", Text(body, "representation"));
        Check.Equal(Text(byPath["0/0/0/0"], "span_key"), Text(local, "origin_span_key"), "Local identity must retain its declarator origin.");

        foreach (var expectation in expected)
        {
            var path = expectation.Path;
            var actual = byPath[path];
            var independent = oracleNodes[path];
            Check.Equal(Text(independent, "kind"), Text(actual, "compiler_kind"), path + ": compiler operation kind");
            Check.Equal(expectation.Kind, Text(actual, "operation_kind"), path + ": shared operation kind");
            Check.Equal(expectation.ChildRole, Text(actual, "child_role"), path + ": structural child role");
            Check.Equal(expectation.SemanticRole, Text(actual, "semantic_role"), path + ": semantic role");
            Check.Equal("semantic", Text(actual, "representation"));
            Check.True(actual.GetProperty("supported").GetBoolean());
            Check.True(actual.GetProperty("unsupported_reason").ValueKind == JsonValueKind.Null);
            Check.True(actual.GetProperty("execution_point").ValueKind == JsonValueKind.Null);
            Check.Equal(independent.GetProperty("implicit_node").GetBoolean(), actual.GetProperty("implicit").GetBoolean());
            var parentPath = ParentPath(path);
            Check.Equal(parentPath is null ? null : Text(byPath[parentPath], "key"), Text(actual, "parent_operation_key"), path + ": exact parent");
            if (parentPath is null) Check.True(actual.GetProperty("child_ordinal").ValueKind == JsonValueKind.Null);
            else Check.Equal(int.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture), actual.GetProperty("child_ordinal").GetInt32());

            var type = Text(independent, "type");
            if (type is null) Check.True(actual.GetProperty("type_key").ValueKind == JsonValueKind.Null, path + ": inapplicable type");
            else
            {
                var primitive = types[Text(actual, "type_key")!].GetProperty("compiler_type_key");
                Check.Equal("builtin", Text(primitive, "tag"));
                Check.Equal(type switch { "int" => "System.Int32", "bool" => "System.Boolean", _ => throw new Exception("Unmapped oracle type: " + type) }, Text(primitive, "name"));
            }

            var target = Text(independent, "target");
            var expectedEntity = target switch
            {
                null => (string?)null,
                "value" => Text(chooseParameter, "key"),
                "result" => Text(local, "key"),
                "Pilot.Arithmetic.Add(int, int)" => Text(add, "key"),
                _ => throw new Exception("Unmapped independent oracle target: " + target)
            };
            Check.Equal(expectedEntity, Text(actual, "referenced_entity_key"), path + ": resolved identity");
            var constant = Text(independent, "constant");
            if (constant is null) Check.True(actual.GetProperty("constant").ValueKind == JsonValueKind.Null, path + ": no constant");
            else
            {
                Check.Equal("signed_integer", Text(actual.GetProperty("constant"), "kind"));
                Check.Equal(constant, Text(actual.GetProperty("constant"), "value"), path + ": exact oracle constant");
            }

            var specific = Text(independent, "specific");
            if (specific == "GreaterThan") Check.Equal(specific, Text(actual, "operator_kind"));
            else
            {
                Check.True(actual.GetProperty("operator_kind").ValueKind == JsonValueKind.Null);
                if (specific is not null)
                {
                    // The bounded wire represents Explicit by a written, nonimplicit
                    // Argument node and its exact callable parameter/value mapping.
                    Check.Equal("Explicit", specific);
                    Check.Equal("argument", expectation.Kind);
                    Check.True(!actual.GetProperty("implicit").GetBoolean());
                    Check.Equal("invocation", Text(byPath[parentPath!], "operation_kind"));
                }
            }

            var span = spans[Text(actual, "span_key")!];
            Check.Equal("written", Text(span, "origin_kind"));
            Check.Equal(0, span.GetProperty("origin_stack").GetArrayLength());
            Check.True(span.GetProperty("cause_span_key").ValueKind == JsonValueKind.Null);
            Check.True(actual.GetProperty("cause_span_key").ValueKind == JsonValueKind.Null);
            Check.Equal(Text(spans[Text(byPath["0"], "span_key")!], "file_key"), Text(span, "file_key"));

            var children = expected.Where(node => ParentPath(node.Path) == path).ToArray();
            var operands = actual.GetProperty("operands").EnumerateArray().ToArray();
            Check.Equal(children.Length, expectation.OperandRoles.Length);
            Check.Equal(children.Length, operands.Length, path + ": exact operand count");
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < operands.Length; index++)
            {
                var role = expectation.OperandRoles[index]; var ordinal = ordinals.GetValueOrDefault(role); ordinals[role] = ordinal + 1;
                Check.Equal(role, Text(operands[index], "role"));
                Check.Equal(ordinal, operands[index].GetProperty("ordinal").GetInt32());
                Check.Equal(Text(byPath[children[index].Path], "key"), Text(operands[index], "target_key"), path + ": exact operand target");
                var argumentOrdinal = expectation.Kind == "invocation" ? index : expectation.Kind == "argument"
                    ? int.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture) : -1;
                Check.Equal(argumentOrdinal < 0 ? null : Text(addParameters[argumentOrdinal], "key"), Text(operands[index], "parameter_entity_key"), path + ": exact parameter binding");
            }
        }
        var addSignature = Rows(result, "signatures").Single(row => Text(row, "owner_entity_key") == Text(add, "key"));
        foreach (var parameter in addSignature.GetProperty("parameters").EnumerateArray())
        {
            Check.True(!parameter.GetProperty("optional").GetBoolean() && !parameter.GetProperty("variadic").GetBoolean());
            Check.Equal("value", Text(parameter, "passing_mode"));
        }
    }

    private static string? ParentPath(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : null;
    private static string NodePath(JsonElement row) => string.Join('/', row.GetProperty("structural_path").EnumerateArray().Select(value => value.GetInt32()));
    private static JsonElement[] Rows(SemanticUnitResult result, string name) => result.Facts.GetProperty(name).EnumerateArray().ToArray();
    private static Dictionary<string, JsonElement> Index(SemanticUnitResult result, string name) => Rows(result, name).ToDictionary(row => Text(row, "key")!, StringComparer.Ordinal);
    private static string? Text(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
}
