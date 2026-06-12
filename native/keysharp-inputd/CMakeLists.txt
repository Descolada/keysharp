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
find_package(Threads REQUIRED)
pkg_check_modules(LIBUDEV REQUIRED IMPORTED_TARGET libudev)
pkg_check_modules(LIBEVDEV REQUIRED IMPORTED_TARGET libevdev)

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../keysharp-trust-lib
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-trust-lib)

add_executable(keysharp-inputd
    src/connection_ref.c
    src/daemon.c
    src/ipc_unix.c
    src/main.c
    src/pipe_ring.c
    src/trust_cli.c
    src/worker_pool.c)

target_include_directories(keysharp-inputd
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include)

target_compile_definitions(keysharp-inputd PRIVATE _GNU_SOURCE _POSIX_C_SOURCE=200809L)
target_link_libraries(keysharp-inputd PRIVATE
    keysharp-trust-lib
    Threads::Threads
    PkgConfig::LIBUDEV
    PkgConfig::LIBEVDEV)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_sources(keysharp-inputd PRIVATE
        src/platform/linux_devices.c
        src/platform/linux_synth.c
        src/platform/platform_linux.c
        src/platform/vk_evdev.c)
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

set(KEYSHARP_INPUTD_SYSTEMD_UNIT_DIR
    "${CMAKE_INSTALL_LIBDIR}/systemd/system"
    CACHE PATH "systemd system unit install directory")

configure_file(
    systemd/keysharp-inputd.service.in
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-inputd.service
    @ONLY)

install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/keysharp-inputd.service
    systemd/keysharp-inputd.socket
    DESTINATION ${KEYSHARP_INPUTD_SYSTEMD_UNIT_DIR})

# Direct root installs finish service setup immediately. Packaging installs
# set DESTDIR and should run the equivalent post-install step themselves.
install(CODE "
    if(\"\$ENV{DESTDIR}\" STREQUAL \"\")
        set(_bin \"\${CMAKE_INSTALL_PREFIX}/${CMAKE_INSTALL_BINDIR}/keysharp-inputd\")
        execute_process(
            COMMAND \"\${_bin}\" --install-input-access
            RESULT_VARIABLE _inputd_setup_result)
        if(NOT _inputd_setup_result EQUAL 0)
            message(WARNING \"keysharp-inputd service setup was not completed; run as root: \${_bin} --install-input-access\")
        endif()
    endif()
")

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
