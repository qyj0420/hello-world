#pragma once
#include <stdint.h>
#ifdef HVX_NATIVE_BUILD
#define HVX_API __declspec(dllexport)
#else
#define HVX_API __declspec(dllimport)
#endif
#define HVX_CALL __stdcall
#ifdef __cplusplus
extern "C" {
#endif
// Bridge failures are private application codes, never vendor status aliases.
enum HvxBridgeError {
    HVX_OK=0, HVX_SDK_MISSING=1, HVX_EXPORT_MISSING=2, HVX_OWNER_CONFLICT=3,
    HVX_HANDLE_UNKNOWN=4, HVX_HANDLE_BUSY=5, HVX_PATH_INVALID=6,
    HVX_PATH_CONFLICT=7, HVX_NATIVE_EXCEPTION=8, HVX_INTERNAL_ERROR=9,
    HVX_INVALID_ARGUMENT=10, HVX_LEASE_TIMEOUT=11, HVX_HANDLE_QUARANTINED=12,
    HVX_LEASE_RELEASE_PENDING=13
};
#define HVX_ART_ERROR(code) (-4850000 - (int32_t)(code))
#define HVX_PICO_ERROR(code) (0xE4850000u + (uint32_t)(code))
typedef void (HVX_CALL *HvxBlockReady)(int16_t, uint32_t, void*);
HVX_API int32_t HVX_CALL HvxConfigurePicoPathW(const wchar_t* absoluteDriverPath);
HVX_API int32_t HVX_CALL HvxConfigureArtPathW(const wchar_t* absoluteDriverPath);
HVX_API int32_t HVX_CALL HvxSuppressPicoStartupWindow(void);
HVX_API int32_t HVX_CALL HvxGetLastErrorInfo(char* utf8Buffer, uint32_t capacity);
HVX_API int32_t HVX_CALL HvxGetLastStatus(int32_t* bridgeCode, uint32_t* vendorStatus, uint32_t* win32Error);
HVX_API int32_t HVX_CALL HvxDescribeStatus(uint32_t family, uint32_t rawStatus, char* utf8Buffer, uint32_t capacity);
HVX_API int32_t HVX_CALL HvxGetOwnershipCount(uint32_t family, uint32_t* count);
HVX_API int32_t HVX_CALL ArtDAQ_CreateTask(const char*, void**);
HVX_API int32_t HVX_CALL ArtDAQ_ClearTask(void*);
HVX_API int32_t HVX_CALL ArtDAQ_GetExtendedErrorInfo(char*, uint32_t);
HVX_API uint32_t HVX_CALL ps5000aOpenUnit(int16_t*, const char*, int32_t);
HVX_API uint32_t HVX_CALL ps5000aCloseUnit(int16_t);
#define HVX_ART_TASK(n,p,a) HVX_API int32_t HVX_CALL n p;
#define HVX_ART_GLOBAL(n,p,a) HVX_ART_TASK(n,p,a)
#define HVX_ART_OPTIONAL(n,p,a) HVX_ART_TASK(n,p,a)
#define HVX_PICO_UNIT(n,p,a) HVX_API uint32_t HVX_CALL n p;
#define HVX_PICO_GLOBAL(n,p,a) HVX_PICO_UNIT(n,p,a)
#include "HvxSdkApis.inc"
#undef HVX_ART_TASK
#undef HVX_ART_GLOBAL
#undef HVX_ART_OPTIONAL
#undef HVX_PICO_UNIT
#undef HVX_PICO_GLOBAL
#ifdef HVX_FAKE_TEST
// Deliberately absent from production DLL. Test variant cannot load real SDKs.
typedef void* (HVX_CALL *HvxTestResolver)(uint32_t, const char*);
HVX_API int32_t HVX_CALL HvxTestInstallResolver(HvxTestResolver resolver);
HVX_API int32_t HVX_CALL HvxTestSetLeaseFault(uint32_t family, int32_t mode);
#endif
#ifdef __cplusplus
}
#endif
