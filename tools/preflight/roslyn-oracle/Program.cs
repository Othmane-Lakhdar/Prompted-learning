using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
var input=args[0];
var paths=Directory.GetFiles(input,"*.cs").OrderBy(p=>p,StringComparer.Ordinal).ToArray();
var trees=paths.Select(p=>CSharpSyntaxTree.ParseText(File.ReadAllText(p),new CSharpParseOptions(LanguageVersion.CSharp12),p)).ToArray();
var refs=Directory.GetFiles(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.19\ref\net8.0","*.dll").Select(p=>MetadataReference.CreateFromFile(p));
var c=CSharpCompilation.Create("Pilot",trees,refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var errors=c.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).ToArray();
if(errors.Length>0)throw new Exception(string.Join("\n",errors.Select(e=>e.ToString())));
var method=trees.SelectMany(t=>t.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()).Single(m=>m.Identifier.Text=="Choose");
var op=c.GetSemanticModel(method.SyntaxTree).GetOperation(method.Body!)!;
var rows=new List<object>();
void Walk(IOperation node,string route){
 string? target=node switch {IInvocationOperation v=>v.TargetMethod.ToDisplayString(),IParameterReferenceOperation v=>v.Parameter.Name,ILocalReferenceOperation v=>v.Local.Name,IVariableDeclaratorOperation v=>v.Symbol.Name,_=>null};
 string? specific=node switch {IBinaryOperation v=>v.OperatorKind.ToString(),IConversionOperation v=>v.Conversion.ToString(),IArgumentOperation v=>v.ArgumentKind.ToString(),_=>null};
 rows.Add(new {path=route,kind=node.Kind.ToString(),implicit_node=node.IsImplicit,type=node.Type?.ToDisplayString(),target,specific,start_utf16=node.Syntax.Span.Start,end_utf16=node.Syntax.Span.End,constant=node.ConstantValue.HasValue?JsonSerializer.Serialize(node.ConstantValue.Value):null});
 var i=0;foreach(var child in node.ChildOperations)Walk(child,route+"/"+(i++));
}
Walk(op,"0");
var result=new {producer="bounded M0 compiler oracle; not extractor",roslyn=typeof(Compilation).Assembly.GetName().Version?.ToString(),language="CSharp12",target_framework="net8.0",reference_pack="8.0.19",compilation_errors=errors.Length,input_files=paths.Select(p=>new {name=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()}),node_count=rows.Count,nodes=rows};
File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine(JsonSerializer.Serialize(new {node_count=rows.Count,kinds=rows}));
