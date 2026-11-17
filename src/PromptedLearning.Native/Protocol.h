#pragma once
// LLVM's unused getAsNumber helper intentionally converts Int64 to double.
// Our protocol exclusively uses getAsInteger; suppress that external definition.
#pragma warning(push)
#pragma warning(disable: 4244)
#include <llvm/Support/JSON.h>
#pragma warning(pop)
#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace pl {
using Object = llvm::json::Object;
using Array = llvm::json::Array;
using Value = llvm::json::Value;
inline constexpr char WorkerVersion[] = "1";
inline constexpr size_t EnvelopeLimit = 64 * 1024 * 1024;

std::string canonical(const Value& value);
std::string sha256(llvm::StringRef bytes);
Value strictJson(llvm::StringRef bytes);
void validateSchema(llvm::StringRef definition, const Value& value);
std::string text(const Object& object, llvm::StringRef name);
int64_t integer(const Object& object, llvm::StringRef name);

struct Input {
    std::string key, logicalPath, virtualPath, bytes;
};
struct Request {
    Object document;
    std::vector<Input> files;
    std::vector<std::string> arguments;
    int64_t timeMs{}, memoryBytes{}, maxResultBytes{}, maxRecords{}, maxOperations{};
    const Input& file(llvm::StringRef key) const;
};
Request parseRequest(llvm::StringRef bytes, llvm::StringRef executableHash);
Object emptyResult(const Request& request);
Object extract(const Request& request);
}
