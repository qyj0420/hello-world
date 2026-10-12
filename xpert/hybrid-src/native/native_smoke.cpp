#include "hvx_hybrid_core.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <thread>
#include <vector>
#include <atomic>

namespace {
uint64_t checks = 0;
void require(bool condition, const char* message) {
    ++checks;
    if (!condition) { std::cerr << "FAIL " << message << '\n'; std::exit(1); }
}
void close(double actual, double expected, const char* message) {
    require(std::isfinite(actual) && actual >= 0 && actual < 360, "phase finite and normalized");
    require(std::abs(actual - expected) <= 2e-11, message);
}
// Independent bounded port of the existing C# PhaseAt oracle. Root QA also
// compiles and executes the unchanged C# source itself for cross-language proof.
double oracle(double index, double rate, const std::vector<double>& crossings, bool positive) {
    const double t = index / rate;
    uint32_t lo = 0, hi = static_cast<uint32_t>(crossings.size() - 1);
    while (hi - lo > 1) { const uint32_t mid = (lo + hi) / 2;
        if (crossings[mid] <= t) lo = mid; else hi = mid; }
    double phase = (positive ? 0.0 : 180.0) +
        180.0 * (static_cast<double>(lo) + (t - crossings[lo]) / (crossings[hi] - crossings[lo]));
    phase = std::fmod(phase, 360.0);
    return phase < 0 ? phase + 360.0 : phase;
}
uint64_t next(uint64_t& state) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; return state; }
}
int main() {
    static_assert(sizeof(void*) == 8, "x64 native smoke required");
    const double nan = (std::numeric_limits<double>::quiet_NaN)();
    const double inf = (std::numeric_limits<double>::infinity)();
    require(hx_core_abi_version() == 1, "ABI version");
    double value = -999;
    require(hx_phase_uniform(0, .02, 0, &value) == HX_OK, "uniform origin"); close(value, 0, "zero origin");
    require(hx_phase_uniform(.005, .02, 0, &value) == HX_OK, "uniform quadrant"); close(value, 90, "quarter period");
    require(hx_phase_uniform(-.005, .02, 0, &value) == HX_OK, "negative time"); close(value, 270, "negative normalization");
    require(hx_phase_uniform(.02, .02, 180, &value) == HX_OK, "full cycle"); close(value, 180, "initial offset");
    require(hx_phase_uniform(0, .02, -360, &value) == HX_OK, "negative integer turn");
    require(value == 0 && !std::signbit(value), "negative zero canonicalization");
    struct InvalidUniform { double time, period, initial; int32_t status; };
    const InvalidUniform invalid[] = {
        {nan, .02, 0, HX_NONFINITE}, {inf, .02, 0, HX_NONFINITE}, {0, nan, 0, HX_NONFINITE},
        {0, inf, 0, HX_NONFINITE}, {0, .02, nan, HX_NONFINITE}, {0, .02, inf, HX_NONFINITE},
        {1, 0, 0, HX_DOMAIN}, {1, -.02, 0, HX_DOMAIN},
        {(std::numeric_limits<double>::max)(), .02, 0, HX_OVERFLOW},
        {1, (std::numeric_limits<double>::denorm_min)(), 0, HX_OVERFLOW}
    };
    for (const auto& item : invalid) { value = -999;
        require(hx_phase_uniform(item.time, item.period, item.initial, &value) == item.status, "uniform strict error");
        require(value == -999, "uniform unchanged output on error"); }
    require(hx_phase_uniform(1, .02, 0, nullptr) == HX_NULL, "scalar null output");
    double times[] = {0, .005, .01, .015, .02};
    double phases[5] = {};
    require(hx_phase_uniform_batch(times, 5, .02, 0, phases) == HX_OK, "uniform batch");
    for (uint32_t i = 0; i < 5; ++i) close(phases[i], static_cast<double>((i * 90) % 360), "batch phase");
    require(hx_phase_uniform_batch(times, 5, .02, 0, times) == HX_OVERLAP, "in-place rejected");
    require(hx_phase_uniform_batch(times, 0, .02, 0, phases) == HX_COUNT, "empty batch rejected");
    require(hx_phase_uniform_batch(times, 16000001, .02, 0, phases) == HX_COUNT, "oversized batch rejected before dereference");
    require(hx_phase_uniform_batch(nullptr, 5, .02, 0, phases) == HX_NULL, "null input");
    times[4] = nan; std::fill(phases, phases + 5, -999);
    require(hx_phase_uniform_batch(times, 5, .02, 0, phases) == HX_NONFINITE, "late invalid batch item");
    require(std::all_of(phases, phases + 5, [](double v) { return v == -999; }), "no partial uniform batch output");

    const double rate = 1000;
    const uint32_t samples = 51;
    const std::vector<double> crossings = {-.0002, .0117, .0248, .0364, .0502};
    for (int32_t positive = 0; positive <= 1; ++positive) {
        for (uint32_t sample = 0; sample < samples; ++sample) {
            require(hx_phase_cycle(static_cast<double>(sample), rate, samples, crossings.data(), 5, positive, &value) == HX_OK,
                "variable half-cycle sample");
            close(value, oracle(static_cast<double>(sample), rate, crossings, positive != 0), "variable-period oracle parity");
        }
        const std::vector<double> fractional = {0, .25, 7.125, 11.7, 24.8, 36.4, 50.125};
        std::vector<double> result(fractional.size());
        require(hx_phase_cycle_batch(fractional.data(), static_cast<uint32_t>(fractional.size()), rate, samples,
            crossings.data(), 5, positive, result.data()) == HX_OK, "fractional batch");
        for (size_t i = 0; i < fractional.size(); ++i) close(result[i], oracle(fractional[i], rate, crossings, positive != 0), "fractional oracle parity");
    }
    const double indices[] = {0, 25, 51};
    double outputs[] = {-999, -999, -999};
    require(hx_phase_cycle_batch(indices, 3, rate, samples, crossings.data(), 5, 1, outputs) == HX_DOMAIN, "late out-of-range index");
    require(std::all_of(outputs, outputs + 3, [](double v) { return v == -999; }), "no partial cycle batch output");
    require(hx_phase_cycle(0, rate, samples, crossings.data(), 5, 1, const_cast<double*>(crossings.data())) == HX_OVERLAP,
        "scalar output/reference overlap rejected");
    value = -999;
    require(hx_phase_cycle(-1, rate, samples, crossings.data(), 5, 1, &value) == HX_DOMAIN, "negative sample rejected");
    require(hx_phase_cycle(nan, rate, samples, crossings.data(), 5, 1, &value) == HX_NONFINITE, "nan sample rejected");
    require(hx_phase_cycle(0, 0, samples, crossings.data(), 5, 1, &value) == HX_DOMAIN, "zero rate rejected");
    require(hx_phase_cycle(0, inf, samples, crossings.data(), 5, 1, &value) == HX_NONFINITE, "infinite rate rejected");
    require(hx_phase_cycle(0, rate, 15, crossings.data(), 5, 1, &value) == HX_COUNT, "short crop rejected");
    require(hx_phase_cycle(0, rate, 16000001, crossings.data(), 5, 1, &value) == HX_COUNT, "large crop rejected");
    require(hx_phase_cycle(0, rate, samples, crossings.data(), 4, 1, &value) == HX_COUNT, "even crossings rejected");
    require(hx_phase_cycle(0, rate, samples, crossings.data(), 5, 2, &value) == HX_DOMAIN, "invalid boolean rejected");
    auto broken = crossings; broken[2] = broken[1];
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_REFERENCE, "duplicate crossings rejected");
    broken = crossings; broken[2] = inf;
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_NONFINITE, "nonfinite crossings rejected");
    broken = crossings; broken[0] = .0002;
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_REFERENCE, "late first crossing rejected");
    broken = crossings; broken[0] = -.002;
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_REFERENCE, "early first crossing rejected");
    broken = crossings; broken[4] = .049;
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_REFERENCE, "incomplete cycle rejected");
    broken = crossings; broken[4] = .051 + 2e-12;
    require(hx_phase_cycle(0, rate, samples, broken.data(), 5, 1, &value) == HX_REFERENCE, "crop beyond last crossing rejected");
    require(value == -999, "scalar invalid cycle requests leave output unchanged");
    require(hx_phase_cycle(50.2000000005, rate, samples, crossings.data(), 5, 1, &value) == HX_OK,
        "one-picosecond end tolerance preserved");
    value = -999;
    require(hx_phase_cycle(50.200000002, rate, samples, crossings.data(), 5, 1, &value) == HX_REFERENCE,
        "beyond end tolerance rejected");
    require(value == -999, "out-of-window leaves output unchanged");

    // 200 reproducible irregular references × 41 fractional indices × both
    // directions. This catches constant-frequency substitution and edge errors.
    uint64_t state = UINT64_C(0x7d13e8659ab24401);
    for (uint32_t record = 0; record < 200; ++record) {
        const uint32_t cycle_count = 1 + static_cast<uint32_t>(next(state) % 20);
        const uint32_t crossing_count = 2 * cycle_count + 1;
        const double record_rate = 100000.0 + static_cast<double>(next(state) % 10000000);
        std::vector<double> ref(crossing_count); ref[0] = -.4 / record_rate;
        for (uint32_t i = 1; i < crossing_count; ++i)
            ref[i] = ref[i - 1] + static_cast<double>(10 + next(state) % 500) / record_rate;
        const uint32_t record_samples = static_cast<uint32_t>(std::floor(ref.back() * record_rate)) + 1;
        for (uint32_t p = 0; p <= 1; ++p) for (uint32_t j = 0; j <= 40; ++j) {
            const double index = static_cast<double>(record_samples - 1) * static_cast<double>(j) / 40;
            require(hx_phase_cycle(index, record_rate, record_samples, ref.data(), crossing_count, static_cast<int32_t>(p), &value) == HX_OK,
                "seeded reference accepted");
            close(value, oracle(index, record_rate, ref, p != 0), "seeded oracle parity");
        }
    }
    std::atomic<bool> concurrent_ok{true};
    std::vector<std::thread> workers;
    for (int worker = 0; worker < 8; ++worker) workers.emplace_back([&]() {
        for (uint32_t i = 0; i < 2000; ++i) {
            double local = 0;
            if (hx_phase_cycle(static_cast<double>(i % samples), rate, samples, crossings.data(), 5, 1, &local) != HX_OK ||
                std::abs(local - oracle(static_cast<double>(i % samples), rate, crossings, true)) > 2e-11)
                concurrent_ok.store(false);
        }
    });
    for (auto& worker : workers) worker.join();
    require(concurrent_ok.load(), "thread-independent no mutable state");
    std::cout << "{\"status\":\"PASS\",\"checks\":" << checks << ",\"hardwareTouched\":false,\"abiVersion\":1}\n";
    return 0;
}
