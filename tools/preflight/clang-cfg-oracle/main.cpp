// Independent bounded compiler observation. No extractor, storage-event
// producer, canonical model or dataflow implementation is linked or called.
#pragma warning(push)
#pragma warning(disable : 4244)
#include <llvm/Support/JSON.h>
#pragma warning(pop)
#include <clang/AST/ASTConsumer.h>
#include <clang/AST/ASTContext.h>
#include <clang/AST/Decl.h>
#include <clang/AST/Expr.h>
#include <clang/Analysis/CFG.h>
#include <clang/Basic/Version.h>
#include <clang/Frontend/CompilerInstance.h>
#include <clang/Frontend/FrontendAction.h>
#include <clang/Index/USRGeneration.h>
#include <clang/Lex/Lexer.h>
#include <clang/Tooling/Tooling.h>
#include <llvm/Support/FormatVariadic.h>
#include <llvm/Support/SHA256.h>
#include <llvm/Support/VirtualFileSystem.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

using llvm::json::Array;
using llvm::json::Object;
using llvm::json::Value;

static void require(bool condition, const std::string& reason) {
    if (!condition) throw std::runtime_error(reason);
}

static std::string hash(llvm::StringRef bytes) {
    const auto digest = llvm::SHA256::hash(llvm::arrayRefFromStringRef(bytes));
    constexpr char digits[] = "0123456789abcdef";
    std::string result;
    for (const auto byte : digest) {
        result.push_back(digits[byte >> 4]);
        result.push_back(digits[byte & 15]);
    }
    return result;
}

static std::string read(const std::filesystem::path& path) {
    require(std::filesystem::file_size(path) <= 1024 * 1024, "input_byte_limit");
    std::ifstream input(path, std::ios::binary);
    require(static_cast<bool>(input), "input_open_failed");
    return std::string(std::istreambuf_iterator<char>(input), {});
}

static std::string sourceName(llvm::StringRef input) {
    auto result = input.str();
    std::replace(result.begin(), result.end(), '\\', '/');
    return result.substr(result.find_last_of('/') + 1);
}

static const char* elementKind(clang::CFGElement::Kind kind) {
    switch (kind) {
    case clang::CFGElement::Initializer: return "Initializer";
    case clang::CFGElement::ScopeBegin: return "ScopeBegin";
    case clang::CFGElement::ScopeEnd: return "ScopeEnd";
    case clang::CFGElement::NewAllocator: return "NewAllocator";
    case clang::CFGElement::LifetimeEnds: return "LifetimeEnds";
    case clang::CFGElement::LoopExit: return "LoopExit";
    case clang::CFGElement::Statement: return "Statement";
    case clang::CFGElement::Constructor: return "Constructor";
    case clang::CFGElement::CXXRecordTypedCall: return "CXXRecordTypedCall";
    case clang::CFGElement::AutomaticObjectDtor: return "AutomaticObjectDtor";
    case clang::CFGElement::DeleteDtor: return "DeleteDtor";
    case clang::CFGElement::BaseDtor: return "BaseDtor";
    case clang::CFGElement::MemberDtor: return "MemberDtor";
    case clang::CFGElement::TemporaryDtor: return "TemporaryDtor";
    case clang::CFGElement::CleanupFunction: return "CleanupFunction";
    }
    throw std::runtime_error("unrecognized_cfg_element");
}

class Observer {
    clang::ASTContext& context;
    std::map<const void*, std::string> paths;
    std::map<std::string, const clang::Stmt*> statements;
    Array sourceNodes;

    Value mapped(const clang::Stmt* statement) const {
        if (statement == nullptr) return nullptr;
        const auto found = paths.find(statement);
        require(found != paths.end(), "unmapped_compiler_stmt_pointer");
        return found->second;
    }

    Object origin(clang::SourceRange range) const {
        const auto& source = context.getSourceManager();
        require(range.isValid() && !range.getBegin().isMacroID() &&
            !range.getEnd().isMacroID(), "unsupported_source_origin");
        const auto end = clang::Lexer::getLocForEndOfToken(
            range.getEnd(), 0, source, context.getLangOpts());
        require(end.isValid() &&
            source.getFileID(range.getBegin()) == source.getFileID(end),
            "invalid_source_range");
        return Object{{"file", sourceName(source.getFilename(range.getBegin()))},
            {"start_byte", static_cast<int64_t>(source.getFileOffset(range.getBegin()))},
            {"end_byte", static_cast<int64_t>(source.getFileOffset(end))}};
    }

