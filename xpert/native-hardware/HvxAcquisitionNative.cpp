#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define HVX_NATIVE_BUILD
#include "HvxAcquisitionNative.h"
#include <windows.h>
#include <shlobj.h>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <stdexcept>
#include <thread>
#include <unordered_map>
#include <vector>

namespace {
constexpr uint32_t ART=1, PICO=2;
struct Diagnostic { int32_t bridge=0; uint32_t vendor=0, win32=0; char text[1024]{}; };
thread_local Diagnostic last;
const char* Reason(int code) noexcept {
    switch(code) {
    case HVX_SDK_MISSING:return "Required SDK or dependency is unavailable";
    case HVX_EXPORT_MISSING:return "Required SDK entry point is unavailable";
    case HVX_OWNER_CONFLICT:return "Another process owns the native SDK family";
    case HVX_HANDLE_UNKNOWN:return "Handle is not owned by this native session";
    case HVX_HANDLE_BUSY:return "Another operation is active on this handle";
    case HVX_PATH_INVALID:return "SDK path is not an absolute local x64 DLL path";
    case HVX_PATH_CONFLICT:return "A different SDK version/path is already selected or loaded";
    case HVX_NATIVE_EXCEPTION:return "SDK raised a native exception; original code is retained";
    case HVX_INTERNAL_ERROR:return "Native bridge failed internally";
    case HVX_INVALID_ARGUMENT:return "Invalid bridge argument";
    case HVX_LEASE_TIMEOUT:return "Native ownership service did not respond within its deadline";
    case HVX_HANDLE_QUARANTINED:return "Failed open/create returned a handle; only explicit cleanup is permitted";
    case HVX_LEASE_RELEASE_PENDING:return "Native ownership release is unconfirmed; new SDK ownership is blocked";
    default:return "Unknown private bridge status";
    }
}
uint32_t Failure(uint32_t family,int code,const char* operation,uint32_t win32=0) noexcept {
    last.bridge=HVX_ART_ERROR(code); last.vendor=0; last.win32=win32;
    std::snprintf(last.text,sizeof(last.text),"bridge=%d; family=%s; operation=%s; win32=0x%08X; %s",
        last.bridge,family==ART?"ART":"Pico",operation,win32,Reason(code));
    return family==ART?static_cast<uint32_t>(last.bridge):HVX_PICO_ERROR(code);
}
void Vendor(uint32_t family,const char* operation,uint32_t status) noexcept {
    last.bridge=0; last.vendor=status; last.win32=0;
    std::snprintf(last.text,sizeof(last.text),"family=%s; operation=%s; rawVendorStatus=0x%08X (%lld)",
        family==ART?"ART":"Pico",operation,status,
        family==ART?static_cast<long long>(static_cast<int32_t>(status)):static_cast<long long>(status));
}
int32_t CopyText(const char* text,char* buffer,uint32_t capacity) noexcept {
    if(!buffer || capacity==0)return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);
    const size_t length=std::strlen(text);
    if(length>=capacity){buffer[0]=0;return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);}
    std::memcpy(buffer,text,length+1); return 0;
}

