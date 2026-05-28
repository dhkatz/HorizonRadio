#pragma once

#include <algorithm>
#include <atomic>
#include <cassert>
#include <cstddef>
#include <new>
#include <vector>

namespace horizon::audio {

// Single-producer, single-consumer lock-free ring buffer.
//
// push() may be called concurrently with pop() from a different thread,
// but concurrent push() calls or concurrent pop() calls are NOT safe.
// This matches the source -> FMOD bridge use case (one decoder thread
// writing, the FMOD mixer thread reading).
//
// Capacity must be a power of two: wrap-around is implemented with a
// bitmask, which is the cheapest operation on the hot read/write path.
//
// The write_ / read_ indices monotonically increase as std::size_t and
// are masked only at slot-access time. Unsigned wraparound after 2^64
// elements still produces the correct (write - read) difference, so
// overflow is a non-issue in any realistic deployment.
template <typename T>
class SpscRingBuffer {
public:
    explicit SpscRingBuffer(std::size_t capacity)
        : buf_(capacity), mask_(capacity - 1) {
        assert(capacity > 0 && (capacity & (capacity - 1)) == 0 &&
               "SpscRingBuffer capacity must be a power of two");
    }

    // Returns the number of elements actually written; less than `count`
    // when the buffer is near full.
    std::size_t push(const T* data, std::size_t count) {
        const std::size_t w = write_.load(std::memory_order_relaxed);
        const std::size_t r = read_.load(std::memory_order_acquire);
        const std::size_t space = buf_.size() - (w - r);
        const std::size_t n = std::min(count, space);
        if (n > 0) {
            const std::size_t first_idx   = w & mask_;
            const std::size_t first_chunk = std::min(n, buf_.size() - first_idx);
            std::copy_n(data, first_chunk, buf_.begin() + first_idx);
            if (n > first_chunk) {
                std::copy_n(data + first_chunk, n - first_chunk, buf_.begin());
            }
            write_.store(w + n, std::memory_order_release);
        }
        return n;
    }

    // Returns the number of elements actually read; less than `count`
    // when the buffer is near empty.
    std::size_t pop(T* out, std::size_t count) {
        const std::size_t r = read_.load(std::memory_order_relaxed);
        const std::size_t w = write_.load(std::memory_order_acquire);
        const std::size_t avail = w - r;
        const std::size_t n = std::min(count, avail);
        if (n > 0) {
            const std::size_t first_idx   = r & mask_;
            const std::size_t first_chunk = std::min(n, buf_.size() - first_idx);
            std::copy_n(buf_.begin() + first_idx, first_chunk, out);
            if (n > first_chunk) {
                std::copy_n(buf_.begin(), n - first_chunk, out + first_chunk);
            }
            read_.store(r + n, std::memory_order_release);
        }
        return n;
    }

    std::size_t readable() const noexcept {
        const std::size_t r = read_.load(std::memory_order_acquire);
        const std::size_t w = write_.load(std::memory_order_acquire);
        return w - r;
    }

    std::size_t writable() const noexcept {
        return buf_.size() - readable();
    }

    std::size_t capacity() const noexcept { return buf_.size(); }

    // Not safe under concurrent push/pop -- intended for setup/teardown.
    void reset() noexcept {
        write_.store(0, std::memory_order_relaxed);
        read_.store(0, std::memory_order_relaxed);
    }

    // Drop everything currently readable. SAFE to call from the
    // consumer thread (only modifies read_, which only the consumer
    // is allowed to touch). Producer can keep push()-ing concurrently.
    // Used when the consumer detects "the data ahead of me is stale"
    // (e.g. the upstream paused and resumed) and wants to skip to
    // whatever the producer is writing right now.
    void discard_all_from_consumer() noexcept {
        const auto w = write_.load(std::memory_order_acquire);
        read_.store(w, std::memory_order_release);
    }

private:
    // 64-byte alignment on the two atomics keeps them on separate cache
    // lines, so producer writes to write_ don't bounce the line that
    // the consumer reads read_ from (and vice versa).
    static constexpr std::size_t kCacheLine = 64;

    std::vector<T> buf_;
    std::size_t    mask_;
    alignas(kCacheLine) std::atomic<std::size_t> write_{0};
    alignas(kCacheLine) std::atomic<std::size_t> read_{0};
};

} // namespace horizon::audio
