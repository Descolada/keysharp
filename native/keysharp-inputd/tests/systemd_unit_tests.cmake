if(NOT DEFINED KSI_SOCKET_UNIT OR NOT EXISTS "${KSI_SOCKET_UNIT}")
    message(FATAL_ERROR "KSI_SOCKET_UNIT does not name an existing socket unit")
endif()

if(NOT DEFINED KSI_SERVICE_UNIT OR NOT EXISTS "${KSI_SERVICE_UNIT}")
    message(FATAL_ERROR "KSI_SERVICE_UNIT does not name an existing service unit")
endif()

file(READ "${KSI_SOCKET_UNIT}" socket_unit)
file(READ "${KSI_SERVICE_UNIT}" service_unit)

string(REGEX MATCHALL "ListenStream[ \t]*=" socket_listeners "${socket_unit}")
list(LENGTH socket_listeners socket_listener_count)

if(NOT socket_listener_count EQUAL 1)
    message(FATAL_ERROR
        "keysharp-inputd.socket must declare exactly one ListenStream; found ${socket_listener_count}")
endif()

if(NOT socket_unit MATCHES
    "ListenStream[ \t]*=[ \t]*/run/keysharp-inputd/keysharp-inputd\\.sock")
    message(FATAL_ERROR "keysharp-inputd.socket does not own the expected single endpoint")
endif()

if(NOT socket_unit MATCHES "\\[Install\\]"
    OR NOT socket_unit MATCHES "WantedBy[ \t]*=[ \t]*sockets\\.target")
    message(FATAL_ERROR
        "keysharp-inputd.socket must remain disableable for clean upgrades")
endif()

if(NOT service_unit MATCHES
    "Requires[ \t]*=[ \t]*keysharp-inputd\\.socket")
    message(FATAL_ERROR
        "keysharp-inputd.service must require its socket activation unit")
endif()

if(NOT service_unit MATCHES "\\[Install\\]"
    OR NOT service_unit MATCHES "WantedBy[ \t]*=[ \t]*multi-user\\.target")
    message(FATAL_ERROR
        "keysharp-inputd.service must be the sole multi-user boot entry point")
endif()

message(STATUS "keysharp-inputd systemd unit topology passed")
