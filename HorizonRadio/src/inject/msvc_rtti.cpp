#include <horizon/inject/msvc_rtti.hpp>

#include <algorithm>
#include <cstdint>
#include <string>

namespace horizon::inject {

namespace {

// Offset of the `name` field within MSVC's _TypeDescriptor on x64:
//   pVFTable (8) + spare (8) = 16
constexpr std::ptrdiff_t kTypeDescNameOffset = 16;

// The COL is six uint32 fields = 24 bytes.
constexpr std::size_t kColUint32Count = 6;

} // namespace

MsvcRtti::MsvcRtti(const PeImage& image) : image_(image) {}

std::optional<TypeDescriptor>
MsvcRtti::find_type_descriptor(std::string_view mangled_name) const {
    // Search with the trailing nul so we don't false-match a class
    // whose name is a prefix of ours (".?AVFoo@@" vs ".?AVFooBar@@").
    std::string needle(mangled_name);
    needle.push_back('\0');

    // TypeDescriptors live in .data (not .rdata) because their `spare`
    // field is written by the runtime to cache the demangled name. The
    // string literals our own callers pass in are in .rdata, so they
    // won't collide with the real TypeDescriptors here.
    auto data = image_.data();
    const auto* begin = reinterpret_cast<const char*>(data.data());
    const auto* end   = begin + data.size();

    const auto* hit = std::search(begin, end, needle.begin(), needle.end());
    if (hit == end) return std::nullopt;

    if (hit - kTypeDescNameOffset < begin) return std::nullopt;
    return TypeDescriptor{ hit - kTypeDescNameOffset };
}

std::optional<CompleteObjectLocator>
MsvcRtti::find_complete_object_locator(TypeDescriptor td) const {
    const std::uintptr_t base = image_.base();
    const std::uint32_t  td_rva = static_cast<std::uint32_t>(
        reinterpret_cast<std::uintptr_t>(td.address) - base);

    auto rdata = image_.rdata();
    const auto* p = reinterpret_cast<const std::uint32_t*>(rdata.data());
    const std::size_t n = rdata.size() / sizeof(std::uint32_t);
    if (n < kColUint32Count) return std::nullopt;

    // A COL on x64 has signature==1 followed three uint32s later by the
    // TypeDescriptor RVA. Walking 4-byte aligned is fine because .rdata
    // starts at a page boundary; any real COL lands on a 4-byte stride
    // from there.
    for (std::size_t i = 0; i + kColUint32Count <= n; ++i) {
        if (p[i] == 1u && p[i + 3] == td_rva) {
            return CompleteObjectLocator{ p + i };
        }
    }
    return std::nullopt;
}

std::optional<Vtable>
MsvcRtti::find_vtable(CompleteObjectLocator col) const {
    const std::uintptr_t col_addr = reinterpret_cast<std::uintptr_t>(col.address);

    auto rdata = image_.rdata();
    const auto* p = reinterpret_cast<const std::uintptr_t*>(rdata.data());
    const std::size_t n = rdata.size() / sizeof(std::uintptr_t);
    if (n < 2) return std::nullopt;

    // vtable[-1] holds the COL pointer. Once we find a QWORD that
    // equals col_addr, the vtable begins at the *next* QWORD.
    for (std::size_t i = 0; i + 1 < n; ++i) {
        if (p[i] == col_addr) {
            return Vtable{ p + i + 1 };
        }
    }
    return std::nullopt;
}

} // namespace horizon::inject
