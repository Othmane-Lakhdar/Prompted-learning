#include "Protocol.h"
#include <clang/AST/ASTConsumer.h>
#include <clang/AST/Attr.h>
#include <clang/AST/DeclCXX.h>
#include <clang/AST/Expr.h>
#include <clang/AST/Stmt.h>
#include <clang/Frontend/CompilerInstance.h>
#include <clang/Frontend/FrontendAction.h>
#include <clang/Index/USRGeneration.h>
#include <clang/Lex/Lexer.h>
#include <clang/Lex/PPCallbacks.h>
#include <clang/Lex/Preprocessor.h>
#include <clang/Tooling/Tooling.h>
#include <llvm/Support/VirtualFileSystem.h>
#include <map>
#include <set>
#include <stdexcept>

namespace pl {
namespace {
using namespace clang;
Value nullable(const std::string& value) { return value.empty() ? Value(nullptr) : Value(value); }
std::string indexKey(const char* family, size_t index) { return std::string(family) + ":" + std::to_string(index); }
std::string virtualName(llvm::StringRef path) {
    std::string result = path.str();
    std::replace(result.begin(),result.end(),'\\','/');
    return result;
}

class Extractor {
public:
    const Request& request;
    Object result;
    ASTContext* context = nullptr;
    SourceManager* sources = nullptr;
    const LangOptions* language = nullptr;
    std::set<std::string> consumed;
    bool unsupported = false;
    bool compilerError = false;
    std::map<const Decl*, std::string> entityKeys;
    // This exact map is reserved for the later CFG stage. No pointer is serialized.
    std::map<const void*, std::string> operationKeys;
    std::map<std::string, std::string> spanKeys, typeKeys;
    std::map<std::pair<const FunctionDecl*, unsigned>, std::string> parameterKeys;
    std::map<const FunctionDecl*, std::string> signatureKeys;

