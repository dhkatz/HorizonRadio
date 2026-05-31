// Single translation unit that defines doctest's main(). All other test
// files just #include <doctest/doctest.h> without the IMPLEMENT macro.
//
// Side mode `--bin-pipe <file>`: read <file> in binary mode and write
// its bytes verbatim to stdout (also in binary mode), then exit. Used
// by test_subprocess_source.cpp as the child it spawns — the test exe
// is a stand-in for librespot, giving us a controlled binary
// producer without depending on a real Spotify Connect setup in CI.

#define DOCTEST_CONFIG_IMPLEMENT
#include <cstdio>
#include <cstdlib>
#include <doctest/doctest.h>
#include <fcntl.h>
#include <io.h>
#include <string>
#include <vector>

namespace {

int run_bin_pipe(const char* path) {
    // Switch stdout to binary mode so the CRT doesn't translate LF
    // to CRLF on our way out — fatal for binary PCM.
    if (_setmode(_fileno(stdout), _O_BINARY) == -1)
        return 2;

    std::FILE* fp = nullptr;
    if (fopen_s(&fp, path, "rb") != 0 || fp == nullptr)
        return 3;

    std::vector<unsigned char> buf(8192);
    while (true) {
        const auto got = std::fread(buf.data(), 1, buf.size(), fp);
        if (got == 0)
            break;
        const auto wrote = std::fwrite(buf.data(), 1, got, stdout);
        if (wrote != got) {
            std::fclose(fp);
            return 4;
        }
    }
    std::fclose(fp);
    std::fflush(stdout);
    return 0;
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 3 && std::string(argv[1]) == "--bin-pipe") {
        return run_bin_pipe(argv[2]);
    }
    doctest::Context ctx(argc, argv);
    return ctx.run();
}
