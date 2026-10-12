#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "../HvxAcquisitionNative.h"
#include <windows.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
namespace {
HMODULE fake=nullptr;
using Set=void(HVX_CALL*)(int32_t,int32_t);
using Get=int32_t(HVX_CALL*)(int32_t);
Set set;Get get;
int checks=0,failures=0;
std::atomic<bool> omitStop{false};
void Check(bool value,const char* name){++checks;std::printf("%s %s\n",value?"PASS":"FAIL",name);if(!value)++failures;}
void* HVX_CALL Resolve(uint32_t family,const char* name){(void)family;if(omitStop && !std::strcmp(name,"ArtDAQ_StopTask"))return nullptr;return reinterpret_cast<void*>(GetProcAddress(fake,name));}
std::wstring ExePath(){wchar_t path[32768]{};GetModuleFileNameW(nullptr,path,32768);return path;}
std::wstring Fixture(const wchar_t* relative){auto exe=ExePath();return exe.substr(0,exe.find_last_of(L"\\/"))+L"\\"+relative;}
bool Install(){auto exe=ExePath();auto path=exe.substr(0,exe.find_last_of(L"\\/"))+L"\\HvxFakeSdk.dll";
    fake=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!fake)return false;set=reinterpret_cast<Set>(GetProcAddress(fake,"FakeSet"));get=reinterpret_cast<Get>(GetProcAddress(fake,"FakeGet"));
    return set && get && HvxTestInstallResolver(Resolve)==0;
}
bool Child(const wchar_t* argument){auto exe=ExePath();auto command=L"\""+exe+L"\" "+argument;STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
    if(!CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi))return false;
    DWORD wait=WaitForSingleObject(pi.hProcess,10000),code=999;
    if(wait==WAIT_OBJECT_0)GetExitCodeProcess(pi.hProcess,&code);
    else TerminateProcess(pi.hProcess,999);
    CloseHandle(pi.hThread);CloseHandle(pi.hProcess);return code==0;
}
struct CallbackState {bool called=false;int16_t handle=0;uint32_t status=0;};
void HVX_CALL Ready(int16_t handle,uint32_t status,void* param){auto state=static_cast<CallbackState*>(param);state->called=true;state->handle=handle;state->status=status;}
}
int wmain(int argc,wchar_t** argv){
    if(argc>1){if(!Install())return 80;
        if(!std::wcscmp(argv[1],L"--global-release-fault")) {
            HvxTestSetLeaseFault(1,1);uint32_t attribute=0;
            int32_t status=ArtDAQ_GetSystemAttribute(1,&attribute,sizeof(attribute));
            int32_t bridge=0;uint32_t vendor=999,win32=0;HvxGetLastStatus(&bridge,&vendor,&win32);
            void* task=nullptr;int before=get(6);
            return status==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && bridge==status && vendor==0 &&
                ArtDAQ_CreateTask("blocked-global-owner",&task)==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) &&
                task==nullptr && get(6)==before?0:85;
        }
        if(!std::wcscmp(argv[1],L"--pico-conflict") || !std::wcscmp(argv[1],L"--pico-free")) {
            int16_t unit=0;uint32_t status=ps5000aOpenUnit(&unit,nullptr,0);
            bool conflict=!std::wcscmp(argv[1],L"--pico-conflict");
            if(conflict)return status==HVX_PICO_ERROR(HVX_OWNER_CONFLICT)&&unit==0?0:83;
            return status==0x11A && unit>0 && ps5000aCloseUnit(unit)==0?0:84;
        }
        void* task=nullptr;int32_t status=ArtDAQ_CreateTask("probe",&task);
        bool conflict=!std::wcscmp(argv[1],L"--lease-conflict");
        if(conflict)return status==HVX_ART_ERROR(HVX_OWNER_CONFLICT)&&task==nullptr?0:81;
        return status==0 && ArtDAQ_ClearTask(task)==0?0:82;
    }
    uint32_t attribute=0;
    Check(ArtDAQ_GetSystemAttribute(1,&attribute,sizeof(attribute))==HVX_ART_ERROR(HVX_SDK_MISSING),"fake build missing SDK fails closed without any real loader");
    Check(Install(),"install explicit independently-built fake SDK resolver");if(!fake)return 1;
    Check(HvxConfigurePicoPathW(L"ps5000a.dll")==HVX_ART_ERROR(HVX_PATH_INVALID),"reject relative configured Pico path");
    Check(HvxConfigureArtPathW(L"Art_DAQ.dll")==HVX_ART_ERROR(HVX_PATH_INVALID),"reject relative configured ART path");
    Check(HvxConfigurePicoPathW(Fixture(L"pico-missing-dependency\\ps5000a.dll").c_str())==HVX_ART_ERROR(HVX_SDK_MISSING),"Pico configured path rejects missing sibling dependency");
    Check(HvxConfigurePicoPathW(Fixture(L"pico-a\\ps5000a.dll").c_str())==0,"Pico absolute x64 fake PE path validated without loading");
    Check(HvxConfigurePicoPathW(Fixture(L"pico-a\\ps5000a.dll").c_str())==0,"same selected Pico path is idempotent");
    Check(HvxConfigurePicoPathW(Fixture(L"pico-b\\ps5000a.dll").c_str())==HVX_ART_ERROR(HVX_PATH_CONFLICT),"different Pico path cannot mix into selected version");
    Check(HvxConfigureArtPathW(Fixture(L"art-a\\Art_DAQ.dll").c_str())==0,"ART absolute x64 fake PE path validated without loading");
    Check(HvxConfigureArtPathW(Fixture(L"art-b\\Art_DAQ.dll").c_str())==HVX_ART_ERROR(HVX_PATH_CONFLICT),"different ART path cannot mix into selected version");
    Check(GetModuleHandleW(L"Art_DAQ.dll")==nullptr && GetModuleHandleW(L"ps5000a.dll")==nullptr && GetModuleHandleW(L"picoipp.dll")==nullptr,"configuration did not load any SDK-named module");
    void *ai=nullptr,*di=nullptr;
    Check(ArtDAQ_CreateTask("AI",&ai)==0 && ai!=nullptr,"ART create registers returned task handle");
    Check(ArtDAQ_CreateTask("DI",&di)==0 && di!=nullptr && di!=ai,"ART independent digital task handle");
    void* otherThreadTask=nullptr;int32_t otherThreadStatus=999;
    std::thread createOnWorker([&]{otherThreadStatus=ArtDAQ_CreateTask("worker-created",&otherThreadTask);});createOnWorker.join();
    Check(otherThreadStatus==0 && ArtDAQ_ClearTask(otherThreadTask)==0,"create on worker and Clear on caller preserve Win32 mutex thread affinity");
    uint32_t count=0;
    Check(HvxGetOwnershipCount(1,&count)==0 && count==2,"ART native ownership count");
    Check(Child(L"--lease-conflict"),"cross-process unique ART owner rejects second process");
    Check(ArtDAQ_StopTask(ai)==0 && HvxGetOwnershipCount(1,&count)==0 && count==2,"ART Stop retains task and owner lease");
    set(1,-50103);
    Check(ArtDAQ_ClearTask(ai)==-50103 && HvxGetOwnershipCount(1,&count)==0 && count==2,"failed Clear preserves original status and ownership");
    int32_t bridge=99;uint32_t vendor=0,win32=0;
    Check(HvxGetLastStatus(&bridge,&vendor,&win32)==0 && bridge==0 && static_cast<int32_t>(vendor)==-50103,"vendor signed status preserved without private bridge substitution");
    Check(Child(L"--lease-conflict"),"failed Clear does not release cross-process ownership");
    set(1,0);set(2,1);
    double samples[4]{};int32_t read=0;int32_t aiResult=999;
    std::thread blocked([&]{aiResult=ArtDAQ_ReadAnalogF64(ai,4,1.0,0,samples,4,&read,nullptr);});
    for(int i=0;i<1000 && !get(1);++i)Sleep(1);
    Check(get(1)==1,"fake AI read entered blocking section");
    auto began=std::chrono::steady_clock::now();uint8_t bits[1]{};int32_t digitalRead=0,bytes=0;
    int32_t diResult=ArtDAQ_ReadDigitalLines(di,1,0.01,0,bits,1,&digitalRead,&bytes,nullptr);
    auto elapsed=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-began).count();
    Check(diResult==0 && bits[0]==1 && elapsed<250,"DI runs independently while AI read holds its own handle lock");
    Check(ArtDAQ_StopTask(ai)==HVX_ART_ERROR(HVX_HANDLE_BUSY),"same-handle re-entry fails promptly rather than deadlocking");
    set(2,0);blocked.join();
    Check(aiResult==0 && read==4 && samples[0]==1.25,"AI original returned values unchanged");
    set(6,-200284);
    Check(ArtDAQ_StopTask(ai)==-200284,"official -200284 is not ignored or remapped to success");set(6,0);
    set(5,1);
    Check(ArtDAQ_StartTask(ai)==HVX_ART_ERROR(HVX_NATIVE_EXCEPTION),"native SDK exception becomes explicit private bridge failure");
    Check(HvxGetLastStatus(&bridge,&vendor,&win32)==0 && bridge==HVX_ART_ERROR(HVX_NATIVE_EXCEPTION) && win32==0xE1234567,"original native exception code retained");
    char text[1024]{};Check(HvxGetLastErrorInfo(text,sizeof(text))==0 && std::strstr(text,"ArtDAQ_StartTask"),"diagnostics retain operation name");
    Check(ArtDAQ_ClearTask(ai)==0 && ArtDAQ_ClearTask(di)==0,"successful ART cleanup removes handles");
    Check(ArtDAQ_StopTask(ai)==HVX_ART_ERROR(HVX_HANDLE_UNKNOWN),"released ART handle is rejected");
    Check(Child(L"--lease-free"),"cross-process owner lease released after last successful Clear");
    omitStop=true;Check(HvxTestInstallResolver(Resolve)==0,"reset fake export cache only with no live handles");
    Check(ArtDAQ_CreateTask("missing-entry",&ai)==0,"create task for unsupported-entry test");
    Check(ArtDAQ_StopTask(ai)==HVX_ART_ERROR(HVX_EXPORT_MISSING),"missing SDK entry returns explicit bridge error");
    Check(ArtDAQ_ClearTask(ai)==0,"missing Stop entry does not discard cleanup ownership");omitStop=false;
    Check(HvxTestInstallResolver(Resolve)==0,"restore fake complete export resolver");
    void* cleanupTask=nullptr;
    Check(ArtDAQ_CreateTask("lease-release-failure",&cleanupTask)==0,"ART create before injected owner release failure");
    int clearBefore=get(4),createBefore=get(6);
    Check(HvxTestSetLeaseFault(1,1)==0,"inject one owner ReleaseMutex failure in fake boundary only");
    Check(ArtDAQ_ClearTask(cleanupTask)==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && get(4)==clearBefore+1,
        "ART SDK Clear success plus lease release failure returns private cleanup error");
    Check(HvxGetLastStatus(&bridge,&vendor,&win32)==0 && bridge==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && vendor==0 && win32==ERROR_NOT_OWNER,
        "ART cleanup error retains successful SDK status and actual lease release error");
    Check(HvxGetLastErrorInfo(text,sizeof(text))==0 && std::strstr(text,"sdkCleanupCompleted=true"),"cleanup diagnostic distinguishes SDK completion from lease confirmation");
    Check(HvxGetOwnershipCount(1,&count)==0 && count==1 && Child(L"--lease-conflict"),"failed release retains ART tombstone and cross-process owner");
    Check(ArtDAQ_StartTask(cleanupTask)==HVX_ART_ERROR(HVX_HANDLE_UNKNOWN),"ordinary API rejects SDK-cleared tombstone");
    void* duplicate=nullptr;set(8,static_cast<int32_t>(reinterpret_cast<uintptr_t>(cleanupTask)-1));
    Check(ArtDAQ_CreateTask("would-reuse-handle",&duplicate)==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && duplicate==nullptr && get(6)==createBefore,
        "pending ART tombstone blocks Create before SDK can reuse its handle");
    Check(ArtDAQ_ClearTask(cleanupTask)==0 && get(4)==clearBefore+1,"explicit Clear retry confirms same release ticket without replaying SDK Clear");
    Check(HvxGetOwnershipCount(1,&count)==0 && count==0 && Child(L"--lease-free"),"confirmed ART release removes tombstone and releases owner");
    Check(ArtDAQ_CreateTask("safe-handle-reuse",&duplicate)==0 && duplicate==cleanupTask,"SDK may reuse handle only after previous cleanup receipt");
    Check(ArtDAQ_ClearTask(duplicate)==0,"reused ART handle has its independent cleanup receipt");
    void *pendingTask=nullptr,*remainingTask=nullptr;
    Check(ArtDAQ_CreateTask("delayed-release",&pendingTask)==0 && ArtDAQ_CreateTask("remaining-owner",&remainingTask)==0,"two ART tasks prepare delayed single-reference release");
    clearBefore=get(4);HvxTestSetLeaseFault(1,2);
    Check(ArtDAQ_ClearTask(pendingTask)==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && get(4)==clearBefore+1,
        "release service timeout does not falsely acknowledge SDK cleanup ownership");
    Check(HvxGetLastErrorInfo(text,sizeof(text))==0 && std::strstr(text,"leaseStatus=-4850011"),"pending release retains bounded timeout diagnostic");
    int32_t confirmed=999;
    std::thread confirmPending([&]{confirmed=ArtDAQ_ClearTask(pendingTask);});Sleep(30);
    Check(get(4)==clearBefore+1,"waiting on same pending release ticket never calls SDK Clear again");
    HvxTestSetLeaseFault(1,0);confirmPending.join();
    Check(confirmed==0 && HvxGetOwnershipCount(1,&count)==0 && count==1,"late release receipt settles exactly the pending task");
    Check(Child(L"--lease-conflict"),"single pending ticket cannot decrement remaining live task owner reference twice");
    Check(ArtDAQ_ClearTask(remainingTask)==0 && Child(L"--lease-free"),"last independent live task retains then releases its own reference");
    Check(Child(L"--global-release-fault"),"global API release failure is returned and blocks new SDK ownership without silently succeeding");
    int16_t unit=0;set(3,0x11A);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==0x11A && unit==42,"Pico power warning and valid handle preserved");
    Check(HvxGetLastStatus(&bridge,&vendor,&win32)==0 && bridge==0 && vendor==0x11A,"Pico warning retains unsigned original status");
    Check(ps5000aChangePowerSource(unit,0x11A)==0,"power warning handle permits official ChangePowerSource continuation");
    Check(ps5000aStop(unit)==0 && HvxGetOwnershipCount(2,&count)==0 && count==1,"Pico Stop retains opened unit");
    Check(Child(L"--pico-conflict"),"power-warning handle retains cross-process Pico owner lease after Stop");
    CallbackState callback;
    Check(ps5000aRunBlock(unit,2,2,1,nullptr,0,Ready,&callback)==0 && callback.called && callback.handle==unit && callback.status==0x11E,"Pico callback and user parameter retain original ABI");
    set(4,0x105);
    Check(ps5000aCloseUnit(unit)==0x105 && HvxGetOwnershipCount(2,&count)==0 && count==1,"Pico failed Close retains handle and original uint status");
    Check(Child(L"--pico-conflict"),"Pico failed Close does not release cross-process lease");
    set(4,0);Check(ps5000aCloseUnit(unit)==0 && HvxGetOwnershipCount(2,&count)==0 && count==0,"Pico successful Close releases ownership");
    Check(Child(L"--pico-free"),"Pico lease releases after successful Close");
    Check(ps5000aStop(unit)==HVX_PICO_ERROR(HVX_HANDLE_UNKNOWN),"Pico unknown handle uses unsigned private bridge status");
    Check(ps5000aOpenUnit(&unit,nullptr,0)==0x11A,"Pico open before injected lease release failure");
    int closeBefore=get(5),openBefore=get(7);HvxTestSetLeaseFault(2,1);
    Check(ps5000aCloseUnit(unit)==HVX_PICO_ERROR(HVX_LEASE_RELEASE_PENDING) && get(5)==closeBefore+1,"Pico SDK Close success plus owner failure returns private uint32 cleanup error");
    Check(HvxGetLastStatus(&bridge,&vendor,&win32)==0 && bridge==HVX_ART_ERROR(HVX_LEASE_RELEASE_PENDING) && vendor==0 && win32==ERROR_NOT_OWNER,
        "Pico cleanup retains raw SDK status and lease error separately");
    int16_t reusedUnit=0;
    Check(ps5000aOpenUnit(&reusedUnit,nullptr,0)==HVX_PICO_ERROR(HVX_LEASE_RELEASE_PENDING) && get(7)==openBefore && reusedUnit==0,
        "pending Pico tombstone blocks Open before SDK can reuse its handle");
    Check(ps5000aStop(unit)==HVX_PICO_ERROR(HVX_HANDLE_UNKNOWN) && Child(L"--pico-conflict"),"Pico tombstone is unusable but retains actual owner");
    Check(ps5000aCloseUnit(unit)==0 && get(5)==closeBefore+1,"explicit Pico Close retries only the same lease ticket, never SDK Close");
    Check(HvxGetOwnershipCount(2,&count)==0 && count==0 && Child(L"--pico-free"),"Pico owner released only after actual lease acknowledgement");
    set(3,0x105);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==0x105 && unit==42,"unexpected Pico failure plus nonzero handle retained for cleanup");
    Check(ps5000aChangePowerSource(unit,0x11A)==HVX_PICO_ERROR(HVX_HANDLE_QUARANTINED),"failed-open handle is quarantined from normal device calls");
    Check(ps5000aCloseUnit(unit)==0,"quarantined Pico handle permits only explicit Close");
    set(7,-1);set(3,0x105);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==0x105 && unit==-1,"Pico failed-open -1 sentinel preserves original vendor failure");
    Check(HvxGetOwnershipCount(2,&count)==0 && count==0,"Pico -1 failure sentinel is not registered as a unit");
    Check(Child(L"--pico-free"),"Pico -1 failure sentinel leaves no cross-process owner lease");
    Check(ps5000aStop(unit)==HVX_PICO_ERROR(HVX_HANDLE_UNKNOWN),"Pico -1 sentinel cannot perform normal device operations");
    set(3,0);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==HVX_PICO_ERROR(HVX_HANDLE_UNKNOWN) && unit==-1,"invalid success plus -1 sentinel fails closed");
    Check(HvxGetOwnershipCount(2,&count)==0 && count==0 && Child(L"--pico-free"),"invalid success plus -1 owns no unit or lease");
    set(7,0);set(3,3);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==3 && unit==0,"Pico no-scope zero sentinel preserves official NOT_FOUND status");
    Check(HvxGetOwnershipCount(2,&count)==0 && count==0 && Child(L"--pico-free"),"Pico zero sentinel owns no unit or lease");
    set(3,0);
    Check(ps5000aOpenUnit(&unit,nullptr,0)==HVX_PICO_ERROR(HVX_HANDLE_UNKNOWN) && unit==0,"invalid success plus zero handle fails closed");
    Check(HvxGetOwnershipCount(2,&count)==0 && count==0,"invalid success plus zero handle owns no unit");
    set(7,42);set(3,0x11A);
    Check(HvxSuppressPicoStartupWindow()==0 && get(2)==485331304 && get(3)==4582,"void ApplyFix exact arguments; no invented status");
    Check(HvxDescribeStatus(2,HVX_PICO_ERROR(HVX_SDK_MISSING),text,sizeof(text))==0 && std::strstr(text,"Private"),"Pico private error remains distinguishable from official status");
    std::printf("SUMMARY checks=%d failures=%d; REAL_SDK_LOADED=NO (compile-time fake loader only)\n",checks,failures);
    return failures?1:0;
}
