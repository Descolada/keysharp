cmake_minimum_required(VERSION 3.16)

project(keysharp-trust
    VERSION 0.1.0
    DESCRIPTION "Shared privileged trust store for Keysharp native helpers"
    LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

add_library(keysharp-trust STATIC
    src/permissions.c)

target_include_directories(keysharp-trust
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include)

target_compile_definitions(keysharp-trust PUBLIC _GNU_SOURCE _POSIX_C_SOURCE=200809L)

if(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(keysharp-trust PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Wconversion
        -Wshadow)
endif()
