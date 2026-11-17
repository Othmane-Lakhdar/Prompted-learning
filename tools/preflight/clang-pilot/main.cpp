// Capability check for the specified fixture; not an extractor implementation.
#include <clang/AST/Attr.h>
#include <clang/AST/Decl.h>
#include <clang/Analysis/CFG.h>
#include <clang/Basic/Version.h>
#include <clang/Frontend/ASTUnit.h>
#include <clang/Tooling/Tooling.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <set>

int main(int argc, char** argv) {
    if (argc != 2 && (argc != 3 || std::string(argv[2]) != "--dump")) return 10;
    std::filesystem::path source(argv[1]);
    std::ifstream input(source, std::ios::binary);
    if (!input) return 11;
    std::string bytes((std::istreambuf_iterator<char>(input)), {});
    auto ast = clang::tooling::buildASTFromCodeWithArgs(bytes,
        {"-std=c++17", "--target=x86_64-pc-windows-msvc", "-fms-extensions",
         "-fms-compatibility", "-fms-compatibility-version=19.44", "-DNDEBUG",
         "-I" + source.parent_path().generic_string()}, source.generic_string());
    if (!ast || ast->getDiagnostics().hasErrorOccurred()) return 12;
    std::set<const clang::FunctionDecl*> identities;
    unsigned declarations = 0, definitions = 0, branches = 0, cfgs = 0;
    for (auto* top : ast->getASTContext().getTranslationUnitDecl()->decls()) {
        auto* ns = llvm::dyn_cast<clang::NamespaceDecl>(top);
        if (!ns || ns->getNameAsString() != "pilot") continue;
        for (auto* decl : ns->decls()) {
            auto* fn = llvm::dyn_cast<clang::FunctionDecl>(decl);
            if (!fn) continue;
            ++declarations;
            identities.insert(fn->getCanonicalDecl());
            if (!fn->isThisDeclarationADefinition()) continue;
            ++definitions;
            if (fn->getNameAsString() != "Choose") continue;
            if (argc == 3) fn->dump(); // Independent compiler oracle for M0 contracts.
            if (!fn->hasAttr<clang::NoInlineAttr>()) return 13;
            auto cfg = clang::CFG::buildCFG(fn, fn->getBody(), &ast->getASTContext(), {});
            if (!cfg) return 14;
            ++cfgs;
            for (auto* block : *cfg) if (block->succ_size() == 2) ++branches;
        }
    }
    if (identities.size() != 4 || declarations != 8 || definitions != 4 || cfgs != 1 || branches != 1)
        return 15;
    std::cout << "Clang " << CLANG_VERSION_STRING
              << ": Windows x64 MSVC19.44 dialect; internal header; noinline attribute; "
              << "4 identities, 8 declarations, 4 definitions, 1 Choose CFG, 1 branch passed\n";
    return 0;
}
