add_executable(DeadlockMVM.Native.Tests
    tests.cpp
    ../DeadlockMVM.Native/pattern_scan.cpp
)

target_include_directories(DeadlockMVM.Native.Tests PRIVATE ../DeadlockMVM.Native)
target_compile_definitions(DeadlockMVM.Native.Tests PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)
target_compile_options(DeadlockMVM.Native.Tests PRIVATE /W4 /WX /permissive- /EHsc)

add_test(NAME DeadlockMVM.Native.Tests COMMAND DeadlockMVM.Native.Tests)
