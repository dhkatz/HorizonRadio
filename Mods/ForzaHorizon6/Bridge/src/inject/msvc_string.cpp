#include <cstring>
#include <horizon/inject/msvc_string.hpp>
#include <windows.h>

namespace horizon::inject {

namespace {

// Dedicated heap for replacement buffers we point game std::strings at when
// their own buffer is too small. A private heap keeps these byte-granular;
// VirtualAlloc rounded each to a 4 KB page + 64 KB reservation, leaking
// megabytes over a long session. We never free them -- the game owns these
// std::strings and may still read or destruct them, so freeing risks heap
// corruption -- but the residual is tiny and bounded (over-sized for reuse).
HANDLE replacement_heap() {
    // Function-local static init is thread-safe (C++11); HeapAlloc on a
    // serialized heap is thread-safe too, so no extra locking is needed.
    static HANDLE h = HeapCreate(0, 0, 0);
    return h;
}

} // namespace

bool write_msvc_string(MsvcString& s, std::string_view new_value) {
    const std::size_t new_len = new_value.size();

    if (new_len <= 15) {
        // Target fits SSO. Switch to inline storage; any prior heap
        // buffer is orphaned (we cannot safely free game-owned memory).
        std::memcpy(s.u.buf, new_value.data(), new_len);
        s.u.buf[new_len] = '\0';
        s.size           = new_len;
        s.capacity       = 15;
        return true;
    }

    // new_len > 15: needs heap.
    if (!is_sso(s) && s.capacity >= new_len) {
        // Reuse existing heap buffer (the game's, or one of ours).
        std::memcpy(s.u.ptr, new_value.data(), new_len);
        s.u.ptr[new_len] = '\0';
        s.size           = new_len;
        return true;
    }

    // Allocate from our private heap. Over-size the buffer so the next write
    // to this same field -- e.g. when the game advances to a longer title,
    // or our own track changes -- lands in the reuse path above instead of
    // allocating again.
    const std::size_t cap  = new_len < 128 ? 128 : new_len;
    HANDLE            heap = replacement_heap();
    if (heap == nullptr)
        return false;
    void* buf = HeapAlloc(heap, 0, cap + 1);
    if (buf == nullptr)
        return false;

    auto* new_ptr = static_cast<char*>(buf);
    std::memcpy(new_ptr, new_value.data(), new_len);
    new_ptr[new_len] = '\0';

    s.u.ptr    = new_ptr;
    s.size     = new_len;
    s.capacity = cap;
    return true;
}

} // namespace horizon::inject
