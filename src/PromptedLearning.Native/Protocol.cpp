#include "Protocol.h"
#include <windows.h>
#include <clang/Basic/Version.h>
#include <llvm/Support/Base64.h>
#include <llvm/Support/SHA256.h>
#include <algorithm>
#include <charconv>
#include <regex>
#include <set>
#include <stdexcept>

namespace pl {
namespace {
[[noreturn]] void fail(const std::string& message) { throw std::runtime_error(message); }
void quote(llvm::StringRef value, std::string& output) {
    static constexpr char hex[] = "0123456789abcdef";
    output += '"';
    for (unsigned char c : value.bytes()) {
        switch (c) {
        case '"': output += "\\\""; break; case '\\': output += "\\\\"; break;
        case '\b': output += "\\b"; break; case '\f': output += "\\f"; break;
        case '\n': output += "\\n"; break; case '\r': output += "\\r"; break;
        case '\t': output += "\\t"; break;
        default: if (c < 32) { output += "\\u00"; output += hex[c >> 4]; output += hex[c & 15]; }
                 else output += static_cast<char>(c);
        }
    }
    output += '"';
}
void write(const Value& value, std::string& output) {
    switch (value.kind()) {
    case Value::Null: output += "null"; break;
    case Value::Boolean: output += *value.getAsBoolean() ? "true" : "false"; break;
    case Value::Number: if (auto n = value.getAsInteger()) output += std::to_string(*n); else fail("non_int64_number"); break;
    case Value::String: quote(*value.getAsString(), output); break;
    case Value::Array: {
        output += '['; bool first = true;
        for (const auto& child : *value.getAsArray()) { if (!first) output += ','; first = false; write(child, output); }
        output += ']'; break;
    }
    case Value::Object: {
        std::map<std::string, const Value*> sorted;
        for (const auto& member : *value.getAsObject()) sorted.emplace(member.first.str(), &member.second);
        output += '{'; bool first = true;
        for (const auto& member : sorted) { if (!first) output += ','; first = false; quote(member.first, output); output += ':'; write(*member.second, output); }
        output += '}'; break;
    }
    }
}

// LLVM's object parser cannot preserve duplicate members. Inspect token structure
// first; all integers are range-checked before any conversion through floating point.
class TokenGuard {
    llvm::StringRef input; size_t pos = 0;
    void space() { while (pos < input.size() && (input[pos] == ' ' || input[pos] == '\t' || input[pos] == '\r' || input[pos] == '\n')) ++pos; }
    char peek() { space(); return pos < input.size() ? input[pos] : '\0'; }
    void take(char c) { if (peek() != c) fail("malformed_json"); ++pos; }
    std::string string() {
        space(); const auto begin = pos; take('"');
        while (pos < input.size()) { char c = input[pos++]; if (c == '\\') { if (pos == input.size()) fail("malformed_escape"); ++pos; }
            else if (c == '"') { auto parsed = llvm::json::parse(input.slice(begin, pos)); if (!parsed || !parsed->getAsString()) { if (!parsed) llvm::consumeError(parsed.takeError()); fail("malformed_string"); } return parsed->getAsString()->str(); }
        }
        fail("unterminated_string");
    }
    void value(unsigned depth) {
        if (depth > 128) fail("json_depth_limit");
        char c = peek();
        if (c == '{') { ++pos; std::set<std::string> keys; if (peek() == '}') { ++pos; return; }
            do { if (!keys.insert(string()).second) fail("duplicate_json_property"); take(':'); value(depth + 1); if (peek() != ',') break; ++pos; } while (true); take('}');
        } else if (c == '[') { ++pos; if (peek() == ']') { ++pos; return; }
            do { value(depth + 1); if (peek() != ',') break; ++pos; } while (true); take(']');
        } else if (c == '"') { (void)string(); }
        else {
            const auto begin = pos;
            while (pos < input.size() && input[pos] != ',' && input[pos] != '}' && input[pos] != ']' && input[pos] != ' ' && input[pos] != '\t' && input[pos] != '\r' && input[pos] != '\n') ++pos;
            auto token = input.slice(begin, pos);
            if (token == "true" || token == "false" || token == "null") return;
            if (token.empty()) fail("missing_json_value");
            int64_t number{}; auto result = std::from_chars(token.begin(), token.end(), number);
            if (result.ec != std::errc{} || result.ptr != token.end()) fail("non_int64_number");
        }
    }
public:
    explicit TokenGuard(llvm::StringRef bytes) : input(bytes) {}
    void run() { value(0); space(); if (pos != input.size()) fail("trailing_json_data"); }
};

const Value& contract() {
    static const Value root = [] {
        auto module = GetModuleHandleW(nullptr);
        auto resource = FindResourceW(module,MAKEINTRESOURCEW(2),MAKEINTRESOURCEW(10));
        auto loaded = resource ? LoadResource(module,resource) : nullptr;
        auto bytes = loaded ? LockResource(loaded) : nullptr;
        if (!bytes) fail("embedded_contract_missing");
        auto parsed = llvm::json::parse(llvm::StringRef(static_cast<const char*>(bytes),SizeofResource(module,resource)));
        if (!parsed) fail("embedded_contract_invalid"); return std::move(*parsed);
    }();
    return root;
}
void validate(const Value& schemaValue, const Value& value, unsigned depth = 0) {
    if (depth > 128) fail("schema_depth_limit");
    const auto& schema = *schemaValue.getAsObject();
    if (auto reference = schema.getString("$ref")) {
        if (!reference->consume_front("#/$defs/")) fail("unsupported_schema_reference");
        const auto* target = contract().getAsObject()->getObject("$defs")->get(*reference);
        if (!target) fail("missing_schema_reference"); validate(*target, value, depth + 1); return;
    }
    if (auto type = schema.getString("type")) {
        bool matches = (*type == "object" && value.getAsObject()) || (*type == "array" && value.getAsArray())
            || (*type == "string" && value.getAsString()) || (*type == "integer" && value.getAsInteger())
            || (*type == "boolean" && value.getAsBoolean()) || (*type == "null" && value.kind() == Value::Null);
        if (!matches) fail("schema_type");
    }
    if (auto constant = schema.get("const")) if (canonical(*constant) != canonical(value)) fail("schema_const");
    if (auto choices = schema.getArray("enum")) {
        bool found = false; for (const auto& choice : *choices) found |= canonical(choice) == canonical(value);
        if (!found) fail("schema_enum");
    }
    if (auto choices = schema.getArray("oneOf")) {
        unsigned matches = 0; for (const auto& choice : *choices) { try { validate(choice, value, depth + 1); ++matches; } catch (const std::runtime_error&) {} }
        if (matches != 1) fail("schema_one_of");
    }
    if (auto rules = schema.getArray("allOf")) for (const auto& rule : *rules) validate(rule, value, depth + 1);
    if (auto condition = schema.get("if")) {
        bool matches = true; try { validate(*condition, value, depth + 1); } catch (const std::runtime_error&) { matches = false; }
        if (auto branch = schema.get(matches ? "then" : "else")) validate(*branch, value, depth + 1);
    }
    if (auto object = value.getAsObject()) {
        if (auto required = schema.getArray("required")) for (const auto& name : *required) if (!object->get(*name.getAsString())) fail("schema_required:" + name.getAsString()->str());
        auto properties = schema.getObject("properties");
        for (const auto& member : *object) {
            auto child = properties ? properties->get(member.first) : nullptr;
            if (child) validate(*child, member.second, depth + 1);
            else if (schema.getBoolean("additionalProperties") == false) fail("schema_unknown_property:" + member.first.str());
        }
    }
    if (auto array = value.getAsArray()) {
        if (auto min = schema.getInteger("minItems")) if (array->size() < static_cast<size_t>(*min)) fail("schema_min_items");
        if (auto item = schema.get("items")) for (const auto& child : *array) validate(*item, child, depth + 1);
        if (schema.getBoolean("uniqueItems") == true) { std::set<std::string> seen; for (const auto& child : *array) if (!seen.insert(canonical(child)).second) fail("schema_duplicate_item"); }
        if (auto contains = schema.get("contains")) { bool found = false; for (const auto& child : *array) { try { validate(*contains, child, depth + 1); found = true; } catch (const std::runtime_error&) {} } if (!found) fail("schema_contains"); }
    }
    if (auto string = value.getAsString()) {
        if (auto min = schema.getInteger("minLength")) if (string->size() < static_cast<size_t>(*min)) fail("schema_min_length");
        if (auto pattern = schema.getString("pattern")) if (!std::regex_search(string->str(), std::regex(pattern->str()))) fail("schema_pattern");
    }
    if (auto n = value.getAsInteger()) {
        if (auto min = schema.getInteger("minimum")) if (*n < *min) fail("schema_minimum");
        if (auto max = schema.getInteger("maximum")) if (*n > *max) fail("schema_maximum");
    }
}
bool safePath(llvm::StringRef path) {
    if (path.empty() || path.starts_with("/") || path.ends_with("/") || path.contains('\\') || path.contains(':') || path.contains('\0')) return false;
    llvm::SmallVector<llvm::StringRef> parts; path.split(parts, '/');
    for (auto part : parts) if (part.empty() || part == "." || part == "..") return false;
    return true;
}
}

std::string canonical(const Value& value) { std::string result; write(value, result); return result; }
std::string sha256(llvm::StringRef bytes) {
    llvm::SHA256 hash; hash.update(bytes); const auto digest = hash.final();
    static constexpr char hex[] = "0123456789abcdef"; std::string result;
    for (auto b : digest) { result += hex[b >> 4]; result += hex[b & 15]; } return result;
}
Value strictJson(llvm::StringRef bytes) {
    if (!llvm::json::isUTF8(bytes)) fail("invalid_utf8_json"); TokenGuard(bytes).run();
    auto parsed = llvm::json::parse(bytes); if (!parsed) { llvm::consumeError(parsed.takeError()); fail("malformed_json"); }
    return std::move(*parsed);
}
void validateSchema(llvm::StringRef definition, const Value& value) {
    auto schema = contract().getAsObject()->getObject("$defs")->get(definition);
    if (!schema) fail("unknown_contract"); validate(*schema, value);
}
std::string text(const Object& object, llvm::StringRef name) { auto value = object.getString(name); if (!value) fail("missing_string:" + name.str()); return value->str(); }
int64_t integer(const Object& object, llvm::StringRef name) { auto value = object.getInteger(name); if (!value) fail("missing_integer:" + name.str()); return *value; }
const Input& Request::file(llvm::StringRef key) const { for (const auto& input : files) if (input.key == key) return input; fail("unknown_file_key"); }

Request parseRequest(llvm::StringRef bytes, llvm::StringRef executableHash) {
    if (bytes.empty() || bytes.size() > EnvelopeLimit || bytes.back() != '\n' || bytes.drop_back().contains('\n') || bytes.contains('\r')) fail("invalid_lf_frame");
    auto parsed = strictJson(bytes.drop_back()); validateSchema("native-worker-request", parsed);
    Request result; result.document = *parsed.getAsObject(); auto& doc = result.document;
    const auto& worker = *doc.getObject("worker");
    if (text(worker, "version") != WorkerVersion || text(worker, "clang_version") != CLANG_VERSION_STRING || text(worker, "binary_sha256") != executableHash) fail("worker_identity_mismatch");
    auto hashed = doc; hashed.erase("request_fingerprint");
    if (sha256(canonical(std::move(hashed))) != text(doc, "request_fingerprint")) fail("request_fingerprint_mismatch");
    Object semantic;
    for (auto name : {"snapshot_id", "context_id", "unit_id", "capture_index_fingerprint", "worker", "target", "main_file_key", "files", "options", "path_bindings", "requested_features", "expected_consumed_file_keys"}) semantic[name] = *doc.get(name);
    for (auto& input : *semantic.getArray("files")) input.getAsObject()->erase("bytes_base64");
    for (auto& binding : *semantic.getArray("path_bindings")) binding.getAsObject()->erase("physical_path");
    if (sha256(canonical(Array{"native-worker-semantic-input/1", std::move(semantic)})) != text(doc, "semantic_input_fingerprint")) fail("semantic_fingerprint_mismatch");
    if (doc.getArray("requested_features")->size() != 1) fail("feature_outside_m1b");
    const auto& target = *doc.getObject("target");
    if (text(target, "triple") != "x86_64-pc-windows-msvc" || text(target, "ms_compatibility_version") != "19.44") fail("target_outside_pilot");
    const auto& budgets = *doc.getObject("budgets");
    result.timeMs = integer(budgets,"time_ms"); result.memoryBytes = integer(budgets,"memory_bytes");
    result.maxResultBytes = integer(budgets,"max_result_bytes"); result.maxRecords = integer(budgets,"max_records"); result.maxOperations = integer(budgets,"max_operations");
    std::set<std::string> keys, paths; int64_t total = 0;
    for (const auto& item : *doc.getArray("files")) {
        const auto& entry = *item.getAsObject(); Input file;
        file.key = text(entry,"key"); file.logicalPath = text(entry,"logical_path"); file.virtualPath = "X:/__capture/" + file.logicalPath;
        if (!keys.insert(file.key).second || !safePath(file.logicalPath) || !paths.insert(file.virtualPath).second) fail("duplicate_or_unsafe_input");
        auto encoded = text(entry,"bytes_base64"); std::vector<char> decoded;
        if (auto error = llvm::decodeBase64(encoded, decoded)) { llvm::consumeError(std::move(error)); fail("invalid_base64"); }
        file.bytes.assign(decoded.begin(), decoded.end());
        if (llvm::encodeBase64(file.bytes) != encoded || static_cast<int64_t>(file.bytes.size()) != integer(entry,"byte_length") || sha256(file.bytes) != text(entry,"original_sha256")) fail("captured_bytes_mismatch");
        if (text(entry,"encoding") != "utf-8" || !llvm::json::isUTF8(file.bytes)) fail("encoding_outside_native_pilot");
        const bool bom = llvm::StringRef(file.bytes).starts_with("\xef\xbb\xbf");
        if (integer(entry,"bom_bytes") != (bom ? 3 : 0)) fail("bom_mismatch");
        total += static_cast<int64_t>(file.bytes.size()); if (total > integer(budgets,"max_input_bytes")) fail("input_budget_exceeded");
        result.files.push_back(std::move(file));
    }
    result.file(text(doc,"main_file_key"));
    for (const auto& key : *doc.getArray("expected_consumed_file_keys")) result.file(*key.getAsString());
    result.arguments = {"clang-tool", "-fsyntax-only", "-x", "c++", "-std=c++17", "--target=x86_64-pc-windows-msvc", "-fms-extensions", "-fms-compatibility", "-fms-compatibility-version=19.44", "-nostdinc", "-fno-delayed-template-parsing"};
    for (const auto& option : *doc.getArray("options")) {
        const auto& entry = *option.getAsObject();
        if (text(entry,"kind") == "literal") {
            auto value = text(entry,"value");
            if (value == "-DNDEBUG" || value == "-std=c++17" || value == "-fms-extensions" || value == "-fms-compatibility" || value == "-fms-compatibility-version=19.44") result.arguments.push_back(value);
            else fail("unsupported_native_option:" + value);
        } else {
            auto optionName = text(entry,"option"); if (optionName != "-I" && optionName != "/I") fail("unsupported_path_option");
            auto root = text(entry,"root_key"); const Object* binding = nullptr;
            for (const auto& item : *doc.getArray("path_bindings")) if (text(*item.getAsObject(),"root_key") == root) { if (binding) fail("duplicate_root_binding"); binding = item.getAsObject(); }
            if (!binding || text(*binding,"role") != "captured_input") fail("external_paths_outside_pilot");
            auto relative = text(entry,"relative_path"); auto prefix = text(*binding,"logical_prefix");
            if ((!relative.empty() && !safePath(relative)) || !safePath(prefix)) fail("unsafe_path_option");
            result.arguments.push_back("-IX:/__capture/" + prefix + (relative.empty() ? "" : "/" + relative));
        }
    }
    result.arguments.push_back(result.file(text(doc,"main_file_key")).virtualPath);
    return result;
}
Object emptyResult(const Request& request) {
    Object output{{"schema","native-worker-result/1"},{"status","complete"},{"stop_reason",nullptr}};
    for (auto name : {"request_id","request_fingerprint","semantic_input_fingerprint","snapshot_id","context_id","unit_id","worker"}) output[name] = *request.document.get(name);
    for (auto name : {"spans","entities","types","signatures","appearances","bodies","operations","control_flow","external_symbols","unresolved_references","coverage","diagnostics","discovery_requests","entity_modifiers","annotations","preprocessing","consumed_file_keys"}) output[name] = Array{};
    return output;
}
}
