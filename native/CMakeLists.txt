cmake_minimum_required(VERSION 3.25)
project(DeadlockMVMNative VERSION 1.0.0 LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "DeadlockMVM.Native requires 64-bit Windows")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_subdirectory(third_party)
add_subdirectory(DeadlockMVM.Native)
