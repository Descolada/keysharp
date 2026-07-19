#include "active_session.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
        return EXIT_FAILURE; \
    } \
} while (0)

static int query_count;
static const char *observed_seat;
static bool observed_null_session;

static int successful_query(const char *seat, char **session, uid_t *uid)
{
    query_count++;
    observed_seat = seat;
    observed_null_session = session == NULL;
    *uid = (uid_t)1234;
    return 0;
}

static int unavailable_query(const char *seat, char **session, uid_t *uid)
{
    (void)seat;
    (void)session;
    (void)uid;
    return -ENODATA;
}

static int invalid_uid_query(const char *seat, char **session, uid_t *uid)
{
    (void)seat;
    (void)session;
    *uid = (uid_t)-1;
    return 0;
}

int main(void)
{
    ksi_active_session_resolver resolver;
    uid_t uid = (uid_t)77;

    CHECK(ksi_active_session_resolver_init_for_test(
        &resolver, successful_query) == 0);
    CHECK(ksi_active_session_resolver_get_uid(&resolver, &uid) == 0);
    CHECK(uid == (uid_t)1234);
    CHECK(query_count == 1);
    CHECK(observed_seat != NULL);
    CHECK(strcmp(observed_seat, "seat0") == 0);
    CHECK(observed_null_session);

    ksi_active_session_resolver_cleanup(&resolver);
    CHECK(resolver.seat_get_active == NULL);
    CHECK(resolver.library_handle == NULL);
    ksi_active_session_resolver_cleanup(&resolver);

    CHECK(ksi_active_session_resolver_init_for_test(
        &resolver, unavailable_query) == 0);
    errno = 0;
    uid = (uid_t)77;
    CHECK(ksi_active_session_resolver_get_uid(&resolver, &uid) == -1);
    CHECK(errno == ENODATA);
    CHECK(uid == (uid_t)77);
    ksi_active_session_resolver_cleanup(&resolver);

    CHECK(ksi_active_session_resolver_init_for_test(
        &resolver, invalid_uid_query) == 0);
    errno = 0;
    CHECK(ksi_active_session_resolver_get_uid(&resolver, &uid) == -1);
    CHECK(errno == ENOENT);
    ksi_active_session_resolver_cleanup(&resolver);

    errno = 0;
    CHECK(ksi_active_session_resolver_init_for_test(&resolver, NULL) == -1);
    CHECK(errno == EINVAL);

    puts("PASS active seat resolver");
    return EXIT_SUCCESS;
}
