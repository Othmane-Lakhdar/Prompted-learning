using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;
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
var originalOperations=new List<(IOperation Node,string Path)>();
void Walk(IOperation node,string route){
 originalOperations.Add((node,route));
 string? target=node switch {IInvocationOperation v=>v.TargetMethod.ToDisplayString(),IParameterReferenceOperation v=>v.Parameter.Name,ILocalReferenceOperation v=>v.Local.Name,IVariableDeclaratorOperation v=>v.Symbol.Name,_=>null};
 string? specific=node switch {IBinaryOperation v=>v.OperatorKind.ToString(),IConversionOperation v=>v.Conversion.ToString(),IArgumentOperation v=>v.ArgumentKind.ToString(),_=>null};
 rows.Add(new {path=route,kind=node.Kind.ToString(),implicit_node=node.IsImplicit,type=node.Type?.ToDisplayString(),target,specific,start_utf16=node.Syntax.Span.Start,end_utf16=node.Syntax.Span.End,constant=node.ConstantValue.HasValue?JsonSerializer.Serialize(node.ConstantValue.Value):null});
 var i=0;foreach(var child in node.ChildOperations)Walk(child,route+"/"+(i++));
}
Walk(op,"0");
var result=new {producer="bounded M0 compiler oracle; not extractor",roslyn=typeof(Compilation).Assembly.GetName().Version?.ToString(),language="CSharp12",target_framework="net8.0",reference_pack="8.0.19",compilation_errors=errors.Length,input_files=paths.Select(p=>new {name=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()}),node_count=rows.Count,nodes=rows};
File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine(JsonSerializer.Serialize(new {node_count=rows.Count,kinds=rows}));
if(args.Length>2 && args[2]=="--cfg") {
 // A capability oracle only: it observes compiler correspondence and does not
 // persist canonical records, implement a CFG adapter, or derive data flow.
 var executable=c.GetSemanticModel(method.SyntaxTree).GetOperation(method) as IMethodBodyOperation
     ?? throw new Exception("The compiler did not expose an executable method root.");
 var cfg=ControlFlowGraph.Create(executable);
 var observations=new List<object>();
 void Observe(IOperation node,int block,string role) {
  var candidates=originalOperations.Where(x=>x.Node.Kind==node.Kind && x.Node.Syntax==node.Syntax).ToArray();
  observations.Add(new {block,role,kind=node.Kind.ToString(),start_utf16=node.Syntax.Span.Start,end_utf16=node.Syntax.Span.End,
   source_paths=candidates.Select(x=>x.Path).ToArray(),same_operation_instance=candidates.Any(x=>ReferenceEquals(x.Node,node))});
  foreach(var child in node.ChildOperations)Observe(child,block,role+"/child");
 }
 foreach(var block in cfg.Blocks) {
  foreach(var operation in block.Operations)Observe(operation,block.Ordinal,"operation");
  if(block.BranchValue is not null)Observe(block.BranchValue,block.Ordinal,"branch_value");
 }
 var cfgResult=new {producer="bounded M0 Roslyn CFG correspondence oracle; not extractor",
  executable_root=executable.Kind.ToString(),root_has_parent=executable.Parent is not null,
  blocks=cfg.Blocks.Select(b=>new {ordinal=b.Ordinal,kind=b.Kind.ToString(),condition_kind=b.ConditionKind.ToString(),
   operation_count=b.Operations.Length,branch_value_kind=b.BranchValue?.Kind.ToString(),
   conditional_destination=b.ConditionalSuccessor?.Destination?.Ordinal,conditional_semantics=b.ConditionalSuccessor?.Semantics.ToString(),
   fallthrough_destination=b.FallThroughSuccessor?.Destination?.Ordinal,fallthrough_semantics=b.FallThroughSuccessor?.Semantics.ToString()}),observations};
 File.WriteAllText(args[1]+".cfg.json",JsonSerializer.Serialize(cfgResult,new JsonSerializerOptions{WriteIndented=true})+"\n");
 Console.WriteLine(JsonSerializer.Serialize(new {cfg_blocks=cfg.Blocks.Length,observed_cfg_operations=observations.Count}));
}
