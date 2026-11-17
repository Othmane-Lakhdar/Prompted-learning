using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using PromptedLearning.Core;

namespace PromptedLearning.Managed;

internal sealed class ManagedFactsBuilder(ManagedAnalysisRequest request)
{
    private sealed record Source(string Key, CapturedFile File, SyntaxTree Tree, DecodedSource Text);
    private readonly Dictionary<SyntaxTree, Source> sources = [];
    private readonly Dictionary<ISymbol, string> symbols = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, ISymbol> compilerSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> spans = new(StringComparer.Ordinal);
    private readonly Dictionary<ISymbol, HashSet<string>> modifiers = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IOperation, string> sourceOperations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<object>> rows = EmptyRows();
    private CSharpCompilation? compilation;
    public bool IsPartial { get; private set; }

    private static Dictionary<string, List<object>> EmptyRows() => new[] { "spans", "entities", "types", "signatures", "appearances", "bodies",
        "operations", "control_flow", "external_symbols", "unresolved_references", "coverage", "diagnostics", "discovery_requests",
        "entity_modifiers", "annotations", "preprocessing" }.ToDictionary(name => name, _ => new List<object>(), StringComparer.Ordinal);
    internal static JsonElement Element(object value)
    {
        using var document = CanonicalJson.Parse(CanonicalJson.Serialize(value));
        return document.RootElement.Clone();
    }
    private string Key(string family, string array) => family + ":managed/" + rows[array].Count.ToString(CultureInfo.InvariantCulture);
    public void AddSource(string key, CapturedFile file, SyntaxTree tree, DecodedSource text) => sources.Add(tree, new(key, file, tree, text));

