#include <doctest/doctest.h>

#include <horizon/inject/msvc_rtti.hpp>

#include <windows.h>

#include <cstdio>
#include <cstring>
#include <ios>
#include <memory>

using namespace horizon::inject;

// A class with virtual methods so MSVC emits RTTI for it. The
// TypeDescriptor, COL, and vtable all land in this test exe's .rdata
// regardless of internal linkage.
class HorizonRttiTestTarget {
public:
    virtual ~HorizonRttiTestTarget() = default;
    virtual int  compute(int x)   { return x * 31 + 7; }
    virtual int  identify() const { return 12345; }
};

// A second class so the "no match" test has a class name that is *not*
// in .rdata to look for.
class HorizonRttiUnused {
public:
    virtual ~HorizonRttiUnused() = default;
};

TEST_CASE("MsvcRtti: locates TypeDescriptor by exact mangled name") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto td = rtti.find_type_descriptor(".?AVHorizonRttiTestTarget@@");
    REQUIRE(td.has_value());
}

TEST_CASE("MsvcRtti: locates COL referencing the TypeDescriptor") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto td = rtti.find_type_descriptor(".?AVHorizonRttiTestTarget@@");
    REQUIRE(td.has_value());
    auto col = rtti.find_complete_object_locator(*td);
    REQUIRE(col.has_value());
}

TEST_CASE("MsvcRtti: locates vtable whose [-1] points at the COL") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto td = rtti.find_type_descriptor(".?AVHorizonRttiTestTarget@@");
    REQUIRE(td.has_value());
    auto col = rtti.find_complete_object_locator(*td);
    REQUIRE(col.has_value());
    auto vt = rtti.find_vtable(*col);
    REQUIRE(vt.has_value());

    // End-to-end correctness check: a live instance of the class must
    // start with a QWORD equal to the resolved vtable address.
    auto instance = std::make_unique<HorizonRttiTestTarget>();
    const void* instance_vptr =
        *reinterpret_cast<const void* const*>(instance.get());
    CHECK(vt->address == instance_vptr);
}

TEST_CASE("MsvcRtti: nullopt for unknown class names") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto td = rtti.find_type_descriptor(".?AVThisClassDoesNotExistAnywhere@@");
    CHECK_FALSE(td.has_value());
}

TEST_CASE("MsvcRtti: trailing-nul anchored search rejects strict prefixes") {
    // If find_type_descriptor matched on substring only, it could find
    // ".?AVHorizonRttiTestTarget@@" when asked for ".?AVHorizonRttiTes"
    // -- which would be a bug. Verify it does NOT match.
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto td = rtti.find_type_descriptor(".?AVHorizonRttiTes");  // missing "tTarget@@\0"
    CHECK_FALSE(td.has_value());
}

TEST_CASE("MsvcRtti: COL+vtable scanning works when given the real TD from a live instance") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    // Walk from a live instance to discover the real TypeDescriptor
    // address (bypassing find_type_descriptor).
    auto instance = std::make_unique<HorizonRttiTestTarget>();
    const auto* vptr =
        *reinterpret_cast<const std::uintptr_t* const*>(instance.get());

    // vtable[-1] holds an absolute pointer to the COL.
    const auto* col_addr = reinterpret_cast<const void*>(*(vptr - 1));
    const auto* col_u32  = reinterpret_cast<const std::uint32_t*>(col_addr);

    INFO("COL address: " << col_addr);
    INFO("COL signature: " << col_u32[0]);
    INFO("COL typeDescriptor RVA: 0x" << std::hex << col_u32[3]);

    REQUIRE(col_u32[0] == 1u);  // x64 sig

    const auto* real_td_addr =
        reinterpret_cast<const void*>(img.base() + col_u32[3]);

    // Verify real_td_addr is within .data (MSVC puts TypeDescriptors
    // there, not .rdata, because the `spare` field is runtime-mutable).
    const auto d_lo = reinterpret_cast<std::uintptr_t>(img.data().data());
    const auto d_hi = d_lo + img.data().size();
    INFO(".data: 0x" << std::hex << d_lo << " - 0x" << d_hi);
    INFO("Real TD addr: " << real_td_addr);
    REQUIRE(reinterpret_cast<std::uintptr_t>(real_td_addr) >= d_lo);
    REQUIRE(reinterpret_cast<std::uintptr_t>(real_td_addr) <  d_hi);

    // Now feed the known-good TD into our COL/vtable scans.
    MsvcRtti rtti(img);
    auto col = rtti.find_complete_object_locator(TypeDescriptor{real_td_addr});
    REQUIRE(col.has_value());
    CHECK(col->address == col_addr);

    auto vt = rtti.find_vtable(*col);
    REQUIRE(vt.has_value());
    CHECK(vt->address == reinterpret_cast<const void*>(vptr));
}

TEST_CASE("MsvcRtti: distinct classes resolve to distinct TypeDescriptors") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    // Reference the second class so MSVC retains its RTTI.
    auto unused = std::make_unique<HorizonRttiUnused>();
    (void)unused;

    MsvcRtti rtti(img);
    auto a = rtti.find_type_descriptor(".?AVHorizonRttiTestTarget@@");
    auto b = rtti.find_type_descriptor(".?AVHorizonRttiUnused@@");
    REQUIRE(a.has_value());
    REQUIRE(b.has_value());
    CHECK(a->address != b->address);
}
