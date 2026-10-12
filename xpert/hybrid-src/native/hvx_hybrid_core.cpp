#define HVX_HYBRID_CORE_EXPORTS
#include "hvx_hybrid_core.h"
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

static_assert(sizeof(void*) == 8, "HVX hybrid ABI 1 requires an x64 build.");
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
    "HVX hybrid ABI 1 requires IEEE-754 binary64.");

namespace {
constexpr uint32_t max_values = 16000000;
constexpr double time_tolerance = 1e-12;
bool finite(double value) noexcept { return std::isfinite(value); }

int32_t span(const double* pointer, uint32_t count,
    uintptr_t& begin, uintptr_t& end) noexcept {
    if (pointer == nullptr) return HX_NULL;
    if (count == 0 || count > max_values) return HX_COUNT;
    begin = reinterpret_cast<uintptr_t>(pointer);
    if (begin % alignof(double) != 0) return HX_DOMAIN;
    const uintptr_t bytes = static_cast<uintptr_t>(count) * sizeof(double);
    if (begin > (std::numeric_limits<uintptr_t>::max)() - bytes) return HX_OVERFLOW;
    end = begin + bytes;
    return HX_OK;
}
int32_t disjoint(const double* input, uint32_t input_count,
    const double* output, uint32_t output_count) noexcept {
    uintptr_t ib = 0, ie = 0, ob = 0, oe = 0;
    int32_t status = span(input, input_count, ib, ie);
    if (status != HX_OK) return status;
    status = span(output, output_count, ob, oe);
    if (status != HX_OK) return status;
    return ib < oe && ob < ie ? HX_OVERLAP : HX_OK;
}
int32_t output_ok(const double* output) noexcept {
    uintptr_t begin = 0, end = 0;
    return span(output, 1, begin, end);
}
int32_t uniform(double time, double period, double initial, double& result) noexcept {
    if (!finite(time) || !finite(period) || !finite(initial)) return HX_NONFINITE;
    if (period <= 0) return HX_DOMAIN;
    // Preserve explicit operation order; never use /fp:fast or fused arithmetic.
    const double scaled = 360.0 * time;
    if (!finite(scaled)) return HX_OVERFLOW;
    const double turns = scaled / period;
    if (!finite(turns)) return HX_OVERFLOW;
    const double degrees = initial + turns;
    if (!finite(degrees)) return HX_OVERFLOW;
    double normalized = std::fmod(degrees, 360.0);
    if (normalized < 0) normalized += 360.0;
    // Removing the negative-zero sign does not alter a valid phase value.
    result = normalized == 0 ? 0 : normalized;
    return HX_OK;
}
int32_t reference(double rate, uint32_t samples, const double* crossings,
    uint32_t count, int32_t positive) noexcept {
    if (!finite(rate)) return HX_NONFINITE;
    if (rate <= 0) return HX_DOMAIN;
    if (samples < 16 || samples > max_values || count < 3 || count > 201 || count % 2 != 1)
        return HX_COUNT;
    if (positive != 0 && positive != 1) return HX_DOMAIN;
    uintptr_t begin = 0, end = 0;
    int32_t status = span(crossings, count, begin, end);
    if (status != HX_OK) return status;
    for (uint32_t i = 0; i < count; ++i) {
        if (!finite(crossings[i])) return HX_NONFINITE;
        if (i > 0 && crossings[i] <= crossings[i - 1]) return HX_REFERENCE;
    }
    const double interval = 1.0 / rate;
    const double last_sample = static_cast<double>(samples - 1) / rate;
    const double crop_end = static_cast<double>(samples) / rate;
    if (!finite(interval) || !finite(last_sample) || !finite(crop_end)) return HX_OVERFLOW;
    if (crossings[0] > time_tolerance || crossings[0] < -interval - time_tolerance ||
        crossings[count - 1] < last_sample - time_tolerance ||
        crossings[count - 1] >= crop_end + time_tolerance) return HX_REFERENCE;
    return HX_OK;
}
int32_t cycle(double index, double rate, uint32_t samples,
    const double* crossings, uint32_t count, int32_t positive,
    double& result) noexcept {
    if (!finite(index)) return HX_NONFINITE;
    if (index < 0 || index >= static_cast<double>(samples)) return HX_DOMAIN;
    const double t = index / rate;
    if (!finite(t)) return HX_OVERFLOW;
    if (t < crossings[0] - time_tolerance || t > crossings[count - 1] + time_tolerance)
        return HX_REFERENCE;
    uint32_t lo = 0, hi = count - 1;
    while (hi - lo > 1) {
        const uint32_t mid = (lo + hi) / 2;
        if (crossings[mid] <= t) lo = mid; else hi = mid;
    }
    const double fraction = (t - crossings[lo]) / (crossings[hi] - crossings[lo]);
    const double degrees = (positive != 0 ? 0.0 : 180.0) +
        180.0 * (static_cast<double>(lo) + fraction);
    if (!finite(fraction) || !finite(degrees)) return HX_OVERFLOW;
    double normalized = std::fmod(degrees, 360.0);
    if (normalized < 0) normalized += 360.0;
    result = normalized == 0 ? 0 : normalized;
    return HX_OK;
}
}

