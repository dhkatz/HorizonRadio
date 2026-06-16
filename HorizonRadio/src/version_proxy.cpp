// version.dll proxy forwarders: thin trampolines that lazy-load the real
// C:\Windows\System32\version.dll and call through, so FH6 (which loads us as
// version.dll) doesn't crash on a missing export. Why trampolines over PE
// forwarders, and the per-compiler export mechanism: docs/architecture.md ->
// "version.dll proxy".

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <cwchar>
#include <windows.h>
#include <winver.h>

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4273)
#elif defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Winconsistent-dllimport"
#endif

// Input-string params: MSVC's winver.h uses LPCSTR/LPCWSTR, mingw-w64's uses
// LPSTR/LPWSTR. Our definitions must match the prior declaration exactly.
#if defined(_MSC_VER) && !defined(__clang__)
using VER_INPUT_STR  = LPCSTR;
using VER_INPUT_WSTR = LPCWSTR;
#else
using VER_INPUT_STR  = LPSTR;
using VER_INPUT_WSTR = LPWSTR;
#endif

namespace {

// Lazy-load the real version.dll once. System dir resolved at runtime so this
// works on installs that aren't on C:.
HMODULE real_version_dll() {
    static HMODULE h = []() -> HMODULE {
        wchar_t    path[MAX_PATH];
        const UINT n = GetSystemDirectoryW(path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH - 12)
            return nullptr;
        std::wmemcpy(path + n, L"\\version.dll", 13); // includes trailing NUL
        return LoadLibraryW(path);
    }();
    return h;
}

template <typename Fn> Fn resolve(const char* name) {
    HMODULE h = real_version_dll();
    if (!h)
        return nullptr;
    return reinterpret_cast<Fn>(GetProcAddress(h, name));
}

} // namespace

// One trampoline per export: forward to the resolved real function (cached),
// returning `ret_t{}` on resolve failure. HZN_EXPORT/HZN_EXPORT_NAME select the
// per-compiler export mechanism (dllexport on clang+MinGW, /EXPORT: pragma on
// MSVC) -- see docs/architecture.md.
#if defined(_MSC_VER) && !defined(__clang__)
#define HZN_EXPORT
#define HZN_EXPORT_NAME(name) __pragma(comment(linker, "/EXPORT:" #name))
#else
#define HZN_EXPORT __declspec(dllexport)
#define HZN_EXPORT_NAME(name)
#endif

