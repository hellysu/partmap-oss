#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <filesystem>
#include <string>

static void add_plugin_dir(const char* plugin_path) {
    auto dir = std::filesystem::path(plugin_path).parent_path();
    if (!dir.empty()) SetDllDirectoryW(dir.wstring().c_str());
}
static void* sym(HMODULE h, const char* name) {
    return reinterpret_cast<void*>(GetProcAddress(h, name));
}
int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: %s <bambu_networking.dll>\n", argv[0]);
        return 2;
    }
    add_plugin_dir(argv[1]);
    HMODULE h = LoadLibraryA(argv[1]);
    if (!h) {
        std::fprintf(stderr, "LoadLibrary failed: %lu\n", (unsigned long)GetLastError());
        return 3;
    }
    using fn_ver = std::string (*)();
    using fn_dbg = bool (*)(bool);
    using fn_create = void* (*)(std::string);
    using fn_destroy = int (*)(void*);
    using fn_set_dir = int (*)(void*, std::string);

    auto ver = reinterpret_cast<fn_ver>(sym(h, "bambu_network_get_version"));
    auto dbg = reinterpret_cast<fn_dbg>(sym(h, "bambu_network_check_debug_consistent"));
    auto create_a = reinterpret_cast<fn_create>(sym(h, "bambu_network_create_agent"));
    auto destroy_a = reinterpret_cast<fn_destroy>(sym(h, "bambu_network_destroy_agent"));
    auto set_dir = reinterpret_cast<fn_set_dir>(sym(h, "bambu_network_set_config_dir"));
    if (!ver || !dbg || !create_a || !destroy_a || !set_dir) {
        std::fprintf(stderr, "required export missing\n");
        FreeLibrary(h);
        return 4;
    }
    const std::string version = ver();
    std::printf("version=%s\n", version.c_str());
    std::printf("debug_false=%d debug_true=%d\n", dbg(false) ? 1 : 0, dbg(true) ? 1 : 0);
    void* agent = create_a(std::string("."));
    std::printf("agent=%s\n", agent ? "ok" : "null");
    if (!agent) { FreeLibrary(h); return 5; }
    int rc = set_dir(agent, std::string("."));
    std::printf("set_config_dir_rc=%d\n", rc);
    int drc = destroy_a(agent);
    std::printf("destroy_rc=%d\n", drc);
    FreeLibrary(h);
    return (rc == 0 && drc == 0) ? 0 : 6;
}
