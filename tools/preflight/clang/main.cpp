#include <clang/AST/Decl.h>
#include <clang/Analysis/CFG.h>
#include <clang/Basic/Version.h>
#include <clang/Frontend/ASTUnit.h>
#include <clang/Tooling/Tooling.h>
#include <iostream>

int main() {
    auto ast = clang::tooling::buildASTFromCodeWithArgs(
        "int probe(int x) { if (x > 0) return x; return 0; }",
        {"-std=c++17", "-fms-extensions"}, "preflight.cpp");
    if (!ast || ast->getDiagnostics().hasErrorOccurred()) return 1;
    for (auto* declaration : ast->getASTContext().getTranslationUnitDecl()->decls()) {
        auto* function = llvm::dyn_cast<clang::FunctionDecl>(declaration);
        if (!function || function->getNameAsString() != "probe") continue;
        if (function->getNumParams() != 1 || !function->getReturnType()->isIntegerType()) return 2;
        auto cfg = clang::CFG::buildCFG(function, function->getBody(), &ast->getASTContext(), {});
        if (!cfg || cfg->size() < 4) return 3;
        unsigned branches = 0;
        for (auto* block : *cfg) if (block->succ_size() == 2) ++branches;
        if (branches != 1) return 4;
        std::cout << "Clang " << CLANG_VERSION_STRING << ": probe(int), int result, "
                  << cfg->size() << " CFG blocks, " << branches << " branch passed\n";
        return 0;
    }
    return 5;
}
