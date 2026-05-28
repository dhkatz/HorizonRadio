#pragma once

#include <horizon/inject/sigscan.hpp>

#include <optional>
#include <string_view>

namespace horizon::inject {

// Lightweight typed wrappers around raw .rdata addresses so the API
// can't be misused (a TypeDescriptor passed where a COL is expected
// won't compile).
struct TypeDescriptor       { const void* address; };
struct CompleteObjectLocator { const void* address; };
struct Vtable               { const void* address; };  // points at vtable[0]

// Walk MSVC's x64 RTTI structures in a loaded module.
//
// Layout recap (no MSVC headers required):
//   TypeDescriptor (in .rdata):
//     +0   void* pVFTable  -- type_info's vtable in this module
//     +8   void* spare
//     +16  char  name[]    -- null-terminated mangled name (".?AVFoo@@")
//
//   RTTICompleteObjectLocator (in .rdata):
//     +0   uint32 signature       -- 1 on x64
//     +4   uint32 offset
//     +8   uint32 cdOffset
//     +12  uint32 typeDescriptor  -- RVA to TypeDescriptor
//     +16  uint32 classDescriptor -- RVA
//     +20  uint32 signatureSelf   -- RVA to this COL
//
//   Vtable (in .rdata):
//     vtable[-1] holds a 64-bit pointer to the COL.
//     vtable[0..N] are the virtual method pointers.
//     Live heap instances of the class start with a QWORD == vtable[0]'s
//     address (i.e. the address we return as Vtable.address).
//
// Game-agnostic: same primitive works for finding "RadioStreamFmod" in
// FH6 as for finding any other MSVC-built class in any other module.
class MsvcRtti {
public:
    explicit MsvcRtti(const PeImage& image);

    // Find the TypeDescriptor whose mangled name matches. The input
    // must include the ".?AV" class prefix and "@@" suffix, e.g.
    // ".?AVRadioStreamFmod@@". Returns nullopt if no exact match.
    std::optional<TypeDescriptor>
    find_type_descriptor(std::string_view mangled_name) const;

    // Find the COL referencing the given TypeDescriptor by RVA.
    std::optional<CompleteObjectLocator>
    find_complete_object_locator(TypeDescriptor td) const;

    // Find the vtable whose [-1] slot is the absolute address of `col`.
    std::optional<Vtable>
    find_vtable(CompleteObjectLocator col) const;

private:
    const PeImage& image_;
};

} // namespace horizon::inject
