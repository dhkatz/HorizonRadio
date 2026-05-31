#pragma once

#include <horizon/inject/sigscan.hpp>
#include <optional>
#include <string_view>

namespace horizon::inject {

struct TypeDescriptor {
    const void* address;
};
struct CompleteObjectLocator {
    const void* address;
};
struct Vtable {
    const void* address;
}; // points at vtable[0]

class MsvcRtti {
public:
    explicit MsvcRtti(const PeImage& image);

    // Find the TypeDescriptor whose mangled name matches. The input
    // must include the ".?AV" class prefix and "@@" suffix, e.g.
    // ".?AVRadioStreamFmod@@". Returns nullopt if no exact match.
    std::optional<TypeDescriptor> find_type_descriptor(std::string_view mangled_name) const;

    // Find the COL referencing the given TypeDescriptor by RVA.
    std::optional<CompleteObjectLocator> find_complete_object_locator(TypeDescriptor td) const;

    // Find the vtable whose [-1] slot is the absolute address of `col`.
    std::optional<Vtable> find_vtable(CompleteObjectLocator col) const;

private:
    const PeImage& image_;
};

} // namespace horizon::inject
