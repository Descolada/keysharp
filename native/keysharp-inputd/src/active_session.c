#include "active_session.h"

#include <dlfcn.h>
#include <errno.h>
#include <stdint.h>
#include <string.h>

#define KSI_SYSTEMD_LIBRARY "libsystemd.so.0"
#define KSI_LOCAL_SEAT "seat0"

static void resolver_clear(ksi_active_session_resolver *resolver)
{
    memset(resolver, 0, sizeof(*resolver));
}

int ksi_active_session_resolver_init(ksi_active_session_resolver *resolver)
{
    void *handle;
    void *symbol;
    const char *error;

    if (resolver == NULL) {
        errno = EINVAL;
        return -1;
    }

    resolver_clear(resolver);
    handle = dlopen(KSI_SYSTEMD_LIBRARY, RTLD_NOW | RTLD_LOCAL);

    if (handle == NULL) {
        errno = ENOSYS;
        return -1;
    }

    (void)dlerror();
    symbol = dlsym(handle, "sd_seat_get_active");
    error = dlerror();

    /* POSIX specifies that dlsym can return a function address. memcpy avoids
     * relying on a non-standard direct conversion which -Wpedantic diagnoses. */
    if (error != NULL || symbol == NULL
        || sizeof(symbol) != sizeof(resolver->seat_get_active)) {
        (void)dlclose(handle);
        errno = ENOSYS;
        return -1;
    }

    memcpy(&resolver->seat_get_active, &symbol,
        sizeof(resolver->seat_get_active));
    resolver->library_handle = handle;
    resolver->owns_library_handle = true;
    return 0;
}

int ksi_active_session_resolver_init_for_test(
    ksi_active_session_resolver *resolver,
    ksi_sd_seat_get_active_fn seat_get_active)
{
    if (resolver == NULL || seat_get_active == NULL) {
        errno = EINVAL;
        return -1;
    }

    resolver_clear(resolver);
    resolver->seat_get_active = seat_get_active;
    return 0;
}

int ksi_active_session_resolver_get_uid(
    const ksi_active_session_resolver *resolver, uid_t *uid_out)
{
    uid_t active_uid = (uid_t)-1;
    int result;

    if (resolver == NULL || uid_out == NULL) {
        errno = EINVAL;
        return -1;
    }

    if (resolver->seat_get_active == NULL) {
        errno = ENOSYS;
        return -1;
    }

    result = resolver->seat_get_active(KSI_LOCAL_SEAT, NULL, &active_uid);

    if (result < 0) {
        int query_error = -result;

        errno = query_error > 0 ? query_error : EIO;
        return -1;
    }

    if (active_uid == (uid_t)-1) {
        errno = ENOENT;
        return -1;
    }

    *uid_out = active_uid;
    return 0;
}

void ksi_active_session_resolver_cleanup(ksi_active_session_resolver *resolver)
{
    if (resolver == NULL) {
        return;
    }

    if (resolver->owns_library_handle && resolver->library_handle != NULL) {
        (void)dlclose(resolver->library_handle);
    }

    resolver_clear(resolver);
}
