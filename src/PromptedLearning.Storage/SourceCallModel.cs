namespace PromptedLearning.Storage;

internal sealed record SourceCallArgument(long Ordinal, string ExpressionId, string ParameterId, string PassingMode);
internal sealed record SourceCall(string OperationId, string BodyId, string CallerId, string TargetId, string? SpanId,
    string? CauseSpanId, IReadOnlyList<SourceCallArgument> Arguments);

internal static class SourceCallModel
{
    public static SourceCall? Read(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> rows,
        IReadOnlyDictionary<string, object?> operation, out string? unsupported)
    {
        unsupported = null;
        if (Number(operation, "supported") != 1) { unsupported = Text(operation, "unsupported_reason") ?? "unsupported_call_operation"; return null; }
        if (Text(operation, "referenced_entity_id") is not { } targetId) { unsupported = "selected_call_target_unavailable"; return null; }
        var target = One(rows["entity"].Where(row => Text(row, "id") == targetId), "Selected call target is not an entity.");
        var targetKind = Text(target, "entity_kind");
        Require(targetKind is "method" or "function", "Pilot selected call target is not a callable method/function.");
        if (Text(target, "language") == "csharp")
        {
            if (targetKind != "method" || !rows["entity_modifier"].Any(row => Text(row, "entity_id") == targetId && Text(row, "modifier") == "static"))
            { unsupported = "nonstatic_call_dispatch_not_supported"; return null; }
        }
        else
        {
            var owner = Text(target, "containing_entity_id");
            if (targetKind != "function" || (owner is not null && !rows["entity"].Any(row => Text(row, "id") == owner && Text(row, "entity_kind") == "namespace")))
            { unsupported = "nonfree_function_dispatch_not_supported"; return null; }
        }
        var body = One(rows["body"].Where(row => Equals(row["id"], operation["body_id"])), "Invocation has no unique source body.");
        var callerId = Text(body, "owner_entity_id")!;
        Require(rows["entity"].Any(row => Text(row, "id") == callerId && Text(row, "entity_kind") is "method" or "function"), "Pilot caller is not a method/function.");
        var detail = One(rows["entity_detail"].Where(row => Text(row, "entity_id") == targetId), "Selected callable detail is missing.");
        var signatureId = Text(detail, "signature_id") ?? throw new InvalidDataException("Selected callable signature is missing.");
        var parameters = rows["parameter"].Where(row => Text(row, "signature_id") == signatureId).OrderBy(row => Number(row, "ordinal")).ToArray();
        var operands = rows["operation_operand"].Where(row => Equals(row["operation_id"], operation["id"]) && Text(row, "role") == "argument").OrderBy(row => Number(row, "ordinal")).ToArray();
        Require(parameters.Length == operands.Length, "Direct call argument/parameter cardinality differs.");
        var arguments = new List<SourceCallArgument>();
        for (var index = 0; index < operands.Length; index++)
        {
            var operand = operands[index]; var parameter = parameters[index];
            Require(Number(operand, "ordinal") == index && Number(parameter, "ordinal") == index && parameter["entity_id"] is string
                && Equals(operand["parameter_id"], parameter["entity_id"]), "Direct argument does not map to its selected declared parameter.");
            var expression = One(rows["operation"].Where(row => Equals(row["id"], operand["target_id"])), "Argument expression is absent.");
            if (Text(expression, "operation_kind") == "argument")
            {
                var wrapper = One(rows["operation_operand"].Where(row => Equals(row["operation_id"], expression["id"])), "Argument wrapper must have one value operand.");
                Require(Text(wrapper, "role") == "argument" && Equals(wrapper["parameter_id"], parameter["entity_id"]), "Argument wrapper mapping differs.");
                expression = One(rows["operation"].Where(row => Equals(row["id"], wrapper["target_id"])), "Argument wrapper value is absent.");
            }
            Require(Equals(expression["body_id"], body["id"]), "Argument expression crosses source bodies.");
            var passing = Text(parameter, "passing_mode")!;
            if (passing != "value") { unsupported = "nonvalue_argument_passing_not_supported"; return null; }
            arguments.Add(new(index, Text(expression, "id")!, Text(parameter, "entity_id")!, passing));
        }
        return new(Text(operation, "id")!, Text(body, "id")!, callerId, targetId, Text(operation, "span_id"), Text(operation, "cause_span_id"), arguments);
    }

    internal static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value) ? value as string : null;
    internal static long Number(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value) && value is not null ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    internal static T One<T>(IEnumerable<T> source, string message) { var values = source.Take(2).ToArray(); Require(values.Length == 1, message); return values[0]; }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
