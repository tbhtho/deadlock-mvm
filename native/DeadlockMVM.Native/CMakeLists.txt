add_library(DeadlockMVM.Native SHARED
    dllmain.cpp
    engine_movie_audio.cpp
    npc_healthbar_hook.cpp
    pattern_scan.cpp
    replay_camera.cpp
    smvm_overlay.cpp
    smvm_theme.cpp
    smvm_ui.cpp
    tower_fade_override.cpp
    tower_outline_hook.cpp
    world_depth_capture.cpp
    version.rc
)

target_include_directories(DeadlockMVM.Native PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_definitions(DeadlockMVM.Native PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)
target_compile_options(DeadlockMVM.Native PRIVATE /W4 /WX /permissive- /EHsc /guard:cf)
target_link_options(DeadlockMVM.Native PRIVATE /DYNAMICBASE /NXCOMPAT /GUARD:CF)
target_link_libraries(DeadlockMVM.Native PRIVATE imgui libgmavi Advapi32 D3D11 D3DCompiler DXGI Gdi32 Shell32 User32 Ws2_32)

set_target_properties(DeadlockMVM.Native PROPERTIES
    OUTPUT_NAME "DeadlockMVM.Native"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
)
