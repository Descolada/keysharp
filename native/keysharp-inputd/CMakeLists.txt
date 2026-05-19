cmake_minimum_required(VERSION 3.16)

project(keysharp-inputd
    VERSION 0.1.0
    DESCRIPTION "Privileged input broker daemon for Keysharp"
    LANGUAGES C)

include(GNUInstallDirs)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBUDEV REQUIRED IMPORTED_TARGET libudev)
pkg_check_modules(LIBEVDEV REQUIRED IMPORTED_TARGET libevdev)

add_executable(keysharp-inputd
    src/daemon.c
    src/ipc_unix.c
    src/main.c)

target_include_directories(keysharp-inputd
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include)

target_compile_definitions(keysharp-inputd PRIVATE _GNU_SOURCE _POSIX_C_SOURCE=200809L)
target_link_libraries(keysharp-inputd PRIVATE
    PkgConfig::LIBUDEV
    PkgConfig::LIBEVDEV)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_sources(keysharp-inputd PRIVATE
        src/platform/linux_devices.c
        src/platform/linux_synth.c
        src/platform/platform_linux.c)
    target_compile_definitions(keysharp-inputd PRIVATE KSI_PLATFORM_LINUX=1)
else()
    message(FATAL_ERROR "keysharp-inputd currently supports Linux only")
endif()

if(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(keysharp-inputd PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Wconversion
        -Wshadow)
endif()

install(TARGETS keysharp-inputd RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})

# After installing, make the daemon setgid 'input' so it can access
# /dev/input/* and /dev/uinput without the launching user being in
# the input group. Requires cmake --install to be run as root.
install(CODE "
    set(_bin \"\$ENV{DESTDIR}\${CMAKE_INSTALL_PREFIX}/${CMAKE_INSTALL_BINDIR}/keysharp-inputd\")
    execute_process(
        COMMAND getent group input
        RESULT_VARIABLE _has_input_group
        OUTPUT_QUIET ERROR_QUIET)
    if(_has_input_group EQUAL 0)
        execute_process(COMMAND chown root:input \"\${_bin}\")
        execute_process(COMMAND chmod g+s \"\${_bin}\")
        message(STATUS \"keysharp-inputd: setgid input applied\")
    else()
        message(WARNING \"keysharp-inputd: 'input' group not found; run manually after install:\")
        message(WARNING \"  sudo chown root:input \${_bin}\")
        message(WARNING \"  sudo chmod g+s \${_bin}\")
    endif()
")

# Development helper: apply setgid input directly in the build directory.
# Usage: cmake --build build --target dev-permissions
add_custom_target(dev-permissions
    COMMAND sudo chown root:input "$<TARGET_FILE:keysharp-inputd>"
    COMMAND sudo chmod g+s "$<TARGET_FILE:keysharp-inputd>"
    DEPENDS keysharp-inputd
    VERBATIM
    COMMENT "Grant setgid input on keysharp-inputd in the build tree (requires sudo)")

option(KEYSHARP_INPUTD_BUILD_TOOLS "Build keysharp-inputd local test tools" ON)

if(KEYSHARP_INPUTD_BUILD_TOOLS)
    add_executable(keysharp-inputd-client
        tools/client.c)

    add_executable(keysharp-inputd-hooktest
        tools/hooktest.c)

    target_include_directories(keysharp-inputd-client
        PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/include)

    target_include_directories(keysharp-inputd-hooktest
        PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/include)

    target_compile_definitions(keysharp-inputd-client PRIVATE _GNU_SOURCE _POSIX_C_SOURCE=200809L)
    target_compile_definitions(keysharp-inputd-hooktest PRIVATE _GNU_SOURCE _POSIX_C_SOURCE=200809L)

    if(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
        target_compile_options(keysharp-inputd-client PRIVATE
            -Wall
            -Wextra
            -Wpedantic
            -Wconversion
            -Wshadow)
        target_compile_options(keysharp-inputd-hooktest PRIVATE
            -Wall
            -Wextra
            -Wpedantic
            -Wconversion
            -Wshadow)
    endif()
endif()
