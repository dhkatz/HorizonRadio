#pragma once

#include <horizon/inject/msvc_rtti.hpp>

#include <cstddef>
#include <functional>
#include <vector>

namespace horizon::inject {

// Walk all committed MEM_PRIVATE memory in this process and return the
// addresses of every 8-byte aligned QWORD whose value equals `target`.
//
// Use case: finding live C++ polymorphic instances. The first QWORD of
// any such instance is the class's vtable pointer; passing the vtable
// address as `target` yields candidate instance start addresses.
//
// The scan skips guard pages, no-access pages, mapped files, and
// module images -- only true heap-style private memory is searched.
// False positives are possible (an unrelated QWORD may coincidentally
// equal the target); callers should validate further if needed.
//
// O(committed heap size). For a test exe this is sub-second; for FH6
// it may take several seconds. Intended for one-time discovery at
// startup, not for hot-path use.
std::vector<const void*> find_heap_instances(const void* target);

// Convenience overload accepting a Vtable handle from MsvcRtti.
inline std::vector<const void*> find_heap_instances(Vtable vt) {
    return find_heap_instances(vt.address);
}

// Streaming linear scan, used by find_heap_instances. Same coverage,
// but invokes `on_progress(bytes_scanned)` periodically so callers
// can publish partial results and abort via `should_continue()`.
// Held public so the discovery dump path can run it with progress
// logging instead of going dark for tens of seconds.
void find_heap_instances_streaming(
    const void* target,
    std::vector<const void*>& matches_out,
    const std::function<bool()>&     should_continue,
    const std::function<void(std::size_t bytes_scanned)>& on_progress);

} // namespace horizon::inject
