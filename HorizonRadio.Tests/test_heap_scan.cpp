#include <algorithm>
#include <doctest/doctest.h>
#include <horizon/inject/heap_scan.hpp>
#include <horizon/inject/msvc_rtti.hpp>
#include <memory>
#include <vector>
#include <windows.h>

using namespace horizon::inject;

// A polymorphic class so MSVC emits RTTI for it; the first QWORD of
// every instance is the vtable pointer that find_heap_instances will
// look for.
class HorizonHeapScanTarget {
public:
    virtual ~HorizonHeapScanTarget() = default;
    virtual int identifier() const {
        return 0xBEE;
    }
    // Padding fields so a candidate match at the vtable position is
    // less likely to be coincidence in a small test exe.
    std::uint64_t pad_a = 0xCAFE'BABE'1234'5678ull;
    std::uint64_t pad_b = 0xDEAD'BEEF'F00D'D00Dull;
};

TEST_CASE("find_heap_instances locates live instances by their vtable") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto     td = rtti.find_type_descriptor(".?AVHorizonHeapScanTarget@@");
    REQUIRE(td.has_value());
    auto col = rtti.find_complete_object_locator(*td);
    REQUIRE(col.has_value());
    auto vt = rtti.find_vtable(*col);
    REQUIRE(vt.has_value());

    auto a = std::make_unique<HorizonHeapScanTarget>();
    auto b = std::make_unique<HorizonHeapScanTarget>();
    auto c = std::make_unique<HorizonHeapScanTarget>();

    auto instances = find_heap_instances(*vt);

    auto contains = [&](const void* p) {
        return std::find(instances.begin(), instances.end(), p) != instances.end();
    };
    CHECK(contains(a.get()));
    CHECK(contains(b.get()));
    CHECK(contains(c.get()));
}

TEST_CASE("find_heap_instances completes without crashing on a clean target") {
    // Sanity: the scanner walks the entire process address space. As
    // long as it terminates and returns a vector, the VirtualQuery
    // loop and region filters are sound. False positives are expected
    // (shared_ptr control blocks, freed-but-unpoisoned memory, CRT
    // internals that happen to hold the vtable address) -- the other
    // tests cover positive identification by checking instance
    // addresses we know we created.
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto     td = rtti.find_type_descriptor(".?AVHorizonHeapScanTarget@@");
    REQUIRE(td.has_value());
    auto col = rtti.find_complete_object_locator(*td);
    REQUIRE(col.has_value());
    auto vt = rtti.find_vtable(*col);
    REQUIRE(vt.has_value());

    auto instances = find_heap_instances(*vt);
    CHECK(instances.size() < 100000); // sanity: scanner didn't run away
}

TEST_CASE("find_heap_instances finds multiple instances under shared_ptr") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MsvcRtti rtti(img);
    auto     td = rtti.find_type_descriptor(".?AVHorizonHeapScanTarget@@");
    REQUIRE(td.has_value());
    auto col = rtti.find_complete_object_locator(*td);
    REQUIRE(col.has_value());
    auto vt = rtti.find_vtable(*col);
    REQUIRE(vt.has_value());

    std::vector<std::shared_ptr<HorizonHeapScanTarget>> kept;
    for (int i = 0; i < 5; ++i) {
        kept.push_back(std::make_shared<HorizonHeapScanTarget>());
    }

    auto instances = find_heap_instances(*vt);
    int  found     = 0;
    for (const auto& sp : kept) {
        if (std::find(instances.begin(), instances.end(), sp.get()) != instances.end()) {
            ++found;
        }
    }
    CHECK(found == 5);
}
