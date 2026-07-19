#pragma once

#include <stdbool.h>
#include <sys/types.h>

/* Runtime signature of sd_seat_get_active(3). Keeping the dependency behind a
 * function pointer lets keysharp-inputd build without libsystemd headers or an
 * unversioned libsystemd linker name. */
typedef int (*ksi_sd_seat_get_active_fn)(
    const char *seat, char **session, uid_t *uid);

typedef struct ksi_active_session_resolver {
    void *library_handle;
    ksi_sd_seat_get_active_fn seat_get_active;
    bool owns_library_handle;
} ksi_active_session_resolver;

/* Load sd_seat_get_active from libsystemd.so.0. Returns -1, with errno set, if
 * logind support is unavailable. The resolver must not already be initialized.
 */
int ksi_active_session_resolver_init(ksi_active_session_resolver *resolver);

/* Inject the query operation without loading libsystemd. Intended for isolated
 * tests; production callers should use ksi_active_session_resolver_init(). */
int ksi_active_session_resolver_init_for_test(
    ksi_active_session_resolver *resolver,
    ksi_sd_seat_get_active_fn seat_get_active);

/* Resolve the UID of the active session on the local seat named "seat0".
 * Returns zero only when logind supplied a valid UID. Every other result is a
 * closed gate: -1 is returned and errno describes the failure.
 *
 * Query calls may run concurrently after successful initialization because the
 * resolver is immutable. Initialization and cleanup require exclusive access;
 * cleanup must not race with a query. */
int ksi_active_session_resolver_get_uid(
    const ksi_active_session_resolver *resolver, uid_t *uid_out);

/* Release the runtime library, if owned, and clear the resolver. Safe after a
 * failed initialization and safe to call more than once. */
void ksi_active_session_resolver_cleanup(ksi_active_session_resolver *resolver);