// All Acquire/ReleaseMutex calls execute on this dedicated thread. A caller
// thread may create a task and another thread clear it without violating Win32
// mutex thread affinity. No SDK call executes under the ownership-service lock.
class LeaseService {
public:
    struct Request {
        bool acquire; HANDLE complete=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        // 0=pending, 1=completed, 2=cancelled. CAS prevents a timed-out caller
        // from leaking an acquire which completed just before event signaling.
        std::atomic<int> state{0}; int result=HVX_INTERNAL_ERROR; uint32_t win32=0;
        std::atomic<bool> acknowledged{false};
        explicit Request(bool value):acquire(value){}
        ~Request(){if(complete)CloseHandle(complete);}
    };
    using Ticket=std::shared_ptr<Request>;
private:
    std::mutex mutex; std::condition_variable changed;
    std::deque<std::shared_ptr<Request>> queue;
    std::atomic<uint32_t> cleanupGuards{0},unacknowledgedReleases{0};
    std::atomic<bool> orphanedRelease{false};
#ifdef HVX_FAKE_TEST
    std::atomic<int32_t> releaseFault{0};
#endif
    const wchar_t* name;
    bool ReleaseOwner(HANDLE owner,uint32_t& error) noexcept {
#ifdef HVX_FAKE_TEST
        int32_t fail=1;
        if(releaseFault.compare_exchange_strong(fail,0)){error=ERROR_NOT_OWNER;return false;}
#endif
        if(ReleaseMutex(owner))return true;
        error=GetLastError();return false;
    }
    void Run() noexcept {
        HANDLE owner=CreateMutexW(nullptr,FALSE,name);
        uint64_t references=0;
        for(;;) {
            std::shared_ptr<Request> request;
            {std::unique_lock<std::mutex> lock(mutex);changed.wait(lock,[&]{return !queue.empty();});request=queue.front();queue.pop_front();}
            if(request->state.load()==2 && request->acquire) {request->result=HVX_LEASE_TIMEOUT;SetEvent(request->complete);continue;}
            bool acquiredReference=false;
            if(!owner){request->result=HVX_OWNER_CONFLICT;request->win32=GetLastError();}
            else if(request->acquire) {
                if(cleanupGuards.load() || unacknowledgedReleases.load() || orphanedRelease.load())
                    request->result=HVX_LEASE_RELEASE_PENDING;
                else {
                    DWORD acquired=references?WAIT_OBJECT_0:WaitForSingleObject(owner,0);
                    if(acquired==WAIT_OBJECT_0 || acquired==WAIT_ABANDONED) {
                        ++references;request->result=HVX_OK;acquiredReference=true;
                    } else {request->result=HVX_OWNER_CONFLICT;request->win32=GetLastError();}
                }
            } else {
#ifdef HVX_FAKE_TEST
                DWORD began=GetTickCount();
                while(releaseFault.load()==2 && GetTickCount()-began<5000)Sleep(1);
#endif
                if(references>1){--references;request->result=HVX_OK;}
                else if(references==1) {
                    if(ReleaseOwner(owner,request->win32)){references=0;request->result=HVX_OK;}
                    else request->result=HVX_INTERNAL_ERROR; // keep the actual owned reference
                } else {request->result=HVX_INTERNAL_ERROR;request->win32=ERROR_NOT_OWNER;}
            }
            int pending=0;
            if(!request->state.compare_exchange_strong(pending,1) && acquiredReference) {
                if(--references==0 && !ReleaseOwner(owner,request->win32)) {
                    ++references;orphanedRelease=true; // cancelled acquire cannot silently lose failed release ownership
                }
            }
            SetEvent(request->complete);
        }
    }
public:
    explicit LeaseService(const wchar_t* value):name(value) {
        // Pin the bridge once it owns a worker thread; do not run SDK cleanup in
        // DllMain or unload code while callbacks/ownership may still be live.
        HMODULE pinned=nullptr;
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&HvxGetLastErrorInfo),&pinned))throw std::runtime_error("pin failed");
        std::thread([this]{Run();}).detach();
    }
    Ticket Submit(bool acquire) {
        auto request=std::make_shared<Request>(acquire);
        if(!request->complete)throw std::runtime_error("lease completion event failed");
        {std::lock_guard<std::mutex> lock(mutex);queue.push_back(request);if(!acquire)++unacknowledgedReleases;} changed.notify_one();
        return request;
    }
    int Wait(const Ticket& request) {
        if(WaitForSingleObject(request->complete,2000)!=WAIT_OBJECT_0) {
            if(!request->acquire)return request->state.load()==1?request->result:HVX_LEASE_TIMEOUT;
            int pending=0;if(request->state.compare_exchange_strong(pending,2))return HVX_LEASE_TIMEOUT;
            // Completed before event signaling: atomic acquire publishes result.
            return request->result;
        }
        return request->result;
    }
    void BeginCleanup() noexcept {++cleanupGuards;}
    void EndCleanup() noexcept {--cleanupGuards;}
    Ticket BeginRelease(){return Submit(false);}
    int ConfirmRelease(const Ticket& request,bool retryCompletedFailure) {
        // A completed failure retained its reference. Explicit cleanup may retry
        // this same ticket; a pending request is only awaited, never enqueued twice.
        if(retryCompletedFailure && request->state.load()==1 && request->result!=0) {
            // Wait until the previous worker has actually signaled the event;
            // otherwise its delayed signal could wake a requeued ticket early.
            if(WaitForSingleObject(request->complete,2000)!=WAIT_OBJECT_0)return HVX_LEASE_TIMEOUT;
            ResetEvent(request->complete);request->result=HVX_INTERNAL_ERROR;request->win32=0;request->state=0;
            {std::lock_guard<std::mutex> lock(mutex);queue.push_back(request);}changed.notify_one();
        }
        int result=Wait(request);
        if(result==0 && !request->acknowledged.exchange(true))--unacknowledgedReleases;
        return result;
    }
    int Change(bool acquire,uint32_t* win32=nullptr) {
        if(acquire)return Wait(Submit(true));
        auto request=BeginRelease();int result=Wait(request);
        if(win32)*win32=request->state.load()==1?request->win32:0;
        if(result==0 && !request->acknowledged.exchange(true))--unacknowledgedReleases;
        // A temporary/global lease has no resource tombstone to retry. Never
        // silently allow a new SDK owner after its release cannot be confirmed.
        if(result!=0)orphanedRelease=true;
        return result;
    }
