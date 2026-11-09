#pragma once

#include <algorithm>
#include <atomic>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace horizon::tap {

// Single-consumer byte ring. Writers are serialized by the caller (TapWriter's lock); the pipe
// thread reads without a lock. Capacity is a power of two.
class ByteRing {
public:
    explicit ByteRing(std::size_t capacity) : buf_(capacity), mask_(capacity - 1) {
        assert(capacity > 0 && (capacity & (capacity - 1)) == 0 && "ByteRing capacity must be a power of two");
    }

    // Writes all of `data` or nothing.
    bool push(const std::uint8_t* data, std::size_t count) {
        const std::size_t w = write_.load(std::memory_order_relaxed);
        const std::size_t r = read_.load(std::memory_order_acquire);
        if (buf_.size() - (w - r) < count) {
            return false;
        }
        copy_in(w, data, count);
        write_.store(w + count, std::memory_order_release);
        return true;
    }

    // Writes `a` then `b` as one unit, or nothing: a message's header never lands without its body.
    bool push2(const std::uint8_t* a, std::size_t a_count, const std::uint8_t* b, std::size_t b_count) {
        const std::size_t w = write_.load(std::memory_order_relaxed);
        const std::size_t r = read_.load(std::memory_order_acquire);
        if (buf_.size() - (w - r) < a_count + b_count) {
            return false;
        }
        copy_in(w, a, a_count);
        copy_in(w + a_count, b, b_count);
        write_.store(w + a_count + b_count, std::memory_order_release);
        return true;
    }

    std::size_t pop(std::uint8_t* out, std::size_t count) {
        const std::size_t r     = read_.load(std::memory_order_relaxed);
        const std::size_t w     = write_.load(std::memory_order_acquire);
        const std::size_t avail = w - r;
        const std::size_t n     = std::min(count, avail);
        if (n > 0) {
            const std::size_t first_idx   = r & mask_;
            const std::size_t first_chunk = std::min(n, buf_.size() - first_idx);
            std::copy_n(buf_.begin() + static_cast<std::ptrdiff_t>(first_idx), first_chunk, out);
            if (n > first_chunk) {
                std::copy_n(buf_.begin(), n - first_chunk, out + first_chunk);
            }
            read_.store(r + n, std::memory_order_release);
        }
        return n;
    }

    std::size_t readable() const noexcept {
        return write_.load(std::memory_order_acquire) - read_.load(std::memory_order_acquire);
    }

    std::size_t capacity() const noexcept {
        return buf_.size();
    }

private:
    void copy_in(std::size_t at, const std::uint8_t* data, std::size_t count) {
        if (count == 0) {
            return;
        }
        const std::size_t first_idx   = at & mask_;
        const std::size_t first_chunk = std::min(count, buf_.size() - first_idx);
        std::copy_n(data, first_chunk, buf_.begin() + static_cast<std::ptrdiff_t>(first_idx));
        if (count > first_chunk) {
            std::copy_n(data + first_chunk, count - first_chunk, buf_.begin());
        }
    }

    static constexpr std::size_t kCacheLine = 64;

    std::vector<std::uint8_t> buf_;
    std::size_t               mask_;
    alignas(kCacheLine) std::atomic<std::size_t> write_{0};
    alignas(kCacheLine) std::atomic<std::size_t> read_{0};
};

} // namespace horizon::tap
