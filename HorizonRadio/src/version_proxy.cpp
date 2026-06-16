// version.dll proxy forwarders.
//
// The game loads this DLL as `version.dll` from its own directory
// (Windows search order finds it before the system copy). Every
// export of the real C:\Windows\System32\version.dll must therefore
// be present here, or the game crashes during its module init. We
// implement each as a thin trampoline that lazy-loads the real OS
// version.dll and calls through.
//
// Why trampolines instead of PE forwarder records (the historical
// `#pragma comment(linker, "/export:Name=path.Name")` approach):
// path-qualified forwarders are MSVC linker syntax — clang in MinGW
// target mode silently drops them, and `.def` EXPORTS without a path
// would recurse on our own DLL. Trampolines compile under both
// toolchains, removing the cross-compile blocker.
//
// How each trampoline is exported differs by compiler, because
// <winver.h> declares all 16 documented entry points with WINBASEAPI
// (dllimport):
//   - clang+MinGW: define them `__declspec(dllexport)`. MinGW only
//     warns (-Winconsistent-dllimport) about the dllimport→dllexport
//     mismatch and still exports our implementation. This is the path
//     the Linux/macOS cross build relies on.
//   - MSVC: the same mismatch is a hard error (C2375, "redefinition;
//     different linkage" — not a suppressible warning), so we instead
//     define them with normal linkage (which matches the header and
//     compiles clean) and export each via
//     `#pragma comment(linker, "/EXPORT:Name")`. No `=path`, so it
//     exports our trampoline rather than forwarding — no recursion.

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

// MSVC's winver.h declares VerFindFile* / VerInstallFile* with
// LPCSTR / LPCWSTR for the input-string params; mingw-w64's declares
// them with LPSTR / LPWSTR (no const). C++ requires our definitions
// to match the prior declarations exactly, so we pick the right
// spelling per compiler.
#if defined(_MSC_VER) && !defined(__clang__)
using VER_INPUT_STR  = LPCSTR;
using VER_INPUT_WSTR = LPCWSTR;
#else
using VER_INPUT_STR  = LPSTR;
using VER_INPUT_WSTR = LPWSTR;
#endif

namespace {

// Lazy-load the real C:\Windows\System32\version.dll once. The
// system-directory path is read at runtime via GetSystemDirectoryW so
// the proxy still works on Windows installs that aren't on C:.
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

// One macro per trampoline. Defines a function whose name and
// signature match a real version.dll export, looks the real one up
// once via GetProcAddress, and forwards. The static caches the
// resolved pointer.
//
// Return type is taken verbatim — for BOOL functions, returning
// `Ret{}` (zero) on failure mirrors the real version.dll's behavior
// when given a bad input.
//
// HZN_EXPORT / HZN_EXPORT_NAME select the per-compiler export
// mechanism described in the file header: dllexport on clang+MinGW,
// normal linkage plus a `/EXPORT:` linker pragma on MSVC.
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
