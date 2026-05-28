#pragma once

#include <cstddef>
#include <string_view>

namespace horizon::inject {

// 32-byte layout matching MSVC's basic_string<char> in the RELEASE CRT
// on x64. Layout:
//
//   offset 0..15   union { char buf[16]; char* ptr; }
//   offset 16..23  size_t size      -- length excluding null terminator
//   offset 24..31  size_t capacity  -- 15 when SSO; > 15 when heap
//
// SSO mode: bytes live inline in buf[]; capacity field is exactly 15.
// Heap mode: ptr holds a buffer of (capacity + 1) bytes; capacity > 15.
//
// Important: the /MDd debug CRT prepends an iterator-debug pointer to
// basic_string, shifting all field offsets by 8 bytes. This struct
// matches the /MD release layout that FH6 (and any non-debug game) will
// have in memory. Our tests operate on MsvcString directly rather than
// real std::string, so the debug-CRT prefix doesn't corrupt anything.
struct MsvcString {
    union {
        char  buf[16];
        char* ptr;
    } u;
    std::size_t size;
    std::size_t capacity;
};
static_assert(sizeof(MsvcString) == 32, "MsvcString layout drift");

inline bool is_sso(const MsvcString& s) noexcept { return s.capacity <= 15; }

inline const char* data(const MsvcString& s) noexcept {
    return is_sso(s) ? s.u.buf : s.u.ptr;
}

inline std::string_view view(const MsvcString& s) noexcept {
    return { data(s), s.size };
}

// Overwrite `s` with `new_value`. Returns true on success, false if a
// required VirtualAlloc failed.
//
// Strategy:
//   new_value.size() <= 15:
//     write inline (SSO). Any prior heap buffer is LEAKED -- we cannot
//     free memory the game's allocator owns.
//
//   new_value.size() > 15, currently heap, fits in current capacity:
//     in-place write into the existing buffer.
//
//   new_value.size() > 15 and (currently SSO or capacity insufficient):
//     VirtualAlloc a fresh buffer, replace ptr+size+capacity, leak any
//     prior heap buffer.
//
// Ownership caveat: after a heap-promoting write, the string can no
// longer be safely destroyed by the runtime that owns it -- the
// destructor would call free() on a VirtualAlloc'd address. Acceptable
// for long-lived game metadata strings (the RadioStreamFmod's
// SampleProperties are held while a track plays); not safe for
// general-purpose use.
bool write_msvc_string(MsvcString& s, std::string_view new_value);

} // namespace horizon::inject
