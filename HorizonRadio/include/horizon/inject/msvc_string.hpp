#pragma once

#include <string_view>

namespace horizon::inject {

struct MsvcString {
    union {
        char  buf[16];
        char* ptr;
    } u;
    std::size_t size;
    std::size_t capacity;
};
static_assert(sizeof(MsvcString) == 32, "MsvcString layout drift");

inline bool is_sso(const MsvcString& s) noexcept {
    return s.capacity <= 15;
}

inline const char* data(const MsvcString& s) noexcept {
    return is_sso(s) ? s.u.buf : s.u.ptr;
}

inline std::string_view view(const MsvcString& s) noexcept {
    return {data(s), s.size};
}

bool write_msvc_string(MsvcString& s, std::string_view new_value);

} // namespace horizon::inject
