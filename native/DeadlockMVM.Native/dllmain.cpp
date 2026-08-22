#include "replay_camera.hpp"

#include <Windows.h>

namespace {

DWORD WINAPI BackendThread(void* parameter) noexcept {
    deadlock_mvm::RunReplayCameraBackend(static_cast<HMODULE>(parameter));
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    DisableThreadLibraryCalls(instance);
    const auto thread = CreateThread(nullptr, 0, BackendThread, instance, 0, nullptr);
    if (thread == nullptr)
        return FALSE;
    CloseHandle(thread);
    return TRUE;
}
