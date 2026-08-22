#pragma once

#include <Windows.h>

namespace deadlock_mvm {

[[noreturn]] void RunReplayCameraBackend(HMODULE self_module) noexcept;

} // namespace deadlock_mvm