    explicit Extractor(const Request& input) : request(input), result(emptyResult(input)) {}
    Array& rows(const char* name) { return *result.getArray(name); }
    void budget() {
        int64_t count = 0;
        for (const auto& member : result) if (auto array = member.second.getAsArray()) count += static_cast<int64_t>(array->size());
        if (count > request.maxRecords || static_cast<int64_t>(rows("operations").size()) > request.maxOperations) throw std::runtime_error("record_or_operation_budget_exceeded");
    }
    const Input* inputAt(SourceLocation location) const {
        if (!sources || location.isInvalid()) return nullptr;
        auto filename = virtualName(sources->getFilename(sources->getSpellingLoc(location)));
        for (const auto& input : request.files) if (filename == input.virtualPath) return &input;
        return nullptr;
    }
    std::string spanBytes(const Input& input, unsigned begin, unsigned end, const std::string& origin = "written", const std::string& cause = "") {
        if (end < begin || end > input.bytes.size()) throw std::runtime_error("invalid_compiler_byte_span");
        auto identity = input.key + "/" + std::to_string(begin) + "/" + std::to_string(end) + "/" + origin;
        if (auto found = spanKeys.find(identity); found != spanKeys.end()) return found->second;
        auto key = indexKey("span", rows("spans").size()); spanKeys.emplace(identity,key);
        Array stack;
        if (!cause.empty()) {
            const Object* causeRow = nullptr;
            for (const auto& row : rows("spans")) if (text(*row.getAsObject(),"key") == cause) causeRow = row.getAsObject();
            if (!causeRow) throw std::runtime_error("origin_cause_unavailable");
            Object frame;
            for (auto name : {"file_key","start_byte","end_byte","origin_kind"}) frame[name] = *causeRow->get(name);
            stack.push_back(std::move(frame));
            for (const auto& frameValue : *causeRow->getArray("origin_stack")) stack.push_back(frameValue);
        }
        rows("spans").push_back(Object{{"key",key},{"file_key",input.key},{"start_byte",begin},{"end_byte",end},
            {"origin_kind",origin},{"origin_stack",std::move(stack)},{"cause_span_key",nullable(cause)}});
        return key;
    }
    std::string span(SourceRange range, bool implicit = false) {
        if (range.isInvalid()) return "";
        if (range.getBegin().isMacroID() || range.getEnd().isMacroID()) { unsupported = true; return ""; }
        auto input = inputAt(range.getBegin()); if (!input || inputAt(range.getEnd()) != input) return "";
        auto end = Lexer::getLocForEndOfToken(range.getEnd(),0,*sources,*language);
        if (end.isInvalid()) return "";
        unsigned beginByte = sources->getFileOffset(range.getBegin()), endByte = sources->getFileOffset(end);
        auto written = spanBytes(*input,beginByte,endByte);
        return implicit ? spanBytes(*input,beginByte,endByte,"implicit_cause",written) : written;
    }
    std::string lineSpan(SourceLocation location) {
        auto input = inputAt(location); if (!input) return "";
        auto begin = sources->getFileOffset(location); auto end = input->bytes.find('\n',begin);
        if (end == std::string::npos) end = input->bytes.size();
        if (end > begin && input->bytes[end-1] == '\r') --end;
        return spanBytes(*input,begin,static_cast<unsigned>(end));
    }
    void incomplete(const std::string& code, const std::string& message, const std::string& origin = "") {
        unsupported = true;
        rows("diagnostics").push_back(Object{{"severity","warning"},{"code",code},{"message",message},{"span_key",nullable(origin)},{"subject_key",nullptr}});
    }
    Object primitiveSignature(const FunctionProtoType* prototype, const std::string& name) {
        Array parameters;
        for (auto type : prototype->param_types()) parameters.push_back(Object{{"type",primitiveType(type)},{"passing",passing(type)}});
        return Object{{"kind","function"},{"name",name},{"generic_arity",0},{"parameters",std::move(parameters)},
            {"return_type",primitiveType(prototype->getReturnType())},{"return_passing",passing(prototype->getReturnType())},
            {"qualifiers",Array{}},{"calling_convention","cdecl"},{"language_linkage","C++"},{"constraints",Array{}}};
    }
    static std::string passing(QualType type) {
        if (type->isLValueReferenceType()) return "lvalue_reference";
        if (type->isRValueReferenceType()) return "rvalue_reference";
        return "value";
    }
    Value primitiveType(QualType type) {
        type = context->getCanonicalType(type);
        if (type.hasLocalQualifiers()) {
            Array qualifiers; if (type.isConstQualified()) qualifiers.push_back("const"); if (type.isVolatileQualified()) qualifiers.push_back("volatile");
            return Object{{"tag","qualified"},{"element",primitiveType(type.getLocalUnqualifiedType())},{"qualifiers",std::move(qualifiers)},{"nullable","not_applicable"}};
        }
        if (type->isBuiltinType()) return Object{{"tag","builtin"},{"name",type.getAsString(context->getPrintingPolicy())}};
        if (type->isPointerType()) return Object{{"tag","pointer"},{"element",primitiveType(type->getPointeeType())}};
        if (type->isReferenceType()) return Object{{"tag",type->isLValueReferenceType() ? "lvalue_reference" : "rvalue_reference"},{"element",primitiveType(type->getPointeeType())}};
        if (auto function = type->getAs<FunctionProtoType>()) return Object{{"tag","function"},{"signature",primitiveSignature(function,"")}};
        incomplete("unsupported_type","Type outside the M1 native pilot.");
        return Object{{"tag","unresolved"},{"spelling",type.getAsString()},{"reason","type_outside_native_pilot"}};
    }
    std::string typeKey(QualType type) {
        auto primitive = primitiveType(type); auto identity = canonical(primitive);
        if (auto found = typeKeys.find(identity); found != typeKeys.end()) return found->second;
        auto key = indexKey("type",rows("types").size()); typeKeys.emplace(identity,key);
        // Reserve before recursion so indices cannot collide.
        rows("types").push_back(Object{{"key",key},{"compiler_type_key",primitive},{"named_entity_key",nullptr},{"element_type_key",nullptr},{"argument_type_keys",Array{}}});
        std::string element;
        if (type->isPointerType() || type->isReferenceType()) element = typeKey(type->getPointeeType());
        for (auto& row : rows("types")) if (text(*row.getAsObject(),"key") == key) (*row.getAsObject())["element_type_key"] = nullable(element);
        return key;
    }
    std::string usr(const Decl* decl) {
        llvm::SmallString<128> buffer;
        if (index::generateUSRForDecl(decl,buffer)) throw std::runtime_error("compiler_usr_unavailable");
        return buffer.str().str();
    }
    std::string entity(const NamedDecl* decl) {
        if (auto parameter = llvm::dyn_cast<ParmVarDecl>(decl)) {
            auto owner = llvm::dyn_cast<FunctionDecl>(parameter->getDeclContext());
            if (!owner) throw std::runtime_error("parameter_owner_unavailable");
            return parameterKeys.at({owner->getCanonicalDecl(),parameter->getFunctionScopeIndex()});
        }
        auto canonicalDecl = decl->getCanonicalDecl();
        if (auto found = entityKeys.find(canonicalDecl); found != entityKeys.end()) return found->second;
        std::string owner;
        if (auto parent = llvm::dyn_cast<NamedDecl>(decl->getDeclContext())) owner = entity(parent);
        std::string kind, identityKind = "compiler", compilerKey;
        Value signature = nullptr, type = nullptr;
        if (llvm::isa<NamespaceDecl>(decl)) kind = "namespace";
        else if (auto function = llvm::dyn_cast<FunctionDecl>(decl)) {
            kind = "function"; signature = primitiveSignature(function->getType()->castAs<FunctionProtoType>(),function->getNameAsString()); type = primitiveType(function->getType());
        } else if (auto variable = llvm::dyn_cast<VarDecl>(decl)) { kind = "local_variable"; identityKind = "source_local"; type = primitiveType(variable->getType()); }
        else throw std::runtime_error("entity_outside_native_pilot");
        if (identityKind == "compiler") compilerKey = usr(canonicalDecl);
        auto key = indexKey("entity",rows("entities").size()); entityKeys.emplace(canonicalDecl,key);
        std::string linkage = "no_linkage";
        if (decl->hasExternalFormalLinkage()) linkage = "external";
        else if (decl->getFormalLinkage() == Linkage::Internal) linkage = "internal";
        rows("entities").push_back(Object{{"key",key},{"entity_kind",kind},{"written_name",decl->getNameAsString()},{"qualified_name",decl->getQualifiedNameAsString()},
            {"compiler_identity_kind",identityKind},{"compiler_key",nullable(compilerKey)},{"owner_entity_key",nullable(owner)},{"declared_ordinal",nullptr},
            {"origin_span_key",nullable(span(decl->getSourceRange()))},{"linkage",linkage},{"access",nullptr},
            {"compiler_signature_key",std::move(signature)},{"compiler_type_key",std::move(type)},{"origin","written"},{"resolution","resolved"}});
        return key;
    }
    void appearance(const std::string& owner, SourceRange range, const std::string& kind) {
        auto origin = span(range); if (origin.empty()) { incomplete("unmapped_appearance","Source appearance cannot be mapped to captured bytes."); return; }
        rows("appearances").push_back(Object{{"key",indexKey("appearance",rows("appearances").size())},{"entity_key",owner},{"span_key",origin},{"kind",kind}});
    }
    void function(FunctionDecl* function) {
        auto owner = entity(function); auto canonicalDecl = function->getCanonicalDecl();
        appearance(owner,function->getSourceRange(),function->isThisDeclarationADefinition() ? "definition" : "declaration");
        bool newSignature = signatureKeys.count(canonicalDecl) == 0;
        Array parameters;
        for (unsigned i = 0; i < function->getNumParams(); ++i) {
            auto parameter = function->getParamDecl(i); std::string key;
            auto identity = std::make_pair(canonicalDecl,i);
            if (auto found = parameterKeys.find(identity); found != parameterKeys.end()) key = found->second;
            else {
                key = indexKey("entity",rows("entities").size()); parameterKeys.emplace(identity,key);
                rows("entities").push_back(Object{{"key",key},{"entity_kind","parameter"},{"written_name",parameter->getNameAsString()},{"qualified_name",nullptr},
                    {"compiler_identity_kind","parameter"},{"compiler_key",nullptr},{"owner_entity_key",owner},{"declared_ordinal",i},
                    {"origin_span_key",nullable(span(parameter->getSourceRange()))},{"linkage","no_linkage"},{"access",nullptr},
                    {"compiler_signature_key",nullptr},{"compiler_type_key",primitiveType(parameter->getType())},{"origin","written"},{"resolution","resolved"}});
            }
            entityKeys[parameter] = key; appearance(key,parameter->getSourceRange(),"declaration");
            parameters.push_back(Object{{"key","parameter:" + owner.substr(7) + "/" + std::to_string(i)},{"entity_key",key},{"ordinal",i},{"name",parameter->getNameAsString()},
                {"type_key",typeKey(parameter->getType())},{"passing_mode",passing(parameter->getType())},{"optional",parameter->hasDefaultArg()},{"variadic",false}});
        }
        if (newSignature) {
            auto key = indexKey("signature",rows("signatures").size()); signatureKeys.emplace(canonicalDecl,key);
            rows("signatures").push_back(Object{{"key",key},{"owner_entity_key",owner},{"compiler_signature_key",primitiveSignature(function->getType()->castAs<FunctionProtoType>(),function->getNameAsString())},
                {"return_type_key",typeKey(function->getReturnType())},{"parameters",std::move(parameters)}});
        }
        unsigned modifierOrdinal = 0;
        for (auto attr : function->attrs()) {
            if (attr->isInherited()) continue;
            if (llvm::isa<NoInlineAttr>(attr)) {
                auto origin = span(attr->getRange());
                rows("annotations").push_back(Object{{"key",indexKey("annotation",rows("annotations").size())},{"owner_entity_key",owner},{"span_key",origin},
                    {"attribute_type_key",nullptr},{"written_name","noinline"},{"namespace",nullptr},{"syntax","declspec"},{"resolution","resolved"},{"arguments",Array{}}});
                bool exists = false; for (const auto& row : rows("entity_modifiers")) exists |= text(*row.getAsObject(),"entity_key") == owner && text(*row.getAsObject(),"modifier") == "noinline";
                if (!exists) rows("entity_modifiers").push_back(Object{{"entity_key",owner},{"ordinal",modifierOrdinal++},{"modifier","noinline"},{"span_key",origin}});
            } else incomplete("unsupported_attribute","Attribute outside the native pilot.",span(attr->getRange()));
        }
        if (function->isThisDeclarationADefinition()) {
            auto body = function->getBody(); auto key = indexKey("body",rows("bodies").size());
            auto root = operation(body,nullptr,key,Array{0},"",0,"root");
            rows("bodies").push_back(Object{{"key",key},{"owner_entity_key",owner},{"span_key",nullable(span(body->getSourceRange()))},{"representation","semantic"},
                {"kind","function"},{"root_operation_key",root},{"status",unsupported ? "partial" : "complete"},{"reason",unsupported ? Value("unsupported_source_construct") : Value(nullptr)}});
        }
    }
    void declarations(DeclContext* parent) {
        for (auto decl : parent->decls()) {
            if (!inputAt(decl->getLocation()) || decl->isImplicit()) continue;
            if (auto ns = llvm::dyn_cast<NamespaceDecl>(decl)) { auto key = entity(ns); appearance(key,ns->getSourceRange(),"declaration"); declarations(ns); }
            else if (auto fn = llvm::dyn_cast<FunctionDecl>(decl)) function(fn);
            else incomplete("unsupported_declaration","Declaration outside the native pilot.",span(decl->getSourceRange()));
            budget();
        }
    }
    std::string operation(const Stmt* statement, const VarDecl* variable, const std::string& body, Array path,
        const std::string& parent, unsigned ordinal, const std::string& role) {
        const void* pointer = statement ? static_cast<const void*>(statement) : static_cast<const void*>(variable);
        auto key = "operation:" + body.substr(5);
        for (const auto& part : path) key += "/" + std::to_string(*part.getAsInteger());
        if (!operationKeys.emplace(pointer,key).second) throw std::runtime_error("duplicate_ast_operation_pointer");
        auto range = statement ? statement->getSourceRange() : variable->getSourceRange();
        const bool implicit = statement && llvm::isa<ImplicitCastExpr>(statement);
        const std::string compilerKind = statement ? statement->getStmtClassName() : "VarDecl";
        std::string kind, referenced, operatorKind, type;
        Value constant = nullptr;
        std::vector<std::pair<const Stmt*,std::string>> children;
        std::vector<const VarDecl*> declarations;
        if (variable) {
            kind = "variable_declarator"; referenced = entity(variable); type = typeKey(variable->getType()); appearance(referenced,range,"definition");
            if (variable->hasInit()) children.emplace_back(variable->getInit(),"initializer");
        } else if (auto block = llvm::dyn_cast<CompoundStmt>(statement)) { kind = "block"; for (auto child : block->body()) children.emplace_back(child,"statement"); }
        else if (auto declaration = llvm::dyn_cast<DeclStmt>(statement)) {
            kind = "variable_declaration_group";
            for (auto decl : declaration->decls()) { if (auto child = llvm::dyn_cast<VarDecl>(decl)) declarations.push_back(child); else incomplete("unsupported_local_declaration","Non-variable declaration in a body.",span(range)); }
        } else if (auto branch = llvm::dyn_cast<IfStmt>(statement)) {
            kind = "if"; children.emplace_back(branch->getCond(),"condition"); children.emplace_back(branch->getThen(),"when_true"); if (branch->getElse()) children.emplace_back(branch->getElse(),"when_false");
            if (branch->getInit() || branch->getConditionVariable()) incomplete("unsupported_if_initializer","Conditional declaration outside pilot.",span(range));
        } else if (auto binary = llvm::dyn_cast<BinaryOperator>(statement)) {
            kind = binary->isAssignmentOp() ? "assignment" : "binary"; operatorKind = binary->getOpcodeStr().str();
            children.emplace_back(binary->getLHS(),binary->isAssignmentOp() ? "destination" : "left"); children.emplace_back(binary->getRHS(),"right");
        } else if (auto cast = llvm::dyn_cast<ImplicitCastExpr>(statement)) { kind = "conversion"; operatorKind = cast->getCastKindName(); children.emplace_back(cast->getSubExpr(),"operand"); }
        else if (auto call = llvm::dyn_cast<CallExpr>(statement)) {
            kind = "invocation"; if (auto callee = call->getDirectCallee()) referenced = entity(callee); else incomplete("unsupported_indirect_call","Indirect call outside pilot.",span(range));
            children.emplace_back(call->getCallee(),"callee"); for (auto argument : call->arguments()) children.emplace_back(argument,"argument");
        } else if (auto ref = llvm::dyn_cast<DeclRefExpr>(statement)) { kind = "reference"; referenced = entity(ref->getDecl()); appearance(referenced,range,"reference"); }
        else if (auto literal = llvm::dyn_cast<IntegerLiteral>(statement)) { kind = "literal"; llvm::SmallString<32> value; literal->getValue().toString(value,10,literal->getType()->isSignedIntegerType()); constant = Object{{"kind",literal->getType()->isSignedIntegerType() ? "signed_integer" : "unsigned_integer"},{"value",value.str().str()}}; }
        else if (auto returned = llvm::dyn_cast<ReturnStmt>(statement)) { kind = "return"; if (returned->getRetValue()) children.emplace_back(returned->getRetValue(),"return"); }
        else { kind = "unsupported"; incomplete("unsupported_operation","Compiler operation outside pilot: " + compilerKind,span(range)); for (auto child : statement->children()) if (child) children.emplace_back(child,"operand"); }
        if (auto expression = llvm::dyn_cast_or_null<Expr>(statement)) type = typeKey(expression->getType());
        auto origin = span(range,implicit), cause = implicit ? span(range) : "";
        auto semanticRole = kind == "invocation" ? "call" : kind == "reference" ? role == "destination" ? "destination" : "read" : kind;
        Object row{{"key",key},{"body_key",body},{"structural_path",Array(path)},{"parent_operation_key",nullable(parent)},
            {"child_ordinal",parent.empty() ? Value(nullptr) : Value(ordinal)},{"child_role",role},{"representation","semantic"},
            {"operation_kind",kind},{"compiler_kind",compilerKind},{"span_key",nullable(origin)},{"cause_span_key",nullable(cause)},
            {"implicit",implicit},{"semantic_role",semanticRole},{"execution_point",nullptr},{"type_key",nullable(type)},{"referenced_entity_key",nullable(referenced)},
            {"operator_kind",nullable(operatorKind)},{"constant",std::move(constant)},{"operands",Array{}},{"supported",kind != "unsupported"},
            {"unsupported_reason",kind == "unsupported" ? Value("compiler_operation_outside_pilot") : Value(nullptr)}};
        auto position = rows("operations").size(); rows("operations").push_back(std::move(row)); budget();
        unsigned childOrdinal = 0; Array operands; std::map<std::string,unsigned> operandOrdinals;
        for (const auto* decl : declarations) { auto childPath = path; childPath.push_back(childOrdinal); operation(nullptr,decl,body,std::move(childPath),key,childOrdinal++,"declaration"); }
        for (const auto& child : children) {
            auto childPath = path; childPath.push_back(childOrdinal); auto childKey = operation(child.first,nullptr,body,std::move(childPath),key,childOrdinal++,child.second);
            std::string operandRole = "operand", parameter;
            if (child.second == "destination" || child.second == "condition" || child.second == "return" || child.second == "argument") operandRole = child.second;
            if (child.second == "argument") if (auto call = llvm::dyn_cast<CallExpr>(statement)) if (auto callee = call->getDirectCallee()) {
                const auto argumentOrdinal = operandOrdinals[operandRole];
                if (argumentOrdinal < callee->getNumParams()) parameter = entity(callee->getParamDecl(argumentOrdinal));
            }
            operands.push_back(Object{{"role",operandRole},{"ordinal",operandOrdinals[operandRole]++},{"target_key",childKey},{"parameter_entity_key",nullable(parameter)}});
        }
        (*rows("operations")[position].getAsObject())["operands"] = std::move(operands);
        return key;
    }
};

class Preprocessing final : public PPCallbacks {
    Extractor& out;
public:
    explicit Preprocessing(Extractor& output) : out(output) {}
    void FileChanged(SourceLocation loc, FileChangeReason reason, SrcMgr::CharacteristicKind, FileID) override {
        if (reason == EnterFile) if (auto input = out.inputAt(loc)) out.consumed.insert(input->key);
    }
    void InclusionDirective(SourceLocation location, const Token&, llvm::StringRef filename, bool, CharSourceRange,
        OptionalFileEntryRef file, llvm::StringRef, llvm::StringRef, const Module*, bool, SrcMgr::CharacteristicKind) override {
        auto input = out.inputAt(location); if (!input) return;
        const Input* resolved = nullptr;
        if (file) for (const auto& candidate : out.request.files) if (virtualName(file->getName()) == candidate.virtualPath) resolved = &candidate;
        out.rows("preprocessing").push_back(Object{{"key",indexKey("preprocessing",out.rows("preprocessing").size())},{"file_key",input->key},{"event_kind","include"},
            {"span_key",out.lineSpan(location)},{"written_name",filename.str()},{"expression_text",nullptr},{"selected",true},
            {"resolved_file_key",resolved ? Value(resolved->key) : Value(nullptr)},{"resolution",resolved ? "resolved" : "unresolved"},{"reason",resolved ? Value(nullptr) : Value("include_not_captured")}});
        if (!resolved) out.rows("discovery_requests").push_back(Object{{"including_file_key",input->key},{"written_include",filename.str()},
            {"logical_candidate_path",input->logicalPath.substr(0,input->logicalPath.find_last_of('/') + 1) + filename.str()},{"reason","include_not_captured"}});
    }
    void PragmaDirective(SourceLocation location, PragmaIntroducerKind) override {
        auto input = out.inputAt(location); if (!input) return;
        const auto start = out.sources->getFileOffset(location); auto end = input->bytes.find('\n',start);
        auto line = llvm::StringRef(input->bytes).slice(start,end == std::string::npos ? input->bytes.size() : end).trim();
        if (line != "#pragma once" && line != "pragma once") out.incomplete("unsupported_pragma","Pragma outside the native pilot.",out.lineSpan(location));
        out.rows("preprocessing").push_back(Object{{"key",indexKey("preprocessing",out.rows("preprocessing").size())},{"file_key",input->key},{"event_kind","pragma"},
            {"span_key",out.lineSpan(location)},{"written_name","once"},{"expression_text",line.str()},{"selected",true},{"resolved_file_key",nullptr},{"resolution","not_applicable"},{"reason",nullptr}});
    }
    void MacroDefined(const Token& token, const MacroDirective*) override { if (out.inputAt(token.getLocation())) out.incomplete("unsupported_macro","Written macro definition outside native pilot.",out.span(SourceRange(token.getLocation()))); }
    void If(SourceLocation loc, SourceRange, ConditionValueKind) override { if (out.inputAt(loc)) out.incomplete("unsupported_conditional","Conditional preprocessing outside native pilot.",out.lineSpan(loc)); }
    void Ifdef(SourceLocation loc, const Token&, const MacroDefinition&) override { if (out.inputAt(loc)) out.incomplete("unsupported_conditional","Conditional preprocessing outside native pilot.",out.lineSpan(loc)); }
    void Ifndef(SourceLocation loc, const Token&, const MacroDefinition&) override { if (out.inputAt(loc)) out.incomplete("unsupported_conditional","Conditional preprocessing outside native pilot.",out.lineSpan(loc)); }
};
class Consumer final : public ASTConsumer {
    Extractor& out;
public:
    explicit Consumer(Extractor& output) : out(output) {}
    void HandleTranslationUnit(ASTContext& context) override {
        out.context = &context;
        if (text(out.request.document,"phase") == "final" && !context.getDiagnostics().hasErrorOccurred()) out.declarations(context.getTranslationUnitDecl());
    }
};
class Action final : public ASTFrontendAction {
    Extractor& out;
public:
    explicit Action(Extractor& output) : out(output) {}
    std::unique_ptr<ASTConsumer> CreateASTConsumer(CompilerInstance& compiler, llvm::StringRef) override {
        out.sources = &compiler.getSourceManager(); out.language = &compiler.getLangOpts();
        compiler.getPreprocessor().addPPCallbacks(std::make_unique<Preprocessing>(out));
        return std::make_unique<Consumer>(out);
    }
};
class Diagnostics final : public DiagnosticConsumer {
    Extractor& out;
public:
    explicit Diagnostics(Extractor& output) : out(output) {}
    void HandleDiagnostic(DiagnosticsEngine::Level level, const Diagnostic& diagnostic) override {
        DiagnosticConsumer::HandleDiagnostic(level,diagnostic);
        if (level < DiagnosticsEngine::Note) return;
        llvm::SmallString<256> message; diagnostic.FormatDiagnostic(message);
        auto severity = level >= DiagnosticsEngine::Error ? "error" : level == DiagnosticsEngine::Warning ? "warning" : "info";
        if (level >= DiagnosticsEngine::Error) out.compilerError = true;
        std::string origin;
        if (diagnostic.hasSourceManager() && out.sources && out.language) origin = out.span(SourceRange(diagnostic.getLocation()));
        out.rows("diagnostics").push_back(Object{{"severity",severity},{"code","clang:" + std::to_string(diagnostic.getID())},{"message",message.str().str()},{"span_key",nullable(origin)},{"subject_key",nullptr}});
    }
};
}

Object extract(const Request& request) {
    Extractor out(request);
    auto filesystem = llvm::makeIntrusiveRefCnt<llvm::vfs::InMemoryFileSystem>();
    for (const auto& input : request.files) if (!filesystem->addFile(input.virtualPath,0,llvm::MemoryBuffer::getMemBufferCopy(input.bytes,input.virtualPath))) throw std::runtime_error("duplicate_virtual_file");
    if (filesystem->setCurrentWorkingDirectory("X:/__capture")) throw std::runtime_error("virtual_working_directory_failed");
    for (const auto& input : request.files) if (!filesystem->getBufferForFile(input.virtualPath)) throw std::runtime_error("virtual_input_unreadable");
    FileSystemOptions options;
    // Intrusive ownership is required: CompilerInstance retains this manager.
    auto files = llvm::makeIntrusiveRefCnt<FileManager>(options,filesystem);
    Diagnostics diagnostics(out);
    tooling::ToolInvocation invocation(request.arguments,std::make_unique<Action>(out),files.get());
    invocation.setDiagnosticConsumer(&diagnostics);
    const bool succeeded = invocation.run();
    for (const auto& key : out.consumed) out.rows("consumed_file_keys").push_back(key);
    const bool discovery = text(request.document,"phase") == "discovery";
    bool setMatches = true; std::set<std::string> expected;
    for (const auto& key : *request.document.getArray("expected_consumed_file_keys")) expected.insert(key.getAsString()->str());
    if (!discovery) setMatches = expected == out.consumed && out.rows("discovery_requests").empty();
    std::string reason = !setMatches ? "consumed_input_set_mismatch" : !succeeded || out.compilerError ? "compiler_failed" : out.unsupported ? "unsupported_source_construct" : "";
    if (!reason.empty()) { out.result["status"] = !succeeded || !setMatches ? "failed" : "partial"; out.result["stop_reason"] = reason; }
    if (discovery || text(out.result,"status") == "failed") {
        for (auto name : {"entities","types","signatures","appearances","bodies","operations","control_flow","external_symbols","unresolved_references","entity_modifiers","annotations","preprocessing"}) out.result[name] = Array{};
        // Discovery may retain located diagnostics, but never the unused spans of
        // discarded preprocessing or AST payload. Keep only diagnostic provenance.
        std::set<std::string> diagnosticSpans;
        for (const auto& diagnostic : out.rows("diagnostics"))
            if (auto span = diagnostic.getAsObject()->getString("span_key")) diagnosticSpans.insert(span->str());
        bool added = true;
        while (added) {
            added = false;
            for (const auto& span : out.rows("spans")) {
                const auto& row = *span.getAsObject();
                if (diagnosticSpans.count(text(row,"key")))
                    if (auto cause = row.getString("cause_span_key")) added |= diagnosticSpans.insert(cause->str()).second;
            }
        }
        Array retainedSpans;
        for (const auto& span : out.rows("spans"))
            if (diagnosticSpans.count(text(*span.getAsObject(),"key"))) retainedSpans.push_back(span);
        out.result["spans"] = std::move(retainedSpans);
    }
    for (const auto& input : request.files) for (auto feature : {"parse","entities","types","modifiers","annotations","preprocessing","operations","control_flow","storage_events"}) {
        std::string status = "complete", featureReason;
        if (std::string(feature) == "control_flow" || std::string(feature) == "storage_events") { status = "not_attempted"; featureReason = "not_requested"; }
        else if (!out.consumed.count(input.key)) { status = "not_attempted"; featureReason = "input_not_consumed"; }
        else if (discovery && std::string(feature) != "parse") { status = "not_attempted"; featureReason = "discovery_only"; }
        else if (!reason.empty()) { status = !succeeded || !setMatches ? "failed" : "partial"; featureReason = reason; }
        out.rows("coverage").push_back(Object{{"file_key",input.key},{"feature",feature},{"status",status},{"reason",nullable(featureReason)},{"unparsed_span_keys",Array{}}});
    }
    out.budget(); validateSchema("native-worker-result",Object(out.result)); return std::move(out.result);
}
}
