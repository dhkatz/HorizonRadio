#include <doctest/doctest.h>

#include <horizon/inject/msvc_string.hpp>

#include <windows.h>

#include <cstring>
#include <ostream>
#include <string>

using namespace horizon::inject;

namespace {

void init_sso(MsvcString& s, std::string_view value) {
    REQUIRE(value.size() <= 15);
    std::memcpy(s.u.buf, value.data(), value.size());
    s.u.buf[value.size()] = '\0';
    s.size     = value.size();
    s.capacity = 15;
}

// Allocate a heap buffer via VirtualAlloc so the test can free it
// symmetrically after the test (matching the writer's allocation
// strategy). Returns the pointer in `s.u.ptr` and sets size+capacity.
void init_heap(MsvcString& s, std::string_view value, std::size_t cap) {
    REQUIRE(value.size() <= cap);
    REQUIRE(cap > 15);
    auto* buf = static_cast<char*>(
        VirtualAlloc(nullptr, cap + 1, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    REQUIRE(buf != nullptr);
    std::memcpy(buf, value.data(), value.size());
    buf[value.size()] = '\0';
    s.u.ptr    = buf;
    s.size     = value.size();
    s.capacity = cap;
}

} // namespace

TEST_CASE("MsvcString layout sanity") {
    static_assert(sizeof(MsvcString) == 32);
    static_assert(offsetof(MsvcString, size)     == 16);
    static_assert(offsetof(MsvcString, capacity) == 24);
}

TEST_CASE("MsvcString: read returns the SSO contents") {
    MsvcString s{};
    init_sso(s, "hello");

    CHECK(is_sso(s));
    CHECK(s.size == 5);
    CHECK(view(s) == "hello");
}

TEST_CASE("MsvcString: read returns the heap contents") {
    MsvcString s{};
    init_heap(s, "longer than fifteen characters here", 64);

    CHECK_FALSE(is_sso(s));
    CHECK(s.size == 35);
    CHECK(view(s) == "longer than fifteen characters here");

    VirtualFree(s.u.ptr, 0, MEM_RELEASE);
}

TEST_CASE("MsvcString: write short into SSO stays SSO") {
    MsvcString s{};
    init_sso(s, "hi");

    REQUIRE(write_msvc_string(s, "world"));
    CHECK(is_sso(s));
    CHECK(s.size == 5);
    CHECK(s.capacity == 15);
    CHECK(view(s) == "world");
}

TEST_CASE("MsvcString: write 15-char value still fits SSO") {
    MsvcString s{};
    init_sso(s, "");

    REQUIRE(write_msvc_string(s, "123456789012345"));  // exactly 15
    CHECK(is_sso(s));
    CHECK(s.size == 15);
    CHECK(view(s) == "123456789012345");
}

TEST_CASE("MsvcString: write promotes SSO to heap when >15 chars") {
    MsvcString s{};
    init_sso(s, "short");

    const std::string long_val(50, 'a');
    REQUIRE(write_msvc_string(s, long_val));

    CHECK_FALSE(is_sso(s));
    CHECK(s.size == 50);
    CHECK(s.capacity >= 50);
    CHECK(view(s) == long_val);

    VirtualFree(s.u.ptr, 0, MEM_RELEASE);
}

TEST_CASE("MsvcString: write reuses heap when new value fits in current capacity") {
    MsvcString s{};
    init_heap(s, "initial heap value", 200);
    const char* original_ptr = s.u.ptr;

    const std::string new_val(100, 'b');
    REQUIRE(write_msvc_string(s, new_val));

    CHECK(s.u.ptr == original_ptr);     // same buffer reused
    CHECK(s.size == 100);
    CHECK(s.capacity == 200);           // capacity unchanged on in-place
    CHECK(view(s) == new_val);

    VirtualFree(s.u.ptr, 0, MEM_RELEASE);
}

TEST_CASE("MsvcString: write reallocates heap when new value exceeds capacity") {
    MsvcString s{};
    init_heap(s, "small heap value", 30);
    char* old_ptr = s.u.ptr;

    const std::string new_val(80, 'c');
    REQUIRE(write_msvc_string(s, new_val));

    CHECK(s.u.ptr != old_ptr);          // fresh allocation
    CHECK(s.size == 80);
    CHECK(s.capacity >= 80);
    CHECK(view(s) == new_val);

    // Old buffer was intentionally leaked by writer; free it here for
    // the test. Writer's behavior is correct -- the old buffer's
    // allocator isn't ours to free in production.
    VirtualFree(old_ptr, 0, MEM_RELEASE);
    VirtualFree(s.u.ptr, 0, MEM_RELEASE);
}

TEST_CASE("MsvcString: write short into heap demotes back to SSO") {
    MsvcString s{};
    init_heap(s, "a long initial heap value", 40);
    char* leaked = s.u.ptr;

    REQUIRE(write_msvc_string(s, "tiny"));

    CHECK(is_sso(s));
    CHECK(s.size == 4);
    CHECK(s.capacity == 15);
    CHECK(view(s) == "tiny");

    // The writer leaked the old heap buffer; free it here.
    VirtualFree(leaked, 0, MEM_RELEASE);
}

TEST_CASE("MsvcString: write empty string works in SSO mode") {
    MsvcString s{};
    init_sso(s, "non-empty");

    REQUIRE(write_msvc_string(s, ""));
    CHECK(is_sso(s));
    CHECK(s.size == 0);
    CHECK(view(s).empty());
}
