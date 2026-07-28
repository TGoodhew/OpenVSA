// A double-precision complex FFT for REQ-NFR-004's native provider.
//
// Written rather than vendored. The plan was to bring in pocketfft or kissfft, both BSD-3, but
// neither is needed for what the interface asks: a power-of-two, in-place, interleaved-double
// forward and inverse transform. Writing it keeps a third-party source file and its licence row
// out of the tree entirely, which is strictly less surface than vendoring, and REQ-NFR-008's
// register stays as short as it is.
//
// The safety net is that correctness is not being taken on trust. IFftProvider's parametrised
// suite runs the same round-trip and Parseval checks against every registered provider, and
// cross-provider agreement is asserted against the managed reference at the tolerance
// REQ-NFR-004a states. A transform that is subtly wrong fails there, in double precision, at
// sizes up to 2^20.
//
// Iterative radix-2 decimation-in-time with precomputed twiddles, cached per length. The cache
// is what makes this worth having over the managed provider: the twiddle table for a 2^20-point
// transform is 16 MB, and recomputing it per call would cost more than the butterflies.

#include <cstddef>
#include <cstdint>
#include <cmath>
#include <map>
#include <mutex>
#include <vector>

namespace
{
    struct Twiddles
    {
        std::vector<double> re;
        std::vector<double> im;
        std::vector<std::uint32_t> reversed;
    };

    std::mutex g_lock;
    std::map<int, Twiddles> g_cache;

    bool is_power_of_two(int n)
    {
        return n > 0 && (n & (n - 1)) == 0;
    }

    const Twiddles& twiddles_for(int n)
    {
        // Held under a lock and handed out by reference: entries are never erased or rewritten,
        // so a reader of an existing entry cannot see a half-built table.
        std::lock_guard<std::mutex> guard(g_lock);

        auto found = g_cache.find(n);

        if (found != g_cache.end())
        {
            return found->second;
        }

        Twiddles t;
        t.re.resize(static_cast<std::size_t>(n) / 2);
        t.im.resize(static_cast<std::size_t>(n) / 2);

        for (int k = 0; k < n / 2; ++k)
        {
            // The angle is computed from k directly rather than accumulated, so the error does
            // not grow with k. An accumulated angle is the classic way an FFT acquires a slow
            // phase drift that only shows at large sizes.
            const double angle = -2.0 * 3.14159265358979323846 * k / n;

            t.re[k] = std::cos(angle);
            t.im[k] = std::sin(angle);
        }

        t.reversed.resize(static_cast<std::size_t>(n));

        int bits = 0;
        while ((1 << bits) < n) { ++bits; }

        for (int i = 0; i < n; ++i)
        {
            std::uint32_t r = 0;

            for (int b = 0; b < bits; ++b)
            {
                r = static_cast<std::uint32_t>((r << 1) | ((i >> b) & 1));
            }

            t.reversed[static_cast<std::size_t>(i)] = r;
        }

        return g_cache.emplace(n, std::move(t)).first->second;
    }

    void transform(double* data, int n, bool inverse)
    {
        const Twiddles& t = twiddles_for(n);

        for (int i = 0; i < n; ++i)
        {
            const std::uint32_t j = t.reversed[static_cast<std::size_t>(i)];

            if (static_cast<std::uint32_t>(i) < j)
            {
                std::swap(data[2 * i], data[2 * j]);
                std::swap(data[2 * i + 1], data[2 * j + 1]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            const int half = len >> 1;
            const int step = n / len;

            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; ++k)
                {
                    const std::size_t ti = static_cast<std::size_t>(k) * step;

                    const double wr = t.re[ti];
                    // The inverse is the forward with the twiddle conjugated. Scaling is the
                    // caller's business: IFftProvider's contract is an unscaled transform, and
                    // dividing here would double-scale everything downstream.
                    const double wi = inverse ? -t.im[ti] : t.im[ti];

                    const std::size_t a = static_cast<std::size_t>(start + k) * 2;
                    const std::size_t b = static_cast<std::size_t>(start + k + half) * 2;

                    const double xr = data[b] * wr - data[b + 1] * wi;
                    const double xi = data[b] * wi + data[b + 1] * wr;

                    data[b] = data[a] - xr;
                    data[b + 1] = data[a + 1] - xi;
                    data[a] += xr;
                    data[a + 1] += xi;
                }
            }
        }
    }
}

extern "C"
{
    __declspec(dllexport) int openvsa_fft_supports(int length)
    {
        return is_power_of_two(length) ? 1 : 0;
    }

    __declspec(dllexport) int openvsa_fft_forward(double* interleaved, int length)
    {
        if (interleaved == nullptr || !is_power_of_two(length)) { return 0; }
        transform(interleaved, length, false);
        return 1;
    }

    __declspec(dllexport) int openvsa_fft_inverse(double* interleaved, int length)
    {
        if (interleaved == nullptr || !is_power_of_two(length)) { return 0; }
        transform(interleaved, length, true);
        return 1;
    }
}