#ifdef HVX_FAKE_TEST
    void TestFault(int32_t mode) noexcept {releaseFault=mode;}
#endif
};
LeaseService& Leases(uint32_t family) {
    // Global family-wide exclusivity is conservative (not a per-device claim).
    // Non-cooperating software such as a vendor tool does not honor this lease.
    static LeaseService* art=new LeaseService(L"Global\\HVX.AcquisitionNative.ART.v1");
    static LeaseService* pico=new LeaseService(L"Global\\HVX.AcquisitionNative.Pico.v1");
    return family==ART?*art:*pico;
}
struct Lease {
    uint32_t family; bool held=false; int error;
    explicit Lease(uint32_t f):family(f),error(Leases(f).Change(true)){held=error==0;}
    void Transfer() noexcept {held=false;}
    uint32_t Finish(uint32_t status,const char* operation) {
        if(!held)return status;
        held=false;Diagnostic original=last;
        uint32_t win32=0;int code=Leases(family).Change(false,&win32);
        if(!code)return status;
        uint32_t result=Failure(family,HVX_LEASE_RELEASE_PENDING,operation,win32);
        last.vendor=original.vendor;
        size_t length=std::strlen(last.text);
        std::snprintf(last.text+length,sizeof(last.text)-length,
            "; temporaryLeaseReleaseUnconfirmed=true; originalBridge=%d; rawVendorStatus=0x%08X; leaseStatus=%d; no automatic owner reset",
            original.bridge,original.vendor,HVX_ART_ERROR(code));
        return result;
    }
    ~Lease(){if(held)try{Leases(family).Change(false);}catch(...){/* retain conservative ownership */}}
};
struct CleanupGuard {
    LeaseService& service;bool held=true;
    explicit CleanupGuard(uint32_t family):service(Leases(family)){service.BeginCleanup();}
    void Transfer() noexcept {held=false;}
    ~CleanupGuard(){if(held)service.EndCleanup();}
};
struct Resource {
    std::recursive_mutex operation;bool released=false, finalized=false, usable=true;
    uint32_t cleanupVendor=0;LeaseService::Ticket releaseTicket;
};
std::mutex registryMutex;
// Lifecycle only: no DI read/AI read/Pico capture shares this lock. It prevents
// a Create/Open already admitted by the lease service racing with SDK cleanup
// and reusing an identifier before the corresponding tombstone is confirmed.
std::mutex lifecycleMutex[2];
std::unordered_map<uintptr_t,std::shared_ptr<Resource>> artTasks;
std::unordered_map<int16_t,std::shared_ptr<Resource>> picoUnits;
template<class Key> std::shared_ptr<Resource> Find(std::unordered_map<Key,std::shared_ptr<Resource>>& registry,Key key) {
    std::lock_guard<std::mutex> lock(registryMutex);
    auto found=registry.find(key);return found==registry.end()?nullptr:found->second;
}
uint32_t ConfirmCleanup(uint32_t family,Resource& resource,const char* operation) {
    bool retry=static_cast<bool>(resource.releaseTicket);
    if(!retry)resource.releaseTicket=Leases(family).BeginRelease();
    int code=Leases(family).ConfirmRelease(resource.releaseTicket,retry);
    if(!code){Vendor(family,operation,resource.cleanupVendor);return resource.cleanupVendor;}
    uint32_t result=Failure(family,HVX_LEASE_RELEASE_PENDING,operation,
        resource.releaseTicket->state.load()==1?resource.releaseTicket->win32:0);
    last.vendor=resource.cleanupVendor;
    size_t length=std::strlen(last.text);
    std::snprintf(last.text+length,sizeof(last.text)-length,
        "; sdkCleanupCompleted=true; rawVendorStatus=0x%08X; leaseStatus=%d; explicit cleanup only confirms/retries this ticket (no SDK cleanup replay)",
        resource.cleanupVendor,HVX_ART_ERROR(code));
    return result;
}

