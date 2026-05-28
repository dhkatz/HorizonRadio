#include <horizon/inject/msvc_string.hpp>

#include <windows.h>

#include <cstring>

namespace horizon::inject {

bool write_msvc_string(MsvcString& s, std::string_view new_value) {
    const std::size_t new_len = new_value.size();

    if (new_len <= 15) {
        // Target fits SSO. Switch to inline storage; any prior heap
        // buffer is leaked (we cannot safely free game-owned memory).
        std::memcpy(s.u.buf, new_value.data(), new_len);
        s.u.buf[new_len] = '\0';
        s.size     = new_len;
        s.capacity = 15;
        return true;
    }

    // new_len > 15: needs heap.
    if (!is_sso(s) && s.capacity >= new_len) {
        // Reuse existing heap buffer.
        std::memcpy(s.u.ptr, new_value.data(), new_len);
        s.u.ptr[new_len] = '\0';
        s.size = new_len;
        return true;
    }

    // Allocate fresh heap buffer.
    void* buf = VirtualAlloc(nullptr, new_len + 1,
                             MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buf) return false;

    auto* new_ptr = static_cast<char*>(buf);
    std::memcpy(new_ptr, new_value.data(), new_len);
    new_ptr[new_len] = '\0';

    s.u.ptr    = new_ptr;
    s.size     = new_len;
    s.capacity = new_len;
    return true;
}

} // namespace horizon::inject
