# Dear ImGui (vendored, MIT — see third_party/imgui/LICENSE.txt).
# Built without /WX: third-party warnings must not fail the strict product build.
add_library(imgui STATIC
    imgui/imgui.cpp
    imgui/imgui_draw.cpp
    imgui/imgui_tables.cpp
    imgui/imgui_widgets.cpp
    imgui/backends/imgui_impl_dx11.cpp
)

target_include_directories(imgui PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/imgui
    ${CMAKE_CURRENT_SOURCE_DIR}/imgui/backends
)

target_compile_definitions(imgui PUBLIC
    IMGUI_DISABLE_OBSOLETE_FUNCTIONS
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(imgui PRIVATE /W3 /EHsc)
set_target_properties(imgui PROPERTIES POSITION_INDEPENDENT_CODE ON)