extern "C" HX_API uint32_t HX_CALL hx_core_abi_version(void) noexcept { return 1; }

extern "C" HX_API int32_t HX_CALL hx_phase_uniform(double time, double period,
    double initial, double* output) noexcept {
    try {
        int32_t status = output_ok(output);
        if (status != HX_OK) return status;
        double result = 0;
        status = uniform(time, period, initial, result);
        if (status == HX_OK) *output = result;
        return status;
    } catch (...) { return HX_INTERNAL; }
}
extern "C" HX_API int32_t HX_CALL hx_phase_uniform_batch(const double* times,
    uint32_t count, double period, double initial, double* output) noexcept {
    try {
        int32_t status = disjoint(times, count, output, count);
        if (status != HX_OK) return status;
        double result = 0;
        // Validate the complete request before touching caller output.
        for (uint32_t i = 0; i < count; ++i) {
            status = uniform(times[i], period, initial, result);
            if (status != HX_OK) return status;
        }
        for (uint32_t i = 0; i < count; ++i) {
            (void)uniform(times[i], period, initial, result);
            output[i] = result;
        }
        return HX_OK;
    } catch (...) { return HX_INTERNAL; }
}
extern "C" HX_API int32_t HX_CALL hx_phase_cycle(double index, double rate,
    uint32_t samples, const double* crossings, uint32_t count, int32_t positive,
    double* output) noexcept {
    try {
        int32_t status = disjoint(crossings, count, output, 1);
        if (status != HX_OK) return status;
        status = reference(rate, samples, crossings, count, positive);
        if (status != HX_OK) return status;
        double result = 0;
        status = cycle(index, rate, samples, crossings, count, positive, result);
        if (status == HX_OK) *output = result;
        return status;
    } catch (...) { return HX_INTERNAL; }
}
extern "C" HX_API int32_t HX_CALL hx_phase_cycle_batch(const double* indices,
    uint32_t length, double rate, uint32_t samples, const double* crossings,
    uint32_t count, int32_t positive, double* output) noexcept {
    try {
        int32_t status = disjoint(indices, length, output, length);
        if (status != HX_OK) return status;
        status = disjoint(crossings, count, output, length);
        if (status != HX_OK) return status;
        status = reference(rate, samples, crossings, count, positive);
        if (status != HX_OK) return status;
        double result = 0;
        for (uint32_t i = 0; i < length; ++i) {
            status = cycle(indices[i], rate, samples, crossings, count, positive, result);
            if (status != HX_OK) return status;
        }
        for (uint32_t i = 0; i < length; ++i) {
            (void)cycle(indices[i], rate, samples, crossings, count, positive, result);
            output[i] = result;
        }
        return HX_OK;
    } catch (...) { return HX_INTERNAL; }
}
