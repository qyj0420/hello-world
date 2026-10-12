// No vendor headers, libraries, drivers or hardware are used by this fake.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <stdint.h>
#include <atomic>
#include <cstring>
#define EXPORT extern "C" __declspec(dllexport)
#define HVX_CALL __stdcall
typedef void(HVX_CALL* HvxBlockReady)(int16_t,uint32_t,void*);
#pragma warning(disable:4100)
namespace {
std::atomic<uintptr_t> nextTask{4096};
std::atomic<int32_t> clearStatus{0},stopStatus{0};
std::atomic<uint32_t> openStatus{0x11A},closeStatus{0};
std::atomic<int16_t> openHandle{42};
std::atomic<bool> readBlocked{false},readEntered{false},startFault{false};
std::atomic<int32_t> fixNumber{0},fixValue{0};
std::atomic<int32_t> createCalls{0},clearCalls{0},openCalls{0},closeCalls{0};
}
EXPORT void HVX_CALL FakeSet(int32_t key,int32_t value) {
    switch(key){
    case 1:clearStatus=value;break;case 2:readBlocked=value!=0;break;
    case 3:openStatus=static_cast<uint32_t>(value);break;case 4:closeStatus=static_cast<uint32_t>(value);break;
    case 5:startFault=value!=0;break;case 6:stopStatus=value;break;
    case 7:openHandle=static_cast<int16_t>(value);break;
    case 8:nextTask=static_cast<uintptr_t>(value);break;
    }
}
EXPORT int32_t HVX_CALL FakeGet(int32_t key) {
    switch(key){case 1:return readEntered?1:0;case 2:return fixNumber;case 3:return fixValue;
    case 4:return clearCalls;case 5:return closeCalls;case 6:return createCalls;case 7:return openCalls;default:return 0;}
}
EXPORT int32_t HVX_CALL ArtDAQ_CreateTask(const char* name,void** task){++createCalls;*task=reinterpret_cast<void*>(++nextTask);return 0;}
EXPORT int32_t HVX_CALL ArtDAQ_ClearTask(void* task){++clearCalls;return clearStatus;}
EXPORT int32_t HVX_CALL ArtDAQ_StartTask(void* task){if(startFault.exchange(false))RaiseException(0xE1234567,0,0,nullptr);return 0;}
EXPORT int32_t HVX_CALL ArtDAQ_StopTask(void* task){return stopStatus;}
EXPORT int32_t HVX_CALL ArtDAQ_GetExtendedErrorInfo(char* error,uint32_t capacity){const char* text="fake vendor error";if(capacity>std::strlen(text))std::memcpy(error,text,std::strlen(text)+1);return 0;}
EXPORT int32_t HVX_CALL ArtDAQ_ReadAnalogF64(void* task,int32_t samples,double timeout,uint32_t layout,double* data,uint32_t length,int32_t* read,uint32_t* reserved){
    readEntered=true;DWORD began=GetTickCount();
    while(readBlocked && GetTickCount()-began<5000)Sleep(1);
    if(read)*read=static_cast<int32_t>(length);if(data && length)data[0]=1.25;return 0;
}
EXPORT int32_t HVX_CALL ArtDAQ_ReadDigitalLines(void* task,int32_t samples,double timeout,uint32_t layout,uint8_t* data,uint32_t length,int32_t* read,int32_t* bytes,uint32_t* reserved){
    if(data && length)data[0]=1;if(read)*read=1;if(bytes)*bytes=1;return 0;
}
EXPORT uint32_t HVX_CALL ps5000aOpenUnit(int16_t* unit,const char* serial,int32_t resolution){++openCalls;*unit=openHandle;return openStatus;}
EXPORT uint32_t HVX_CALL ps5000aCloseUnit(int16_t unit){++closeCalls;return closeStatus;}
EXPORT uint32_t HVX_CALL ps5000aRunBlock(int16_t unit,int32_t pre,int32_t post,uint32_t timebase,int32_t* indisposed,uint32_t segment,HvxBlockReady callback,void* param){if(callback)callback(unit,0x11E,param);return 0;}
EXPORT void HVX_CALL ps5000aApplyFix(int32_t number,int16_t value){fixNumber=number;fixValue=value;}
// Remaining APIs deliberately use the same explicit x64 ABI as the bridge.
#define HVX_ART_TASK(n,p,a) EXPORT int32_t HVX_CALL n p {return 0;}
#define HVX_ART_GLOBAL(n,p,a) HVX_ART_TASK(n,p,a)
#define HVX_ART_OPTIONAL(n,p,a) HVX_ART_TASK(n,p,a)
#define HVX_PICO_UNIT(n,p,a) EXPORT uint32_t HVX_CALL n p {return 0;}
#define HVX_PICO_GLOBAL(n,p,a) HVX_PICO_UNIT(n,p,a)
#define ArtDAQ_StartTask FakeUnused_StartTask
#define ArtDAQ_StopTask FakeUnused_StopTask
#define ArtDAQ_ReadAnalogF64 FakeUnused_ReadAnalogF64
#define ArtDAQ_ReadDigitalLines FakeUnused_ReadDigitalLines
#define ps5000aRunBlock FakeUnused_RunBlock
#include "../HvxSdkApis.inc"