std::wstring FullPath(const wchar_t* path) {
    if(!path || !*path)return {};
    std::wstring value(path);
    if(value.size()<3 || value[1]!=L':' || (value[2]!=L'\\' && value[2]!=L'/'))return {};
    if(value.find(L':',2)!=std::wstring::npos)return {}; // reject alternate data streams
    DWORD length=GetFullPathNameW(path,0,nullptr,nullptr);if(!length)return {};
    std::vector<wchar_t> buffer(length+1);
    if(!GetFullPathNameW(path,static_cast<DWORD>(buffer.size()),buffer.data(),nullptr))return {};
    return buffer.data();
}
bool IsX64Dll(const std::wstring& path) {
    if(path.empty())return false;
    // Do not accept link/reparse redirection at any component of a configured path.
    auto cursor=std::filesystem::path(path);
    while(!cursor.empty()) {
        DWORD attributes=GetFileAttributesW(cursor.c_str());
        if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_REPARSE_POINT))return false;
        auto parent=cursor.parent_path();if(parent==cursor)break;cursor=parent;
    }
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return false;
    IMAGE_DOS_HEADER dos{};DWORD read=0;bool valid=false;
    if(ReadFile(file,&dos,sizeof(dos),&read,nullptr) && read==sizeof(dos) && dos.e_magic==IMAGE_DOS_SIGNATURE && dos.e_lfanew>0 && dos.e_lfanew<16*1024*1024) {
        LARGE_INTEGER offset{};offset.QuadPart=dos.e_lfanew;IMAGE_NT_HEADERS64 nt{};
        valid=SetFilePointerEx(file,offset,nullptr,FILE_BEGIN) && ReadFile(file,&nt,sizeof(nt),&read,nullptr) && read==sizeof(nt) &&
            nt.Signature==IMAGE_NT_SIGNATURE && nt.FileHeader.Machine==IMAGE_FILE_MACHINE_AMD64 &&
            nt.OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC && (nt.FileHeader.Characteristics&IMAGE_FILE_DLL);
    }
    CloseHandle(file);return valid;
}
std::wstring KnownFolder(REFKNOWNFOLDERID id) {
    PWSTR path=nullptr;if(FAILED(SHGetKnownFolderPath(id,0,nullptr,&path)))return {};
    std::wstring result(path);CoTaskMemFree(path);return result;
}
std::wstring ModulePath(HMODULE module) {
    std::vector<wchar_t> path(32768);DWORD length=GetModuleFileNameW(module,path.data(),static_cast<DWORD>(path.size()));
    return length && length<path.size()?std::wstring(path.data(),length):std::wstring();
}
bool SamePath(const std::wstring& a,const std::wstring& b){return _wcsicmp(a.c_str(),b.c_str())==0;}