    void mapStatement(const clang::Stmt* statement, const std::string& path) {
        require(statement != nullptr && paths.emplace(statement, path).second,
            "duplicate_source_stmt_pointer");
        statements.emplace(path, statement);
        sourceNodes.emplace_back(Object{{"path", path},
            {"compiler_kind", statement->getStmtClassName()},
            {"implicit", llvm::isa<clang::ImplicitCastExpr>(statement)},
            {"origin", origin(statement->getSourceRange())}});
        unsigned ordinal = 0;
        if (const auto declaration = llvm::dyn_cast<clang::DeclStmt>(statement)) {
            for (const auto child : declaration->decls()) {
                const auto variable = llvm::dyn_cast<clang::VarDecl>(child);
                require(variable != nullptr, "unsupported_declstmt_child");
                const auto childPath = path + "/" + std::to_string(ordinal++);
                require(paths.emplace(variable, childPath).second,
                    "duplicate_source_decl_pointer");
                sourceNodes.emplace_back(Object{{"path", childPath},
                    {"compiler_kind", "VarDecl"}, {"implicit", false},
                    {"origin", origin(variable->getSourceRange())}});
                if (variable->hasInit()) mapStatement(variable->getInit(), childPath + "/0");
            }
            return;
        }
        for (const auto child : statement->children())
            if (child != nullptr)
                mapStatement(child, path + "/" + std::to_string(ordinal++));
    }

    static bool contains(const clang::Stmt* root, const clang::Stmt* needle) {
        if (root == nullptr) return false;
        if (root == needle) return true;
        for (const auto child : root->children())
            if (contains(child, needle)) return true;
        return false;
    }

    bool containsPath(const clang::CFGBlock& block, const std::string& path) const {
        const auto needle = statements.at(path);
        for (const auto& element : block)
            if (const auto member = element.getAs<clang::CFGStmt>())
                if (contains(member->getStmt(), needle)) return true;
        // Visit only the condition, never both bodies of a branching IfStmt.
        return contains(block.getTerminatorCondition(), needle);
    }

public:
    explicit Observer(clang::ASTContext& value) : context(value) {}

