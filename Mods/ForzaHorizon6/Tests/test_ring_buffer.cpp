#include <array>
#include <doctest/doctest.h>
#include <horizon/audio/ring_buffer.hpp>
#include <thread>
#include <utility>
#include <vector>

using horizon::audio::SpscRingBuffer;

TEST_CASE("empty after construction") {
    SpscRingBuffer<int> rb(16);
    CHECK(rb.capacity() == 16);
    CHECK(rb.readable() == 0);
    CHECK(rb.writable() == 16);
}

TEST_CASE("push and pop roundtrip") {
    SpscRingBuffer<int> rb(16);
    const int           in[] = {1, 2, 3, 4};
    CHECK(rb.push(in, 4) == 4);
    CHECK(rb.readable() == 4);

    int out[4] = {};
    CHECK(rb.pop(out, 4) == 4);
    CHECK(out[0] == 1);
    CHECK(out[3] == 4);
    CHECK(rb.readable() == 0);
}

TEST_CASE("push truncates at capacity") {
    SpscRingBuffer<int> rb(4);
    const int           in[] = {1, 2, 3, 4, 5, 6};
    CHECK(rb.push(in, 6) == 4);
    CHECK(rb.writable() == 0);
    CHECK(rb.readable() == 4);
}

TEST_CASE("pop truncates at available") {
    SpscRingBuffer<int> rb(16);
    const int           in[] = {1, 2};
    rb.push(in, 2);
    int out[10] = {};
    CHECK(rb.pop(out, 10) == 2);
}

TEST_CASE("wraps across the buffer boundary") {
    SpscRingBuffer<int> rb(4);
    const int           in1[] = {1, 2, 3};
    rb.push(in1, 3);
    int discard[3] = {};
    rb.pop(discard, 3);

    // Write head is now at 3; capacity 4. Pushing 3 more must wrap.
    const int in2[] = {10, 11, 12};
    CHECK(rb.push(in2, 3) == 3);

    int out[3] = {};
    CHECK(rb.pop(out, 3) == 3);
    CHECK(out[0] == 10);
    CHECK(out[1] == 11);
    CHECK(out[2] == 12);
}

TEST_CASE("reset clears state") {
    SpscRingBuffer<int> rb(16);
    const int           in[] = {1, 2, 3};
    rb.push(in, 3);
    rb.reset();
    CHECK(rb.readable() == 0);
    CHECK(rb.writable() == 16);
}

TEST_CASE("concurrent producer and consumer preserve order") {
    SpscRingBuffer<int> rb(1024);
    constexpr int       N = 100'000;

    std::thread producer([&] {
        int                 produced = 0;
        std::array<int, 64> chunk{};
        while (produced < N) {
            int batch = std::min<int>(64, N - produced);
            for (int j = 0; j < batch; ++j)
                chunk[j] = produced + j;
            const std::size_t pushed = rb.push(chunk.data(), batch);
            produced += static_cast<int>(pushed);
            if (std::cmp_less(pushed, batch))
                std::this_thread::yield();
        }
    });

    std::vector<int> received;
    received.reserve(N);
    std::array<int, 64> out{};
    while (static_cast<int>(received.size()) < N) {
        const std::size_t got = rb.pop(out.data(), out.size());
        for (std::size_t j = 0; j < got; ++j)
            received.push_back(out[j]);
        if (got == 0)
            std::this_thread::yield();
    }
    producer.join();

    REQUIRE(received.size() == static_cast<std::size_t>(N));
    for (int i = 0; i < N; ++i) {
        REQUIRE(received[i] == i);
    }
}