class SdkModule {
    uint32_t family;
    std::mutex mutex;
    HMODULE loaded=nullptr, dependency=nullptr;
    std::wstring configured, loadedPath;
    std::unordered_map<std::string,FARPROC> exports;
#ifdef HVX_FAKE_TEST
    HvxTestResolver resolver=nullptr;
#endif
    int Load(const char* operation) {
#ifdef HVX_FAKE_TEST
        (void)operation;return resolver?0:HVX_SDK_MISSING;
#else
        if(loaded)return 0;
        std::vector<std::wstring> paths;
        if(!configured.empty())paths.push_back(configured);
        else {
            auto pf=KnownFolder(family==ART?FOLDERID_ProgramFilesX86:FOLDERID_ProgramFiles);
            if(!pf.empty()) {
                if(family==ART)paths.push_back(pf+L"\\ART Technology\\ART-DAQ\\Lib\\x64\\Art_DAQ.dll");
                else {
                    paths.push_back(pf+L"\\Pico Technology\\SDK\\lib\\ps5000a.dll");
                    paths.push_back(pf+L"\\Pico Technology\\PicoScope 7 T&M Stable\\ps5000a.dll");
                }
            }
            wchar_t system[MAX_PATH+1]{};UINT length=GetSystemDirectoryW(system,MAX_PATH);
            if(length && length<MAX_PATH)paths.push_back(std::wstring(system)+(family==ART?L"\\Art_DAQ.dll":L"\\ps5000a.dll"));
        }
        for(const auto& path:paths) {
            DWORD attributes=GetFileAttributesW(path.c_str());if(attributes==INVALID_FILE_ATTRIBUTES)continue;
            if(!IsX64Dll(path))return HVX_PATH_INVALID;
            const wchar_t* basename=family==ART?L"Art_DAQ.dll":L"ps5000a.dll";
            if(HMODULE existing=GetModuleHandleW(basename))if(!SamePath(ModulePath(existing),path))return HVX_PATH_CONFLICT;
            if(family==PICO) {
                auto dependencyPath=(std::filesystem::path(path).parent_path()/L"picoipp.dll").wstring();
                if(!IsX64Dll(dependencyPath))return HVX_SDK_MISSING;
                if(HMODULE existing=GetModuleHandleW(L"picoipp.dll"))if(!SamePath(ModulePath(existing),dependencyPath))return HVX_PATH_CONFLICT;
                if(!dependency)dependency=LoadLibraryExW(dependencyPath.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
                if(!dependency)return HVX_SDK_MISSING;
            }
            loaded=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
            if(!loaded)return HVX_SDK_MISSING;
            loadedPath=path;return 0;
        }
        (void)operation;return HVX_SDK_MISSING;
#endif
    }
public:
    explicit SdkModule(uint32_t f):family(f){}
    int Configure(const wchar_t* path) {
        std::lock_guard<std::mutex> lock(mutex);
        auto full=FullPath(path);
        const wchar_t* basename=family==ART?L"Art_DAQ.dll":L"ps5000a.dll";
        if(full.empty() || _wcsicmp(std::filesystem::path(full).filename().c_str(),basename) || !IsX64Dll(full))return HVX_PATH_INVALID;
        if((!configured.empty() && !SamePath(configured,full)) || (!loadedPath.empty() && !SamePath(loadedPath,full)))return HVX_PATH_CONFLICT;
        if(HMODULE existing=GetModuleHandleW(basename))if(!SamePath(ModulePath(existing),full))return HVX_PATH_CONFLICT;
        if(family==PICO) {
            auto dep=(std::filesystem::path(full).parent_path()/L"picoipp.dll").wstring();
            if(!IsX64Dll(dep))return HVX_SDK_MISSING;
            if(HMODULE existing=GetModuleHandleW(L"picoipp.dll"))if(!SamePath(ModulePath(existing),dep))return HVX_PATH_CONFLICT;
        }
        configured=full;return 0; // Path validation only. Never LoadLibrary here.
    }
    FARPROC Resolve(const char* name) {
        std::lock_guard<std::mutex> lock(mutex);
        auto existing=exports.find(name);if(existing!=exports.end())return existing->second;
        int error=Load(name);if(error){Failure(family,error,name,GetLastError());return nullptr;}
        FARPROC entry=nullptr;
#ifdef HVX_FAKE_TEST
        entry=reinterpret_cast<FARPROC>(resolver(family,name));
#else
        entry=GetProcAddress(loaded,name);
#endif
        if(!entry){Failure(family,HVX_EXPORT_MISSING,name,GetLastError());return nullptr;}
        exports.emplace(name,entry);return entry;
    }
#ifdef HVX_FAKE_TEST
    void Install(HvxTestResolver value){std::lock_guard<std::mutex> lock(mutex);resolver=value;exports.clear();}
#endif
};
SdkModule& Module(uint32_t family){static auto* art=new SdkModule(ART);static auto* pico=new SdkModule(PICO);return family==ART?*art:*pico;}
template<typename Fn,typename... Args>
auto RawCall(Fn function,bool* fault,uint32_t* code,Args... args)->decltype(function(args...)) {
    __try {return function(args...);} __except(EXCEPTION_EXECUTE_HANDLER) {
        *fault=true;*code=GetExceptionCode();return 0;
    }
}
void RawApply(void(HVX_CALL* apply)(int32_t,int16_t),bool* fault,uint32_t* code) noexcept {
    __try {apply(485331304,4582);} __except(EXCEPTION_EXECUTE_HANDLER){*fault=true;*code=GetExceptionCode();}
}
template<typename Fn,typename... Args>
uint32_t Invoke(uint32_t family,const char* name,Args... args) {
    auto function=reinterpret_cast<Fn>(Module(family).Resolve(name));
    if(!function)return family==ART?static_cast<uint32_t>(last.bridge):HVX_PICO_ERROR(-last.bridge-4850000);
    bool fault=false;uint32_t code=0;auto result=RawCall(function,&fault,&code,args...);
    if(fault)return Failure(family,HVX_NATIVE_EXCEPTION,name,code);
    Vendor(family,name,static_cast<uint32_t>(result));return static_cast<uint32_t>(result);
}
template<typename Fn,typename... Args>
uint32_t GlobalCall(uint32_t family,const char* name,Args... args) {
    Lease lease(family);if(lease.error)return Failure(family,lease.error,name);
    uint32_t status=Invoke<Fn>(family,name,args...);return lease.Finish(status,name);
}
template<typename Fn,typename... Args>
int32_t ArtCall(void* task,const char* name,Args... args) {
    auto resource=Find(artTasks,reinterpret_cast<uintptr_t>(task));
    if(!resource)return static_cast<int32_t>(Failure(ART,HVX_HANDLE_UNKNOWN,name));
    std::unique_lock<std::recursive_mutex> lock(resource->operation,std::try_to_lock);
    if(!lock.owns_lock())return static_cast<int32_t>(Failure(ART,HVX_HANDLE_BUSY,name));
    if(resource->released)return static_cast<int32_t>(Failure(ART,HVX_HANDLE_UNKNOWN,name));
    if(!resource->usable)return static_cast<int32_t>(Failure(ART,HVX_HANDLE_QUARANTINED,name));
    return static_cast<int32_t>(Invoke<Fn>(ART,name,args...));
}
template<typename Fn,typename... Args>
uint32_t PicoCall(int16_t unit,const char* name,Args... args) {
    auto resource=Find(picoUnits,unit);
    if(!resource)return Failure(PICO,HVX_HANDLE_UNKNOWN,name);
    std::unique_lock<std::recursive_mutex> lock(resource->operation,std::try_to_lock);
    if(!lock.owns_lock())return Failure(PICO,HVX_HANDLE_BUSY,name);
    if(resource->released)return Failure(PICO,HVX_HANDLE_UNKNOWN,name);
    if(!resource->usable)return Failure(PICO,HVX_HANDLE_QUARANTINED,name);
    return Invoke<Fn>(PICO,name,args...);
}
}

