cmake_minimum_required(VERSION 3.25)
project(DeadlockMVMNative LANGUAGES CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "DeadlockMVM.Native requires 64-bit Windows")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_subdirectory(DeadlockMVM.Native)
enable_testing()
add_subdirectory(DeadlockMVM.Native.Tests)
