#pragma once
#include <stdint.h>

#if defined(_WIN32)
# if defined(HVX_HYBRID_CORE_EXPORTS)
#  define HX_API __declspec(dllexport)
# else
#  define HX_API __declspec(dllimport)
# endif
# define HX_CALL __cdecl
#else
# define HX_API
# define HX_CALL
#endif
#if defined(__cplusplus)
# define HX_NOEXCEPT noexcept
extern "C" {
#else
# define HX_NOEXCEPT
#endif

/* ABI 1 is x64 only. All arrays are caller-owned valid accessible memory.
   Each array is at most 16,000,000 doubles. Output must not overlap any input.
   Error returns leave every output element unchanged, including scalar calls.
   No device, file, network, allocation or training operation occurs here. */
enum hx_status {
    HX_OK = 0, HX_NULL = 1, HX_COUNT = 2, HX_NONFINITE = 3,
    HX_DOMAIN = 4, HX_REFERENCE = 5, HX_OVERFLOW = 6,
    HX_OVERLAP = 7, HX_INTERNAL = 255
};

HX_API uint32_t HX_CALL hx_core_abi_version(void) HX_NOEXCEPT;
/* Uniform phase = normalize(initial + 360*time_seconds/period_seconds).
   Finite intermediate arithmetic is required. This does NOT estimate a period
   or replace the per-record alternating-crossing reference. */
HX_API int32_t HX_CALL hx_phase_uniform(double time_seconds,
    double period_seconds, double initial_degrees, double* output) HX_NOEXCEPT;
HX_API int32_t HX_CALL hx_phase_uniform_batch(const double* times_seconds,
    uint32_t count, double period_seconds, double initial_degrees,
    double* output) HX_NOEXCEPT;
/* Exact LocationCycleReference.PhaseAt/Validate semantics for valid references:
   binary-search half-cycle interpolation, positive first crossing = 0 degrees,
   negative = 180 degrees, variable half-periods are preserved. Sample indices
   may be fractional. 16..16,000,000 samples, 3..201 odd crossings, strictly
   increasing; first crossing within one pre-origin sample, last matches crop.
   An out-of-window index returns HX_DOMAIN/HX_REFERENCE, never invented data. */
HX_API int32_t HX_CALL hx_phase_cycle(double sample_index, double sample_rate_hz,
    uint32_t sample_count, const double* crossings_seconds,
    uint32_t crossing_count, int32_t first_crossing_positive,
    double* output) HX_NOEXCEPT;
HX_API int32_t HX_CALL hx_phase_cycle_batch(const double* sample_indices,
    uint32_t count, double sample_rate_hz, uint32_t sample_count,
    const double* crossings_seconds, uint32_t crossing_count,
    int32_t first_crossing_positive, double* output) HX_NOEXCEPT;

#if defined(__cplusplus)
}
#endif