extern "C" {
int32_t HVX_CALL HvxGetLastErrorInfo(char* buffer,uint32_t capacity){return CopyText(last.text,buffer,capacity);}
int32_t HVX_CALL HvxGetLastStatus(int32_t* bridge,uint32_t* vendor,uint32_t* win32) {
    if(!bridge || !vendor || !win32)return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);
    *bridge=last.bridge;*vendor=last.vendor;*win32=last.win32;return 0;
}
int32_t HVX_CALL HvxDescribeStatus(uint32_t family,uint32_t status,char* buffer,uint32_t capacity) {
    if(family!=ART && family!=PICO)return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);
    char text[1024]{};
    int64_t code=family==ART?-static_cast<int64_t>(static_cast<int32_t>(status))-4850000:
        static_cast<int64_t>(status)-0xE4850000LL;
    if(code>=1 && code<=HVX_LEASE_RELEASE_PENDING)
        std::snprintf(text,sizeof(text),"Private native bridge status: %s",Reason(static_cast<int>(code)));
    else std::snprintf(text,sizeof(text),"Raw %s vendor status 0x%08X; consult the matching official SDK documentation",family==ART?"ART":"Pico",status);
    return CopyText(text,buffer,capacity);
}
int32_t HVX_CALL HvxGetOwnershipCount(uint32_t family,uint32_t* count) {
    if(!count || (family!=ART && family!=PICO))return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);
    std::lock_guard<std::mutex> lock(registryMutex);*count=static_cast<uint32_t>(family==ART?artTasks.size():picoUnits.size());return 0;
}
int32_t HVX_CALL HvxConfigurePicoPathW(const wchar_t* path) {
    try {int code=Module(PICO).Configure(path);if(code){Failure(PICO,code,"HvxConfigurePicoPathW",GetLastError());return last.bridge;}
        Vendor(PICO,"HvxConfigurePicoPathW",0);return 0;
    }catch(...){Failure(PICO,HVX_INTERNAL_ERROR,"HvxConfigurePicoPathW");return last.bridge;}
}
int32_t HVX_CALL HvxConfigureArtPathW(const wchar_t* path) {
    try {int code=Module(ART).Configure(path);if(code){Failure(ART,code,"HvxConfigureArtPathW",GetLastError());return last.bridge;}
        Vendor(ART,"HvxConfigureArtPathW",0);return 0;
    }catch(...){Failure(ART,HVX_INTERNAL_ERROR,"HvxConfigureArtPathW");return last.bridge;}
}
int32_t HVX_CALL HvxSuppressPicoStartupWindow() {
    try {
        Lease lease(PICO);if(lease.error){Failure(PICO,lease.error,"HvxSuppressPicoStartupWindow");return last.bridge;}
        using Apply=void(HVX_CALL*)(int32_t,int16_t);
        auto apply=reinterpret_cast<Apply>(Module(PICO).Resolve("ps5000aApplyFix"));
        if(!apply){lease.Finish(HVX_PICO_ERROR(-last.bridge-4850000),"HvxSuppressPicoStartupWindow");return last.bridge;}
        // Official application's legacy fix is void. Never interpret an
        // undocumented register value as a Pico status or erase earlier faults.
        bool fault=false;uint32_t code=0;
        RawApply(apply,&fault,&code);
        if(fault){uint32_t status=Failure(PICO,HVX_NATIVE_EXCEPTION,"HvxSuppressPicoStartupWindow",code);lease.Finish(status,"HvxSuppressPicoStartupWindow");return last.bridge;}
        Vendor(PICO,"HvxSuppressPicoStartupWindow",0);lease.Finish(0,"HvxSuppressPicoStartupWindow");return last.bridge;
    }catch(...){Failure(PICO,HVX_INTERNAL_ERROR,"HvxSuppressPicoStartupWindow");return last.bridge;}
}
int32_t HVX_CALL ArtDAQ_CreateTask(const char* name,void** task) {
    try {
        if(!task)return static_cast<int32_t>(Failure(ART,HVX_INVALID_ARGUMENT,"ArtDAQ_CreateTask"));
        std::unique_lock<std::mutex> lifecycle(lifecycleMutex[0],std::try_to_lock);
        if(!lifecycle.owns_lock())return static_cast<int32_t>(Failure(ART,HVX_HANDLE_BUSY,"ArtDAQ_CreateTask lifecycle"));
        Lease lease(ART);if(lease.error)return static_cast<int32_t>(Failure(ART,lease.error,"ArtDAQ_CreateTask"));
        // Preallocate ownership storage before entering the SDK.
        auto resource=std::make_shared<Resource>();*task=nullptr;
        auto status=static_cast<int32_t>(Invoke<decltype(&ArtDAQ_CreateTask)>(ART,"ArtDAQ_CreateTask",name,task));
        if(*task) {
            resource->usable=status>=0 && !last.bridge;
            lease.Transfer(); // A returned resource must retain its lease even if registry allocation fails.
            std::lock_guard<std::mutex> lock(registryMutex);
            if(!artTasks.emplace(reinterpret_cast<uintptr_t>(*task),resource).second)return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,"ArtDAQ_CreateTask duplicate handle"));
        } else if(status>=0 && !last.bridge)status=static_cast<int32_t>(Failure(ART,HVX_HANDLE_UNKNOWN,"ArtDAQ_CreateTask returned null handle"));
        return static_cast<int32_t>(lease.Finish(static_cast<uint32_t>(status),"ArtDAQ_CreateTask"));
    }catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,"ArtDAQ_CreateTask"));}
}
int32_t HVX_CALL ArtDAQ_ClearTask(void* task) {
    try {
        std::unique_lock<std::mutex> lifecycle(lifecycleMutex[0],std::try_to_lock);
        if(!lifecycle.owns_lock())return static_cast<int32_t>(Failure(ART,HVX_HANDLE_BUSY,"ArtDAQ_ClearTask lifecycle"));
        auto resource=Find(artTasks,reinterpret_cast<uintptr_t>(task));if(!resource)return static_cast<int32_t>(Failure(ART,HVX_HANDLE_UNKNOWN,"ArtDAQ_ClearTask"));
        std::unique_lock<std::recursive_mutex> lock(resource->operation,std::try_to_lock);
        if(!lock.owns_lock())return static_cast<int32_t>(Failure(ART,HVX_HANDLE_BUSY,"ArtDAQ_ClearTask"));
        if(resource->finalized)return static_cast<int32_t>(Failure(ART,HVX_HANDLE_UNKNOWN,"ArtDAQ_ClearTask"));
        if(!resource->released) {
            CleanupGuard guard(ART);
            int32_t status=static_cast<int32_t>(Invoke<decltype(&ArtDAQ_ClearTask)>(ART,"ArtDAQ_ClearTask",task));
            if(status<0 || last.bridge)return status;
            resource->released=true;resource->cleanupVendor=static_cast<uint32_t>(status);guard.Transfer();
        }
        int32_t status=static_cast<int32_t>(ConfirmCleanup(ART,*resource,"ArtDAQ_ClearTask"));
        if(!last.bridge) {
            resource->finalized=true;
            {std::lock_guard<std::mutex> registryLock(registryMutex);artTasks.erase(reinterpret_cast<uintptr_t>(task));}
            Leases(ART).EndCleanup();
        }
        return status;
    }catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,"ArtDAQ_ClearTask"));}
}
int32_t HVX_CALL ArtDAQ_GetExtendedErrorInfo(char* error,uint32_t capacity) {
    try {
        Diagnostic original=last;
        if(original.bridge){CopyText(original.text,error,capacity);return original.bridge;}
        auto status=static_cast<int32_t>(GlobalCall<decltype(&ArtDAQ_GetExtendedErrorInfo)>(ART,"ArtDAQ_GetExtendedErrorInfo",error,capacity));
        if(!last.bridge)last=original;return status;
    }catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,"ArtDAQ_GetExtendedErrorInfo"));}
}
uint32_t HVX_CALL ps5000aOpenUnit(int16_t* unit,const char* serial,int32_t resolution) {
    try {
        if(!unit)return Failure(PICO,HVX_INVALID_ARGUMENT,"ps5000aOpenUnit");
        std::unique_lock<std::mutex> lifecycle(lifecycleMutex[1],std::try_to_lock);
        if(!lifecycle.owns_lock())return Failure(PICO,HVX_HANDLE_BUSY,"ps5000aOpenUnit lifecycle");
        Lease lease(PICO);if(lease.error)return Failure(PICO,lease.error,"ps5000aOpenUnit");
        auto resource=std::make_shared<Resource>();*unit=0;
        uint32_t status=Invoke<decltype(&ps5000aOpenUnit)>(PICO,"ps5000aOpenUnit",unit,serial,resolution);
        // 0x119/0x11A/0x11E are documented power/USB warnings with a live handle.
        // The official OpenUnit contract uses -1 for failed open, 0 for not found,
        // and only positive values as device handles. Retain positive unexpected
        // failure handles for explicit cleanup, but never own a failure sentinel.
        if(*unit>0) {
            resource->usable=!last.bridge && (status==0 || status==0x119u || status==0x11Au || status==0x11Eu);
            lease.Transfer();
            std::lock_guard<std::mutex> lock(registryMutex);
            if(!picoUnits.emplace(*unit,resource).second)return Failure(PICO,HVX_INTERNAL_ERROR,"ps5000aOpenUnit duplicate handle");
        }else if(*unit<=0 && status==0 && !last.bridge)status=Failure(PICO,HVX_HANDLE_UNKNOWN,"ps5000aOpenUnit returned invalid handle");
        return lease.Finish(status,"ps5000aOpenUnit");
    }catch(...){return Failure(PICO,HVX_INTERNAL_ERROR,"ps5000aOpenUnit");}
}
uint32_t HVX_CALL ps5000aCloseUnit(int16_t unit) {
    try {
        std::unique_lock<std::mutex> lifecycle(lifecycleMutex[1],std::try_to_lock);
        if(!lifecycle.owns_lock())return Failure(PICO,HVX_HANDLE_BUSY,"ps5000aCloseUnit lifecycle");
        auto resource=Find(picoUnits,unit);if(!resource)return Failure(PICO,HVX_HANDLE_UNKNOWN,"ps5000aCloseUnit");
        std::unique_lock<std::recursive_mutex> lock(resource->operation,std::try_to_lock);
        if(!lock.owns_lock())return Failure(PICO,HVX_HANDLE_BUSY,"ps5000aCloseUnit");
        if(resource->finalized)return Failure(PICO,HVX_HANDLE_UNKNOWN,"ps5000aCloseUnit");
        if(!resource->released) {
            CleanupGuard guard(PICO);
            uint32_t status=Invoke<decltype(&ps5000aCloseUnit)>(PICO,"ps5000aCloseUnit",unit);
            if(status!=0 || last.bridge)return status;
            resource->released=true;resource->cleanupVendor=status;guard.Transfer();
        }
        uint32_t status=ConfirmCleanup(PICO,*resource,"ps5000aCloseUnit");
        if(!last.bridge) {
            resource->finalized=true;
            {std::lock_guard<std::mutex> registryLock(registryMutex);picoUnits.erase(unit);}
            Leases(PICO).EndCleanup();
        }
        return status;
    }catch(...){return Failure(PICO,HVX_INTERNAL_ERROR,"ps5000aCloseUnit");}
}
#define HVX_EXPAND(...) __VA_ARGS__
#define HVX_ART_TASK(n,p,a) int32_t HVX_CALL n p {try{return ArtCall<decltype(&n)>(task,#n,HVX_EXPAND a);}catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,#n));}}
#define HVX_ART_GLOBAL(n,p,a) int32_t HVX_CALL n p {try{return static_cast<int32_t>(GlobalCall<decltype(&n)>(ART,#n,HVX_EXPAND a));}catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,#n));}}
#define HVX_ART_OPTIONAL(n,p,a) int32_t HVX_CALL n p {try{return task?ArtCall<decltype(&n)>(task,#n,HVX_EXPAND a):static_cast<int32_t>(GlobalCall<decltype(&n)>(ART,#n,HVX_EXPAND a));}catch(...){return static_cast<int32_t>(Failure(ART,HVX_INTERNAL_ERROR,#n));}}
#define HVX_PICO_UNIT(n,p,a) uint32_t HVX_CALL n p {try{return PicoCall<decltype(&n)>(unit,#n,HVX_EXPAND a);}catch(...){return Failure(PICO,HVX_INTERNAL_ERROR,#n);}}
#define HVX_PICO_GLOBAL(n,p,a) uint32_t HVX_CALL n p {try{return GlobalCall<decltype(&n)>(PICO,#n,HVX_EXPAND a);}catch(...){return Failure(PICO,HVX_INTERNAL_ERROR,#n);}}
#include "HvxSdkApis.inc"
#undef HVX_ART_TASK
#undef HVX_ART_GLOBAL
#undef HVX_ART_OPTIONAL
#undef HVX_PICO_UNIT
#undef HVX_PICO_GLOBAL
#ifdef HVX_FAKE_TEST
int32_t HVX_CALL HvxTestInstallResolver(HvxTestResolver resolver) {
    std::lock_guard<std::mutex> lock(registryMutex);
    if(!artTasks.empty() || !picoUnits.empty())return HVX_ART_ERROR(HVX_HANDLE_BUSY);
    Module(ART).Install(resolver);Module(PICO).Install(resolver);return 0;
}
int32_t HVX_CALL HvxTestSetLeaseFault(uint32_t family,int32_t mode) {
    if((family!=ART && family!=PICO) || mode<0 || mode>2)return HVX_ART_ERROR(HVX_INVALID_ARGUMENT);
    Leases(family).TestFault(mode);return 0;
}
#endif
}
