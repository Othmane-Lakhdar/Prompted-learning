#include "Protocol.h"
#include <llvm/Support/MemoryBuffer.h>
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <thread>

namespace {
class Limits {
    HANDLE job = nullptr;
    std::mutex mutex;
    std::condition_variable condition;
    bool finished = false;
    std::thread timer;
public:
    explicit Limits(const pl::Request& request) {
        job = CreateJobObjectW(nullptr,nullptr);
        if (!job) throw std::runtime_error("job_create_failed");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_PROCESS_TIME;
        limits.ProcessMemoryLimit = static_cast<SIZE_T>(request.memoryBytes);
        if (request.timeMs > INT64_MAX / 10000) throw std::runtime_error("time_budget_overflow");
        limits.BasicLimitInformation.PerProcessUserTimeLimit.QuadPart = request.timeMs * 10000;
        if (!SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits)) || !AssignProcessToJobObject(job,GetCurrentProcess())) {
            CloseHandle(job); job = nullptr; throw std::runtime_error("job_limit_install_failed");
        }
        timer = std::thread([this, milliseconds = request.timeMs] {
            std::unique_lock<std::mutex> lock(mutex);
            if (!condition.wait_for(lock,std::chrono::milliseconds(milliseconds),[this] { return finished; })) TerminateProcess(GetCurrentProcess(),124);
        });
    }
    ~Limits() { { std::lock_guard<std::mutex> lock(mutex); finished = true; } condition.notify_all(); if (timer.joinable()) timer.join(); if (job) CloseHandle(job); }
};
std::string executableHash() {
    std::wstring path(32768,L'\0'); auto size = GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));
    if (!size || size == path.size()) throw std::runtime_error("worker_path_unavailable"); path.resize(size);
    HANDLE file = CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("worker_hash_open_failed");
    std::string bytes; char buffer[65536]; DWORD count = 0;
    while (true) {
        if (!ReadFile(file,buffer,sizeof(buffer),&count,nullptr)) { CloseHandle(file); throw std::runtime_error("worker_hash_read_failed"); }
        if (!count) break;
        bytes.append(buffer,count);
    }
    CloseHandle(file);
    return pl::sha256(bytes);
}
}
int main() {
    _setmode(_fileno(stdin),_O_BINARY); _setmode(_fileno(stdout),_O_BINARY);
    try {
        std::string frame; frame.reserve(65536); char block[8192];
        while (std::cin) { std::cin.read(block,sizeof(block)); auto size = std::cin.gcount(); if (frame.size() + static_cast<size_t>(size) > pl::EnvelopeLimit) throw std::runtime_error("envelope_limit_exceeded"); frame.append(block,static_cast<size_t>(size)); }
        auto request = pl::parseRequest(frame,executableHash());
        Limits limits(request);
        auto result = pl::canonical(pl::extract(request)) + "\n";
        if (static_cast<int64_t>(result.size()) > request.maxResultBytes) throw std::runtime_error("result_budget_exceeded");
        std::cout.write(result.data(),static_cast<std::streamsize>(result.size())); std::cout.flush();
        return std::cout ? 0 : 3;
    } catch (const std::exception& error) { std::cerr << "native_worker_rejected: " << error.what() << '\n'; return 2; }
}