    Object inspect(clang::FunctionDecl& function) {
        require(function.getNumParams() == 1 &&
            function.getParamDecl(0)->getType()->isSpecificBuiltinType(clang::BuiltinType::Int) &&
            function.getReturnType()->isSpecificBuiltinType(clang::BuiltinType::Int),
            "unexpected_choose_signature");
        llvm::SmallString<128> usr;
        require(!clang::index::generateUSRForDecl(function.getCanonicalDecl(), usr) &&
            usr == "c:@N@pilot@F@Choose#I#", "unexpected_choose_identity");
        mapStatement(function.getBody(), "0");
        require(sourceNodes.size() == 22, "unexpected_source_node_count");

        clang::CFG::BuildOptions options;
        options.PruneTriviallyFalseEdges = false;
        options.AddEHEdges = false;
        options.AddInitializers = false;
        options.AddImplicitDtors = false;
        options.AddLifetime = false;
        options.AddLoopExit = false;
        options.AddTemporaryDtors = false;
        options.AddScopes = false;
        options.AddStaticInitBranches = false;
        options.AddCXXNewAllocator = false;
        options.AddCXXDefaultInitExprInCtors = false;
        options.AddCXXDefaultInitExprInAggregates = false;
        options.AddRichCXXConstructors = false;
        options.MarkElidedCXXConstructors = false;
        options.AddVirtualBaseBranches = false;
        options.OmitImplicitValueInitializers = false;
        const auto graph = clang::CFG::buildCFG(
            &function, function.getBody(), &context, options);
        require(graph != nullptr, "cfg_build_failed");

        std::vector<const clang::CFGBlock*> sorted(graph->begin(), graph->end());
        std::sort(sorted.begin(), sorted.end(), [](const auto left, const auto right) {
            return left->getBlockID() < right->getBlockID();
        });
        Array blocks;
        int64_t memberCount = 0;
        unsigned branches = 0;
        for (const auto block : sorted) {
            Array members;
            unsigned ordinal = 0;
            for (const auto& element : *block) {
                const auto member = element.getAs<clang::CFGStmt>();
                require(member.has_value() && element.getKind() == clang::CFGElement::Statement,
                    std::string("unsupported_native_cfg_element:") + elementKind(element.getKind()));
                const auto statement = member->getStmt();
                members.emplace_back(Object{{"ordinal", ordinal++},
                    {"element_kind", elementKind(element.getKind())},
                    {"compiler_kind", statement->getStmtClassName()},
                    {"source_path", mapped(statement)}, {"same_ast_pointer", true},
                    {"origin", origin(statement->getSourceRange())}});
                ++memberCount;
            }
            const auto terminator = block->getTerminatorStmt();
            const auto condition = block->getTerminatorCondition();
            if (terminator != nullptr) {
                const auto conditional = llvm::dyn_cast<clang::IfStmt>(terminator);
                require(conditional != nullptr && conditional->getCond() == condition,
                    "unsupported_terminator_or_condition");
                ++branches;
            }
            Array successors;
            ordinal = 0;
            for (auto it = block->succ_begin(); it != block->succ_end(); ++it) {
                const auto adjacent = *it;
                const auto target = adjacent.getReachableBlock();
                require(adjacent.isReachable() && target != nullptr,
                    "unsupported_null_or_pruned_successor");
                std::string kind = "continuation";
                Value branchWitness = nullptr;
                if (terminator != nullptr) {
                    const bool hasThen = containsPath(*target, "0/1/1");
                    const bool hasElse = containsPath(*target, "0/1/2");
                    require(hasThen != hasElse, "branch_successor_not_identifiable_from_ast");
                    kind = hasThen ? "true_branch" : "false_branch";
                    branchWitness = hasThen ? "0/1/1" : "0/1/2";
                } else {
                    for (const auto& element : *block)
                        if (const auto member = element.getAs<clang::CFGStmt>())
                            if (llvm::isa<clang::ReturnStmt>(member->getStmt())) kind = "return";
                }
                successors.emplace_back(Object{{"ordinal", ordinal++},
                    {"target_block_id", target->getBlockID()}, {"kind", kind},
                    {"condition_source_path", mapped(condition)},
                    {"source_branch_body_witness", std::move(branchWitness)}});
            }
            Array predecessors;
            for (auto it = block->pred_begin(); it != block->pred_end(); ++it) {
                require(it->getReachableBlock() != nullptr, "unsupported_null_predecessor");
                predecessors.emplace_back(it->getReachableBlock()->getBlockID());
            }
            blocks.emplace_back(Object{{"block_id", block->getBlockID()},
                {"kind", block == &graph->getEntry() ? "entry" :
                    block == &graph->getExit() ? "exit" : "ordinary"},
                {"members", std::move(members)},
                {"terminator_source_path", mapped(terminator)},
                {"condition_source_path", mapped(condition)},
                {"predecessor_block_ids", std::move(predecessors)},
                {"successors", std::move(successors)}});
        }
        require(branches == 1, "unexpected_branch_count");

        // These source points were specified before this probe was written.
        // Observe only their compiler-block membership; do not generate events.
        Array sourcePoints;
        sourcePoints.emplace_back(Object{{"role", "parameter_entry"},
            {"source_path", nullptr}, {"block_id", graph->getEntry().getBlockID()},
            {"parameter_ordinal", 0},
            {"definition_parameter_origin", origin(function.getParamDecl(0)->getSourceRange())}});
        for (const auto& point : std::vector<std::pair<std::string, std::string>>{
            {"condition_read", "0/1/0/0/0"}, {"argument_read", "0/1/1/1/1/0"},
            {"true_assignment", "0/1/1"}, {"false_assignment", "0/1/2"},
            {"return_read", "0/2/0/0"}}) {
            Array matches;
            for (const auto block : sorted)
                if (containsPath(*block, point.second)) matches.emplace_back(block->getBlockID());
            require(matches.size() == 1, "scalar_source_point_block_ambiguous:" + point.first);
            sourcePoints.emplace_back(Object{{"role", point.first},
                {"source_path", point.second}, {"containing_block_ids", std::move(matches)},
                {"origin", origin(statements.at(point.second)->getSourceRange())}});
        }

        return Object{{"producer", "independent native compiler CFG observation; not extractor or storage-event analysis"},
            {"clang_version", CLANG_VERSION_STRING}, {"function_usr", usr.str().str()},
            {"cfg_build_options", Object{{"PruneTriviallyFalseEdges", false},
                {"AddEHEdges", false}, {"AddInitializers", false}, {"AddImplicitDtors", false},
                {"AddLifetime", false}, {"AddLoopExit", false}, {"AddTemporaryDtors", false},
                {"AddScopes", false}, {"AddStaticInitBranches", false},
                {"AddCXXNewAllocator", false}, {"AddCXXDefaultInitExprInCtors", false},
                {"AddCXXDefaultInitExprInAggregates", false}, {"AddRichCXXConstructors", false},
                {"MarkElidedCXXConstructors", false}, {"AddVirtualBaseBranches", false},
                {"OmitImplicitValueInitializers", false}, {"setAllAlwaysAdd", false}}},
            {"source_node_count", static_cast<int64_t>(sourceNodes.size())},
            {"source_nodes", std::move(sourceNodes)},
            {"cfg_block_count", static_cast<int64_t>(sorted.size())},
            {"cfg_member_count", memberCount},
            {"entry_block_id", graph->getEntry().getBlockID()},
            {"exit_block_id", graph->getExit().getBlockID()}, {"blocks", std::move(blocks)},
            {"predeclared_scalar_source_point_observations", std::move(sourcePoints)},
            {"unmapped_pointer_count", 0}, {"unsupported_cfg_element_count", 0}};
    }
};

