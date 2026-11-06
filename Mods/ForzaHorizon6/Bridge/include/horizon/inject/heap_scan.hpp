#pragma once

#include <functional>
#include <horizon/inject/msvc_rtti.hpp>
#include <vector>

namespace horizon::inject {

std::vector<const void*> find_heap_instances(const void* target);

// Convenience overload accepting a Vtable handle from MsvcRtti.
inline std::vector<const void*> find_heap_instances(const Vtable vt) {
    return find_heap_instances(vt.address);
}

void find_heap_instances_streaming(const void* target, std::vector<const void*>& matches_out,
                                   const std::function<bool()>&                          should_continue,
                                   const std::function<void(std::size_t bytes_scanned)>& on_progress);

} // namespace horizon::inject
