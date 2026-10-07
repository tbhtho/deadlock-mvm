add_library(DeadlockMVM.Native SHARED
    dllmain.cpp
    engine_movie_audio.cpp
    mp4_encoder.cpp
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
target_link_libraries(DeadlockMVM.Native PRIVATE imgui libgmavi Advapi32 D3D11 D3DCompiler DXGI Gdi32 Mfplat Mfreadwrite Mfuuid Ole32 Shell32 User32 Ws2_32)

set_target_properties(DeadlockMVM.Native PROPERTIES
    OUTPUT_NAME "DeadlockMVM.Native"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
)

# Compile project-authored effects at build time; never compile shaders in Present.
file(GLOB SMVM_FXC_CANDIDATES "C:/Program Files (x86)/Windows Kits/10/bin/*/x64/fxc.exe")
list(SORT SMVM_FXC_CANDIDATES COMPARE NATURAL ORDER DESCENDING)
find_program(SMVM_FXC NAMES fxc.exe HINTS ${SMVM_FXC_CANDIDATES})
if(NOT SMVM_FXC AND SMVM_FXC_CANDIDATES)
    list(GET SMVM_FXC_CANDIDATES 0 SMVM_FXC)
endif()
if(NOT SMVM_FXC)
    message(FATAL_ERROR "Windows SDK fxc.exe is required for in-house Reshade shaders")
endif()
foreach(stage IN ITEMS vs ps)
    if(stage STREQUAL "vs")
        set(entry VSMain)
    else()
        set(entry PSMain)
    endif()
    add_custom_command(OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/look_${stage}.h"
        COMMAND "${SMVM_FXC}" /nologo /WX /O3 /T ${stage}_5_0 /E ${entry} /Vn g_look_${stage}
            /Fh "${CMAKE_CURRENT_BINARY_DIR}/look_${stage}.h" "${CMAKE_CURRENT_SOURCE_DIR}/look_pipeline.hlsl"
        DEPENDS look_pipeline.hlsl VERBATIM)
endforeach()
target_sources(DeadlockMVM.Native PRIVATE look_pipeline.cpp)
target_include_directories(DeadlockMVM.Native PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
foreach(stage IN ITEMS cs ps)
    if(stage STREQUAL "cs")
        set(entry CompareCS)
    else()
        set(entry RecoverPS)
    endif()
    add_custom_command(OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/look_fresh_${stage}.h"
        COMMAND "${SMVM_FXC}" /nologo /WX /O3 /T ${stage}_5_0 /E ${entry} /Vn g_look_fresh_${stage}
            /Fh "${CMAKE_CURRENT_BINARY_DIR}/look_fresh_${stage}.h" "${CMAKE_CURRENT_SOURCE_DIR}/look_freshness.hlsl"
        DEPENDS look_freshness.hlsl VERBATIM)
endforeach()


set(SMVM_LOOK_GENERATED "${CMAKE_CURRENT_BINARY_DIR}/look_vs.h" "${CMAKE_CURRENT_BINARY_DIR}/look_ps.h" "${CMAKE_CURRENT_BINARY_DIR}/look_fresh_cs.h" "${CMAKE_CURRENT_BINARY_DIR}/look_fresh_ps.h")
# One producer avoids parallel DLL/test shader-generation races.
add_custom_target(SmvmLookShaders DEPENDS ${SMVM_LOOK_GENERATED})
add_dependencies(DeadlockMVM.Native SmvmLookShaders)