class Consumer final : public clang::ASTConsumer {
    Object& output;
    bool& observed;
public:
    Consumer(Object& value, bool& done) : output(value), observed(done) {}
    void HandleTranslationUnit(clang::ASTContext& context) override {
        require(!context.getDiagnostics().hasErrorOccurred(), "compiler_diagnostics");
        clang::FunctionDecl* chosen = nullptr;
        for (const auto declaration : context.getTranslationUnitDecl()->decls())
            if (const auto space = llvm::dyn_cast<clang::NamespaceDecl>(declaration))
                if (space->getName() == "pilot")
                    for (const auto child : space->decls())
                        if (const auto function = llvm::dyn_cast<clang::FunctionDecl>(child))
                            if (function->getName() == "Choose" && function->doesThisDeclarationHaveABody()) {
                                require(chosen == nullptr, "multiple_choose_definitions");
                                chosen = function;
                            }
        require(chosen != nullptr, "missing_choose_definition");
        output = Observer(context).inspect(*chosen);
        observed = true;
    }
};

class Action final : public clang::ASTFrontendAction {
    Object& output;
    bool& observed;
public:
    Action(Object& value, bool& done) : output(value), observed(done) {}
    std::unique_ptr<clang::ASTConsumer> CreateASTConsumer(
        clang::CompilerInstance&, llvm::StringRef) override {
        return std::make_unique<Consumer>(output, observed);
    }
};

int main(int argc, char** argv) {
    try {
        require(argc == 3, "usage: clang-cfg-oracle frozen-native-folder output-json");
        require(std::string(CLANG_VERSION_STRING) == "19.1.7", "unexpected_clang_version");
        const auto source = read(std::filesystem::path(argv[1]) / "Arithmetic.cpp");
        const auto header = read(std::filesystem::path(argv[1]) / "Arithmetic.hpp");
        require(source.size() == 451 &&
            hash(source) == "0d45a74df34e1dc8e656f0c9e15adb7235a52672b06063ea74760aaa603a2603",
            "frozen_source_mismatch");
        require(header.size() == 179 &&
            hash(header) == "c4b178701eee5e955b7d34209334d84765296f61c4e2c22a3cf6a99c5134e2fd",
            "frozen_header_mismatch");
        auto files = llvm::makeIntrusiveRefCnt<llvm::vfs::InMemoryFileSystem>();
        require(files->addFile("X:/__capture/native/Arithmetic.cpp", 0,
            llvm::MemoryBuffer::getMemBufferCopy(source)), "vfs_source_failed");
        require(files->addFile("X:/__capture/native/Arithmetic.hpp", 0,
            llvm::MemoryBuffer::getMemBufferCopy(header)), "vfs_header_failed");
        require(!files->setCurrentWorkingDirectory("X:/__capture"), "vfs_cwd_failed");
        auto manager = llvm::makeIntrusiveRefCnt<clang::FileManager>(
            clang::FileSystemOptions{}, files);
        std::vector<std::string> arguments{"clang-tool", "-fsyntax-only", "-x", "c++",
            "-std=c++17", "--target=x86_64-pc-windows-msvc", "-fms-extensions",
            "-fms-compatibility", "-fms-compatibility-version=19.44", "-DNDEBUG",
            "-nostdinc", "-fno-delayed-template-parsing", "-IX:/__capture/native",
            "X:/__capture/native/Arithmetic.cpp"};
        Object report;
        bool observed = false;
        clang::tooling::ToolInvocation invocation(arguments,
            std::make_unique<Action>(report, observed), manager.get());
        require(invocation.run() && observed, "compiler_invocation_failed");
        Array command;
        for (const auto& argument : arguments) command.emplace_back(argument);
        report["compiler_arguments"] = std::move(command);
        report["inputs"] = Array{
            Object{{"name", "Arithmetic.cpp"}, {"byte_length", 451}, {"sha256", hash(source)}},
            Object{{"name", "Arithmetic.hpp"}, {"byte_length", 179}, {"sha256", hash(header)}}};
        std::ofstream output(argv[2], std::ios::binary | std::ios::trunc);
        output << llvm::formatv("{0:2}", Value(std::move(report))).str() << '\n';
        require(static_cast<bool>(output), "output_write_failed");
        std::cout << "native CFG observation written\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "oracle_failed: " << error.what() << '\n';
        return 1;
    }
}