    public void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden))
            rows["diagnostics"].Add(new { severity = diagnostic.Severity.ToString().ToLowerInvariant(), code = diagnostic.Id,
                message = diagnostic.GetMessage(CultureInfo.InvariantCulture),
                span_key = diagnostic.Location.IsInSource ? Span(diagnostic.Location.SourceTree!, diagnostic.Location.SourceSpan) : null,
                subject_key = diagnostic.Location.IsInSource ? sources[diagnostic.Location.SourceTree!].Key : null });
    }

    public void Extract(CSharpCompilation value, CancellationToken token)
    {
        compilation = value;
        foreach (var source in sources.Values)
        {
            var model = value.GetSemanticModel(source.Tree);
            var root = source.Tree.GetRoot(token);
            // Only declared internal source symbols are emitted; referenced framework inventories are never expanded.
            foreach (var syntax in root.DescendantNodes().Where(node => node is BaseNamespaceDeclarationSyntax or ClassDeclarationSyntax
                         or MethodDeclarationSyntax or ParameterSyntax or VariableDeclaratorSyntax))
            {
                token.ThrowIfCancellationRequested();
                var symbol = model.GetDeclaredSymbol(syntax, token) ?? throw new InvalidDataException("Pilot declaration has no compiler symbol.");
                var entity = Entity(symbol, syntax);
                rows["appearances"].Add(new { key = Key("appearance", "appearances"), entity_key = entity,
                    span_key = Span(syntax.SyntaxTree, syntax.Span), kind = "definition" });
                AddModifiers(symbol, entity, syntax);
            }
            if (root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective))
                throw new InvalidDataException("The frozen managed pilot unexpectedly contains preprocessing directives.");
            if (root.DescendantNodes().OfType<AttributeSyntax>().Any())
                throw new InvalidDataException("The frozen managed pilot unexpectedly contains annotations.");
        }
        foreach (var source in sources.Values)
        {
            var model = value.GetSemanticModel(source.Tree);
            foreach (var method in source.Tree.GetRoot(token).DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                token.ThrowIfCancellationRequested();
                var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method, token)!;
                var owner = symbols[symbol];
                Signature(symbol, owner);
                var executable = model.GetOperation(method, token) as IMethodBodyOperation;
                var operation = executable?.BlockBody ?? executable?.ExpressionBody;
                if (operation is null) throw new InvalidDataException("Managed pilot method has no semantic body.");
                var body = Key("body", "bodies");
                var bodySpan = Span(method.SyntaxTree, (method.Body as SyntaxNode ?? method.ExpressionBody!).Span);
                var operationKey = Operation(operation, body, [0], null, null, "root", token);
                rows["bodies"].Add(new { key = body, owner_entity_key = owner, span_key = bodySpan, representation = "semantic", kind = "function",
                    root_operation_key = operationKey, status = "complete", reason = (string?)null });
            }
        }
    }

    private string Entity(ISymbol symbol, SyntaxNode declaration)
    {
        if (symbols.TryGetValue(symbol, out var existing)) return existing;
        string? owner = null;
        if (symbol.ContainingSymbol is { } containing && containing is not IAssemblySymbol && containing is not IModuleSymbol &&
            containing is not INamespaceSymbol { IsGlobalNamespace: true })
        {
            var syntax = containing.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                ?? throw new InvalidDataException("Pilot symbol owner has no internal declaration.");
            owner = Entity(containing, syntax);
        }
        var identityKind = symbol is IParameterSymbol ? "parameter" : symbol is ILocalSymbol ? "source_local" : "compiler";
        var compilerKey = identityKind == "compiler" ? symbol.GetDocumentationCommentId()
            ?? throw new InvalidDataException("A named symbol has no compiler identity.") : null;
        if (compilerKey is not null)
        {
            if (compilerSymbols.TryGetValue(compilerKey, out var prior) && !SymbolEqualityComparer.Default.Equals(prior, symbol))
                throw new InvalidDataException("Compiler documentation identity collides across unequal symbols.");
            compilerSymbols[compilerKey] = symbol;
        }
        var key = Key("entity", "entities");
        symbols.Add(symbol, key);
        var type = symbol switch { ITypeSymbol t => t, ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => null };
        var kind = symbol switch { INamespaceSymbol => "namespace", INamedTypeSymbol => "class", IMethodSymbol => "method",
            IParameterSymbol => "parameter", ILocalSymbol => "local_variable", _ => throw new NotSupportedException("Unmapped pilot symbol kind.") };
        rows["entities"].Add(new { key, entity_kind = kind, written_name = symbol.Name,
            qualified_name = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), compiler_identity_kind = identityKind,
            compiler_key = compilerKey, owner_entity_key = owner, declared_ordinal = symbol is IParameterSymbol parameter ? (int?)parameter.Ordinal : null,
            origin_span_key = Span(declaration.SyntaxTree, declaration.Span), linkage = (string?)null,
            access = symbol is INamespaceSymbol or IParameterSymbol or ILocalSymbol ? null : Access(symbol.DeclaredAccessibility),
            compiler_signature_key = symbol is IMethodSymbol method ? PrimitiveSignature(method) : (JsonElement?)null,
            compiler_type_key = type is null ? (JsonElement?)null : PrimitiveType(type), origin = "written", resolution = "resolved" });
        if (type is not null) Type(type);
        return key;
    }

    private static string? Access(Accessibility access) => access switch
    {
        Accessibility.Public => "public", Accessibility.Private => "private", Accessibility.Internal => "internal",
        Accessibility.Protected => "protected", Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected", Accessibility.NotApplicable => null,
        _ => throw new NotSupportedException("Unmapped compiler accessibility.")
    };

    private void AddModifiers(ISymbol symbol, string entity, SyntaxNode syntax)
    {
        var tokens = syntax switch { TypeDeclarationSyntax type => type.Modifiers, MethodDeclarationSyntax method => method.Modifiers, _ => default };
        if (!modifiers.TryGetValue(symbol, out var known)) modifiers[symbol] = known = new(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (!known.Add(token.ValueText)) continue;
            rows["entity_modifiers"].Add(new { entity_key = entity, ordinal = known.Count - 1, modifier = token.ValueText,
                span_key = Span(syntax.SyntaxTree, token.Span) });
        }
    }

    private JsonElement PrimitiveType(ITypeSymbol type)
    {
        var name = type.SpecialType switch { SpecialType.System_Int32 => "System.Int32", SpecialType.System_Int64 => "System.Int64",
            SpecialType.System_Boolean => "System.Boolean", SpecialType.System_Void => "System.Void", _ => null };
        if (name is not null) return Element(new { tag = "builtin", name });
        if (type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation!.Assembly))
            return Element(new { tag = "named", compiler_key = type.GetDocumentationCommentId(), provider = new
            {
                kind = "internal_output", logical_name = compilation.AssemblyName,
                version = new { state = "known", value = compilation.Assembly.Identity.Version.ToString(), reason = (string?)null },
                content_fingerprint = new { state = "not_collected", value = (string?)null, reason = "output_not_emitted" },
                root_key = "output", relative_location = compilation.AssemblyName
            }, arguments = Array.Empty<object>() });
        throw new NotSupportedException("Type family is outside the frozen managed pilot.");
    }

    private string Type(ITypeSymbol type)
    {
        var primitive = PrimitiveType(type);
        var encoded = CanonicalJson.Sha256(CanonicalJson.Serialize(primitive));
        if (types.TryGetValue(encoded, out var found)) return found;
        var key = Key("type", "types"); types.Add(encoded, key);
        rows["types"].Add(new { key, compiler_type_key = primitive,
            named_entity_key = symbols.TryGetValue(type, out var entity) ? entity : null,
            element_type_key = (string?)null, argument_type_keys = Array.Empty<string>() });
        return key;
    }

    private JsonElement PrimitiveSignature(IMethodSymbol method) => Element(new { kind = "method", name = method.Name, generic_arity = method.Arity,
        parameters = method.Parameters.Select(parameter => new { type = PrimitiveType(parameter.Type), passing = "value" }).ToArray(),
        return_type = PrimitiveType(method.ReturnType), return_passing = "value", qualifiers = method.IsStatic ? new[] { "static" } : [],
        calling_convention = (string?)null, language_linkage = (string?)null, constraints = Array.Empty<string>() });

    private void Signature(IMethodSymbol method, string owner)
    {
        var key = Key("signature", "signatures");
        rows["signatures"].Add(new { key, owner_entity_key = owner, compiler_signature_key = PrimitiveSignature(method),
            return_type_key = Type(method.ReturnType), parameters = method.Parameters.Select(parameter => new
            {
                key = "parameter:managed/" + owner.Split('/')[^1] + "/" + parameter.Ordinal.ToString(CultureInfo.InvariantCulture),
                entity_key = symbols[parameter], ordinal = parameter.Ordinal, name = parameter.Name, type_key = Type(parameter.Type),
                passing_mode = "value", optional = parameter.IsOptional, variadic = parameter.IsParams
            }).ToArray() });
    }

    private string Span(SyntaxTree tree, TextSpan span, bool implicitCause = false)
    {
        var source = sources[tree];
        var start = source.Text.ByteOffset(span.Start); var end = source.Text.ByteOffset(span.End);
        var identity = source.Key + "/" + start.ToString(CultureInfo.InvariantCulture) + "/" + end.ToString(CultureInfo.InvariantCulture) + "/" + implicitCause;
        if (spans.TryGetValue(identity, out var found)) return found;
        var cause = implicitCause ? Span(tree, span) : null;
        var key = Key("span", "spans"); spans.Add(identity, key);
        rows["spans"].Add(new { key, file_key = source.Key, start_byte = start, end_byte = end,
            origin_kind = implicitCause ? "implicit_cause" : "written", origin_stack = implicitCause
                ? new object[] { new { file_key = source.Key, start_byte = start, end_byte = end, origin_kind = "written" } } : [],
            cause_span_key = cause });
        return key;
    }

    private string Operation(IOperation operation, string body, int[] path, string? parent, int? ordinal, string role, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = "operation:managed/" + body.Split('/')[^1] + "/" + string.Join('/', path);
        if (!sourceOperations.TryAdd(operation, key)) throw new InvalidDataException("A source operation appears twice in the semantic tree.");
        var children = operation.ChildOperations.ToArray();
        var childKeys = children.Select((child, index) => "operation:managed/" + body.Split('/')[^1] + "/" + string.Join('/', path.Append(index))).ToArray();
        var kind = operation switch
        {
            IBlockOperation => "block", IVariableDeclarationGroupOperation => "variable_declaration_group", IVariableDeclarationOperation => "variable_declaration",
            IVariableDeclaratorOperation => "variable_declarator", IExpressionStatementOperation => "expression_statement", IArgumentOperation => "argument",
            ILiteralOperation => "literal", ILocalReferenceOperation or IParameterReferenceOperation => "reference", ISimpleAssignmentOperation => "assignment",
            IBinaryOperation => "binary", IConversionOperation => "conversion", IInvocationOperation => "invocation", IReturnOperation => "return",
            IConditionalOperation => "if", _ => "unsupported"
        };
        if (kind == "unsupported") IsPartial = true;
        var referenced = operation switch { IInvocationOperation invocation => (ISymbol)invocation.TargetMethod,
            IParameterReferenceOperation parameter => parameter.Parameter, ILocalReferenceOperation local => local.Local,
            IVariableDeclaratorOperation variable => variable.Symbol, _ => null };
        var roleOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var operands = children.Select((child, index) =>
        {
            var operandRole = OperandRole(operation, index);
            var operandOrdinal = roleOrdinals.GetValueOrDefault(operandRole);
            roleOrdinals[operandRole] = operandOrdinal + 1;
            return new { role = operandRole, ordinal = operandOrdinal, target_key = childKeys[index],
                parameter_entity_key = child is IArgumentOperation { Parameter: { } parameter } ? symbols[parameter] :
                    operation is IArgumentOperation { Parameter: { } ownerParameter } ? symbols[ownerParameter] : null };
        }).ToArray();
        rows["operations"].Add(new { key, body_key = body, structural_path = path, parent_operation_key = parent, child_ordinal = ordinal,
            child_role = role, representation = "semantic", operation_kind = kind, compiler_kind = operation.Kind.ToString(),
            span_key = Span(operation.Syntax.SyntaxTree, operation.Syntax.Span, operation.IsImplicit),
            cause_span_key = operation.IsImplicit ? Span(operation.Syntax.SyntaxTree, operation.Syntax.Span) : null,
            @implicit = operation.IsImplicit, semantic_role = SemanticRole(operation), execution_point = (string?)null,
            type_key = operation.Type is null ? null : Type(operation.Type), referenced_entity_key = referenced is null ? null : symbols[referenced],
            operator_kind = operation switch { IBinaryOperation binary => binary.OperatorKind.ToString(), IConversionOperation conversion => conversion.Conversion.IsIdentity ? "identity" : "non_identity", _ => null },
            constant = operation.ConstantValue.HasValue ? Constant(operation.ConstantValue.Value) : (JsonElement?)null,
            operands, supported = kind != "unsupported", unsupported_reason = kind == "unsupported" ? "operation_kind_not_supported" : null });
        for (var index = 0; index < children.Length; index++)
            Operation(children[index], body, path.Append(index).ToArray(), key, index, ChildRole(operation, index), token);
        return key;
    }

    private static string ChildRole(IOperation parent, int ordinal) => parent switch
    {
        IConditionalOperation => ordinal switch { 0 => "condition", 1 => "true", 2 => "false", _ => "child" },
        ISimpleAssignmentOperation => ordinal == 0 ? "destination" : "value", IBinaryOperation => ordinal == 0 ? "left" : "right",
        IReturnOperation => "return", IInvocationOperation => "argument", IArgumentOperation => "value", IExpressionStatementOperation => "expression",
        IVariableDeclarationGroupOperation => "declaration", IVariableDeclarationOperation => "declarator", IBlockOperation => "statement", _ => "child"
    };
    private static string OperandRole(IOperation parent, int ordinal) => parent switch
    {
        IConditionalOperation when ordinal == 0 => "condition", ISimpleAssignmentOperation when ordinal == 0 => "destination",
        IReturnOperation => "return", IInvocationOperation or IArgumentOperation => "argument", IConversionOperation => "conversion", _ => "operand"
    };
    private static string SemanticRole(IOperation operation) => operation switch
    {
        ILocalReferenceOperation or IParameterReferenceOperation =>
            operation.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, operation) ? "destination" : "read",
        IInvocationOperation => "call",
        IVariableDeclaratorOperation => "declaration",
        ISimpleAssignmentOperation => "assignment",
        IReturnOperation => "return",
        IConditionalOperation => "condition",
        IBlockOperation => "block",
        IArgumentOperation => "argument",
        ILiteralOperation => "constant",
        _ => "evaluation"
    };
    private static JsonElement Constant(object? value) => value switch
    {
        int number => Element(new { kind = "signed_integer", value = number.ToString(CultureInfo.InvariantCulture) }),
        long number => Element(new { kind = "signed_integer", value = number.ToString(CultureInfo.InvariantCulture) }),
        bool boolean => Element(new { kind = "boolean", value = boolean ? "true" : "false" }),
        null => Element(new { kind = "null", value = "null" }),
        _ => throw new NotSupportedException("Literal is outside the frozen managed pilot.")
    };

    public JsonElement Finish(string status, string? reason)
    {
        foreach (var source in request.Sources)
            foreach (var feature in new[] { "parse", "entities", "types", "modifiers", "annotations", "preprocessing", "operations", "control_flow", "storage_events" })
                rows["coverage"].Add(new { file_key = source.FileKey, status = feature is "control_flow" or "storage_events" ? "not_attempted" : status,
                    reason = feature is "control_flow" or "storage_events" ? "not_requested" : reason, unparsed_span_keys = Array.Empty<string>(), feature });
        return Element(rows);
    }
    public JsonElement Failure(string reason)
    {
        foreach (var row in rows.Values) row.Clear();
        rows["diagnostics"].Add(new { severity = "error", code = "managed_input_failure", message = reason, span_key = (string?)null, subject_key = (string?)null });
        return Finish("not_attempted", reason);
    }
}
