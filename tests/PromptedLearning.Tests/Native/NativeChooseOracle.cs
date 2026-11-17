using System.Text;
using System.Text.Json;

namespace PromptedLearning.Tests;

/// <summary>Expected source tree from the frozen M0 compiler AST oracle and pilot text.</summary>
internal static class NativeChooseOracle
{
    private sealed record Node(string Path, string CompilerKind, string Kind, string Role, string Text,
        string? Type, string? Reference = null, string? Operator = null, bool Implicit = false);

    public static void Assert(TestContext context, JsonElement result, JsonElement body, JsonElement[] operations)
    {
        var source = File.ReadAllBytes(Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native/Arithmetic.cpp"));
        var sourceText = Encoding.UTF8.GetString(source);
        var bodyText = sourceText[(sourceText.IndexOf("{", sourceText.IndexOf("int Choose", StringComparison.Ordinal), StringComparison.Ordinal))..
            (sourceText.IndexOf("\n}", sourceText.IndexOf("int Choose", StringComparison.Ordinal), StringComparison.Ordinal) + 2)];
        var branchText = sourceText[sourceText.IndexOf("if (value > 0)", StringComparison.Ordinal)..
            (sourceText.IndexOf("result = 0", StringComparison.Ordinal) + "result = 0".Length)];
        Node[] expected =
        [
            new("0", "CompoundStmt", "block", "root", bodyText, null),
            new("0/0", "DeclStmt", "variable_declaration_group", "statement", "int result;", null),
            new("0/0/0", "VarDecl", "variable_declarator", "declaration", "int result", "int", "result"),
            new("0/1", "IfStmt", "if", "statement", branchText, null),
            new("0/1/0", "BinaryOperator", "binary", "condition", "value > 0", "bool", Operator: ">"),
            new("0/1/0/0", "ImplicitCastExpr", "conversion", "left", "value", "int", Operator: "LValueToRValue", Implicit: true),
            new("0/1/0/0/0", "DeclRefExpr", "reference", "operand", "value", "int", "value"),
            new("0/1/0/1", "IntegerLiteral", "literal", "right", "0", "int"),
            new("0/1/1", "BinaryOperator", "assignment", "when_true", "result = Add(value, 1)", "int", Operator: "="),
            new("0/1/1/0", "DeclRefExpr", "reference", "destination", "result", "int", "result"),
            new("0/1/1/1", "CallExpr", "invocation", "right", "Add(value, 1)", "int", "Add"),
            new("0/1/1/1/0", "ImplicitCastExpr", "conversion", "callee", "Add", "pointer", Operator: "FunctionToPointerDecay", Implicit: true),
            new("0/1/1/1/0/0", "DeclRefExpr", "reference", "operand", "Add", "function", "Add"),
            new("0/1/1/1/1", "ImplicitCastExpr", "conversion", "argument", "value", "int", Operator: "LValueToRValue", Implicit: true),
            new("0/1/1/1/1/0", "DeclRefExpr", "reference", "operand", "value", "int", "value"),
            new("0/1/1/1/2", "IntegerLiteral", "literal", "argument", "1", "int"),
            new("0/1/2", "BinaryOperator", "assignment", "when_false", "result = 0", "int", Operator: "="),
            new("0/1/2/0", "DeclRefExpr", "reference", "destination", "result", "int", "result"),
            new("0/1/2/1", "IntegerLiteral", "literal", "right", "0", "int"),
            new("0/2", "ReturnStmt", "return", "statement", "return result", null),
            new("0/2/0", "ImplicitCastExpr", "conversion", "return", "result", "int", Operator: "LValueToRValue", Implicit: true),
            new("0/2/0/0", "DeclRefExpr", "reference", "operand", "result", "int", "result")
        ];
        Check.Equal(expected.Length, operations.Length);
        var actual = operations.ToDictionary(row => string.Join('/', row.GetProperty("structural_path").EnumerateArray().Select(v => v.GetInt32())), StringComparer.Ordinal);
        var spans = result.GetProperty("spans").EnumerateArray().ToDictionary(row => Text(row, "key")!, StringComparer.Ordinal);
        var entities = result.GetProperty("entities").EnumerateArray().ToDictionary(row => Text(row, "key")!, StringComparer.Ordinal);
        var types = result.GetProperty("types").EnumerateArray().ToDictionary(row => Text(row, "key")!, StringComparer.Ordinal);
        foreach (var node in expected)
        {
            Check.True(actual.TryGetValue(node.Path, out var row), "Missing exact AST path " + node.Path);
            Check.Equal(node.CompilerKind, Text(row, "compiler_kind"), node.Path + ": compiler kind");
            Check.Equal(node.Kind, Text(row, "operation_kind"), node.Path + ": normalized kind");
            Check.Equal(node.Role, Text(row, "child_role"), node.Path + ": child role");
            Check.Equal(node.Implicit, row.GetProperty("implicit").GetBoolean(), node.Path + ": implicit flag");
            Check.Equal(node.Operator, Text(row, "operator_kind"), node.Path + ": operator/conversion");
            var lastSlash = node.Path.LastIndexOf('/');
            Check.Equal(lastSlash < 0 ? null : Text(actual[node.Path[..lastSlash]], "key"), Text(row, "parent_operation_key"), node.Path + ": parent");
            if (lastSlash >= 0) Check.Equal(int.Parse(node.Path[(lastSlash + 1)..], System.Globalization.CultureInfo.InvariantCulture), row.GetProperty("child_ordinal").GetInt32());
            var origin = spans[Text(row, "span_key")!];
            Check.Equal("file:main", Text(origin, "file_key"));
            var start = origin.GetProperty("start_byte").GetInt32(); var end = origin.GetProperty("end_byte").GetInt32();
            Check.Equal(node.Text, Encoding.UTF8.GetString(source.AsSpan(start, end - start)), node.Path + ": exact original source interval");
            Check.Equal(node.Implicit ? "implicit_cause" : "written", Text(origin, "origin_kind"));
            if (node.Implicit)
            {
                var cause = spans[Text(origin, "cause_span_key")!]; var stack = origin.GetProperty("origin_stack");
                Check.Equal(1, stack.GetArrayLength());
                foreach (var field in new[] { "file_key", "start_byte", "end_byte", "origin_kind" }) Check.Equal(cause.GetProperty(field).GetRawText(), stack[0].GetProperty(field).GetRawText());
                Check.Equal(Text(origin, "cause_span_key"), Text(row, "cause_span_key"));
                Check.Equal(start, cause.GetProperty("start_byte").GetInt32()); Check.Equal(end, cause.GetProperty("end_byte").GetInt32());
            }
            if (node.Type is null) Check.True(row.GetProperty("type_key").ValueKind == JsonValueKind.Null);
            else
            {
                var primitive = types[Text(row, "type_key")!].GetProperty("compiler_type_key");
                Check.Equal(node.Type, Text(primitive, "tag") == "builtin" ? Text(primitive, "name") : Text(primitive, "tag"));
            }
            Check.Equal(node.Reference, Text(row, "referenced_entity_key") is { } entity ? Text(entities[entity], "written_name") : null, node.Path + ": reference identity");
            if (node.CompilerKind == "IntegerLiteral")
            {
                Check.Equal("signed_integer", Text(row.GetProperty("constant"), "kind"));
                Check.Equal(node.Text, Text(row.GetProperty("constant"), "value"));
            }
            else Check.True(row.GetProperty("constant").ValueKind == JsonValueKind.Null);

            var children = expected.Where(child => child.Path.LastIndexOf('/') >= 0 && child.Path[..child.Path.LastIndexOf('/')] == node.Path && child.CompilerKind != "VarDecl").ToArray();
            var operands = row.GetProperty("operands").EnumerateArray().ToArray();
            Check.Equal(children.Length, operands.Length, node.Path + ": operand count");
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i]; var role = child.Role is "destination" or "condition" or "return" or "argument" ? child.Role : "operand";
                var ordinal = ordinals.GetValueOrDefault(role); ordinals[role] = ordinal + 1;
                Check.Equal(role, Text(operands[i], "role")); Check.Equal(ordinal, operands[i].GetProperty("ordinal").GetInt32());
                Check.Equal(Text(actual[child.Path], "key"), Text(operands[i], "target_key"));
                if (node.Kind == "invocation" && role == "argument")
                {
                    var parameter = entities[Text(operands[i], "parameter_entity_key")!];
                    Check.Equal(Text(row, "referenced_entity_key"), Text(parameter, "owner_entity_key")); Check.Equal(ordinal, parameter.GetProperty("declared_ordinal").GetInt32());
                }
                else Check.True(operands[i].GetProperty("parameter_entity_key").ValueKind == JsonValueKind.Null);
            }
        }
        Check.Equal(Text(actual["0"], "key"), Text(body, "root_operation_key"));
    }

    private static string? Text(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
}
