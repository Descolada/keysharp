cmake_minimum_required(VERSION 3.16)

project(keysharp-screencap
    VERSION 0.1.0
    DESCRIPTION "Privileged screen capture helper for Keysharp"
    LANGUAGES C)

include(GNUInstallDirs)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

find_package(PkgConfig REQUIRED)
pkg_check_modules(GIO REQUIRED IMPORTED_TARGET gio-2.0 gio-unix-2.0)

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../keysharp-common
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-common)

add_executable(keysharp-screencap
    src/main.c)

target_include_directories(keysharp-screencap
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include)

target_compile_definitions(keysharp-screencap PRIVATE _GNU_SOURCE _POSIX_C_SOURCE=200809L)
target_link_libraries(keysharp-screencap PRIVATE
    keysharp-trust-lib
    PkgConfig::GIO)

if(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(keysharp-screencap PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Wconversion
        -Wshadow)
endif()

configure_file(
    keysharp-screencap.desktop.in
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-screencap.desktop
    @ONLY)

set(KEYSHARP_SCREENCAP_DESKTOP_DIR
    "/usr/share/applications"
    CACHE PATH "System application desktop file directory")

install(TARGETS keysharp-screencap RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-screencap.desktop
    DESTINATION ${KEYSHARP_SCREENCAP_DESKTOP_DIR})

install(CODE "
    if(\"\$ENV{DESTDIR}\" STREQUAL \"\")
        set(_bin \"\${CMAKE_INSTALL_PREFIX}/${CMAKE_INSTALL_BINDIR}/keysharp-screencap\")
        if(EXISTS \"\${_bin}\")
            execute_process(COMMAND chown root:root \"\${_bin}\" RESULT_VARIABLE _chown_result)
            execute_process(COMMAND chmod 4755 \"\${_bin}\" RESULT_VARIABLE _chmod_result)
            if(NOT _chown_result EQUAL 0 OR NOT _chmod_result EQUAL 0)
                message(WARNING \"keysharp-screencap privilege setup was not completed; run as root: chown root:root \${_bin} && chmod 4755 \${_bin}\")
            endif()
        endif()
    endif()
")