// NOLINTBEGIN(bugprone-macro-parentheses) -- ret_t is a type-name argument;
// parenthesizing it (the check's suggested fix) is not valid C++ here.
#define HZN_VERSION_PROXY(ret_t, name, params, args)                                                                   \
    extern "C" HZN_EXPORT ret_t WINAPI name params {                                                                   \
        using Fn      = ret_t(WINAPI*) params;                                                                         \
        static Fn ptr = resolve<Fn>(#name);                                                                            \
        if (!ptr)                                                                                                      \
            return ret_t{};                                                                                            \
        return ptr args;                                                                                               \
    }                                                                                                                  \
    HZN_EXPORT_NAME(name)
// NOLINTEND(bugprone-macro-parentheses)

// Documented (winver.h) exports.
HZN_VERSION_PROXY(BOOL, GetFileVersionInfoA, (LPCSTR lp, DWORD h, DWORD len, LPVOID data), (lp, h, len, data))
HZN_VERSION_PROXY(BOOL, GetFileVersionInfoW, (LPCWSTR lp, DWORD h, DWORD len, LPVOID data), (lp, h, len, data))
HZN_VERSION_PROXY(BOOL, GetFileVersionInfoExA, (DWORD flags, LPCSTR lp, DWORD h, DWORD len, LPVOID data),
                  (flags, lp, h, len, data))
HZN_VERSION_PROXY(BOOL, GetFileVersionInfoExW, (DWORD flags, LPCWSTR lp, DWORD h, DWORD len, LPVOID data),
                  (flags, lp, h, len, data))
HZN_VERSION_PROXY(DWORD, GetFileVersionInfoSizeA, (LPCSTR lp, LPDWORD out), (lp, out))
HZN_VERSION_PROXY(DWORD, GetFileVersionInfoSizeW, (LPCWSTR lp, LPDWORD out), (lp, out))
HZN_VERSION_PROXY(DWORD, GetFileVersionInfoSizeExA, (DWORD flags, LPCSTR lp, LPDWORD out), (flags, lp, out))
HZN_VERSION_PROXY(DWORD, GetFileVersionInfoSizeExW, (DWORD flags, LPCWSTR lp, LPDWORD out), (flags, lp, out))
HZN_VERSION_PROXY(DWORD, VerFindFileA,
                  (DWORD flags, VER_INPUT_STR n, VER_INPUT_STR wdir, VER_INPUT_STR adir, LPSTR cur, PUINT cln,
                   LPSTR out, PUINT oln),
                  (flags, n, wdir, adir, cur, cln, out, oln))
HZN_VERSION_PROXY(DWORD, VerFindFileW,
                  (DWORD flags, VER_INPUT_WSTR n, VER_INPUT_WSTR wdir, VER_INPUT_WSTR adir, LPWSTR cur, PUINT cln,
                   LPWSTR out, PUINT oln),
                  (flags, n, wdir, adir, cur, cln, out, oln))
HZN_VERSION_PROXY(DWORD, VerInstallFileA,
                  (DWORD f, VER_INPUT_STR src, VER_INPUT_STR dest, VER_INPUT_STR sdir, VER_INPUT_STR cdir,
                   VER_INPUT_STR tdir, LPSTR out, PUINT oln),
                  (f, src, dest, sdir, cdir, tdir, out, oln))
HZN_VERSION_PROXY(DWORD, VerInstallFileW,
                  (DWORD f, VER_INPUT_WSTR src, VER_INPUT_WSTR dest, VER_INPUT_WSTR sdir, VER_INPUT_WSTR cdir,
                   VER_INPUT_WSTR tdir, LPWSTR out, PUINT oln),
                  (f, src, dest, sdir, cdir, tdir, out, oln))
HZN_VERSION_PROXY(DWORD, VerLanguageNameA, (DWORD lang, LPSTR buf, DWORD cch), (lang, buf, cch))
HZN_VERSION_PROXY(DWORD, VerLanguageNameW, (DWORD lang, LPWSTR buf, DWORD cch), (lang, buf, cch))
HZN_VERSION_PROXY(BOOL, VerQueryValueA, (LPCVOID block, LPCSTR sub, LPVOID* out, PUINT pcb), (block, sub, out, pcb))
HZN_VERSION_PROXY(BOOL, VerQueryValueW, (LPCVOID block, LPCWSTR sub, LPVOID* out, PUINT pcb), (block, sub, out, pcb))

// GetFileVersionInfoByHandle is undocumented and not in winver.h, so
// there's no conflicting dllimport declaration — plain dllexport would
// compile on MSVC here. We still route it through HZN_EXPORT /
// HZN_EXPORT_NAME to keep one export mechanism per compiler. Pass-through
// signature follows the ordinal-13 export shape used by the OS shim layer.
extern "C" HZN_EXPORT DWORD WINAPI GetFileVersionInfoByHandle(INT a, HANDLE b, DWORD c, LPVOID d) {
    using Fn      = DWORD(WINAPI*)(INT, HANDLE, DWORD, LPVOID);
    static Fn ptr = resolve<Fn>("GetFileVersionInfoByHandle");
    if (!ptr)
        return 0;
    return ptr(a, b, c, d);
}
HZN_EXPORT_NAME(GetFileVersionInfoByHandle)

#undef HZN_VERSION_PROXY
#undef HZN_EXPORT
#undef HZN_EXPORT_NAME

#ifdef _MSC_VER
#pragma warning(pop)
#elif defined(__clang__)
#pragma clang diagnostic pop
#endif
