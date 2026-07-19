#include "connection_ref.h"

#include <errno.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
        return false; \
    } \
} while (0)

#define WAIT_MS 3000

typedef struct turn_group {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    unsigned int active;
    unsigned int max_active;
    uint64_t order[16];
    size_t order_count;
} turn_group;

typedef struct turn_task {
    turn_group *group;
    ksi_hook_send_ref *ref;
    uint64_t event_id;
    bool recursive;
    bool started;
    bool entered;
    bool release;
    bool finished;
} turn_task;

typedef struct pump_end_task {
    turn_group *group;
    ksi_hook_send_ref *ref;
    uint64_t event_id;
    bool started;
    bool ended;
    bool finished;
} pump_end_task;

static struct timespec deadline_after_ms(int milliseconds)
{
    struct timespec deadline;

    (void)clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += milliseconds / 1000;
    deadline.tv_nsec += (long)(milliseconds % 1000) * 1000000L;

    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    return deadline;
}

static bool group_init(turn_group *group)
{
    memset(group, 0, sizeof(*group));

    if (pthread_mutex_init(&group->mutex, NULL) != 0) {
        return false;
    }

    if (pthread_cond_init(&group->changed, NULL) != 0) {
        pthread_mutex_destroy(&group->mutex);
        return false;
    }

    return true;
}

static void group_destroy(turn_group *group)
{
    pthread_cond_destroy(&group->changed);
    pthread_mutex_destroy(&group->mutex);
}

static bool wait_flag(
    turn_group *group, const bool *flag, bool expected, int milliseconds)
{
    struct timespec deadline = deadline_after_ms(milliseconds);
    bool matched;

    pthread_mutex_lock(&group->mutex);

    while (*flag != expected) {
        int rc = pthread_cond_timedwait(
            &group->changed, &group->mutex, &deadline);

        if (rc == ETIMEDOUT || rc != 0) {
            break;
        }
    }

    matched = *flag == expected;
    pthread_mutex_unlock(&group->mutex);
    return matched;
}

static void release_task(turn_task *task)
{
    pthread_mutex_lock(&task->group->mutex);
    task->release = true;
    pthread_cond_broadcast(&task->group->changed);
    pthread_mutex_unlock(&task->group->mutex);
}

static void *run_turn(void *argument)
{
    turn_task *task = argument;
    bool entered;

    pthread_mutex_lock(&task->group->mutex);
    task->started = true;
    pthread_cond_broadcast(&task->group->changed);
    pthread_mutex_unlock(&task->group->mutex);

    entered = hook_send_ref_wait_event_turn(
        task->ref, task->event_id, task->recursive, WAIT_MS);

    pthread_mutex_lock(&task->group->mutex);
    task->entered = entered;

    if (entered) {
        task->group->active++;
        if (task->group->active > task->group->max_active) {
            task->group->max_active = task->group->active;
        }
        task->group->order[task->group->order_count++] = task->event_id;
    }

    pthread_cond_broadcast(&task->group->changed);

    while (entered && !task->release) {
        (void)pthread_cond_wait(&task->group->changed, &task->group->mutex);
    }

    if (entered) {
        task->group->active--;
    }

    pthread_mutex_unlock(&task->group->mutex);

    if (entered) {
        hook_send_ref_complete_event(task->ref, task->event_id);
    }

    pthread_mutex_lock(&task->group->mutex);
    task->finished = true;
    pthread_cond_broadcast(&task->group->changed);
    pthread_mutex_unlock(&task->group->mutex);
    return NULL;
}

static void *run_pump_end(void *argument)
{
    pump_end_task *task = argument;

    pthread_mutex_lock(&task->group->mutex);
    task->started = true;
    pthread_cond_broadcast(&task->group->changed);
    pthread_mutex_unlock(&task->group->mutex);

    task->ended = hook_send_ref_end_pump(task->ref, task->event_id);

    pthread_mutex_lock(&task->group->mutex);
    task->finished = true;
    pthread_cond_broadcast(&task->group->changed);
    pthread_mutex_unlock(&task->group->mutex);
    return NULL;
}

static void close_ref(ksi_hook_send_ref *ref, int peer_fd)
{
    hook_send_ref_invalidate(ref);
    hook_send_ref_release(ref);
    close(peer_fd);
}

static bool test_one_hook_stream_serializes_keyboard_and_mouse(void)
{
    int sockets[2];
    pthread_t first_thread;
    pthread_t second_thread;
    turn_group group;
    turn_task first = { 0 };
    turn_task second = { 0 };
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(group_init(&group));
    first = (turn_task) { .group = &group, .ref = ref, .event_id = 1u };
    second = (turn_task) { .group = &group, .ref = ref, .event_id = 2u };

    CHECK(pthread_create(&first_thread, NULL, run_turn, &first) == 0);
    CHECK(wait_flag(&group, &first.entered, true, WAIT_MS));
    CHECK(pthread_create(&second_thread, NULL, run_turn, &second) == 0);
    CHECK(wait_flag(&group, &second.started, true, WAIT_MS));
    CHECK(!wait_flag(&group, &second.entered, true, 100));

    release_task(&first);
    CHECK(wait_flag(&group, &second.entered, true, WAIT_MS));
    release_task(&second);
    CHECK(pthread_join(first_thread, NULL) == 0);
    CHECK(pthread_join(second_thread, NULL) == 0);
    CHECK(group.max_active == 1u);
    CHECK(group.order_count == 2u);
    CHECK(group.order[0] == 1u && group.order[1] == 2u);

    group_destroy(&group);
    close_ref(ref, sockets[1]);
    return true;
}

static bool test_separate_hook_streams_run_in_parallel(void)
{
    int first_sockets[2];
    int second_sockets[2];
    pthread_t first_thread;
    pthread_t second_thread;
    turn_group group;
    turn_task first = { 0 };
    turn_task second = { 0 };
    ksi_hook_send_ref *first_ref;
    ksi_hook_send_ref *second_ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, first_sockets) == 0);
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, second_sockets) == 0);
    CHECK((first_ref = hook_send_ref_create(first_sockets[0])) != NULL);
    CHECK((second_ref = hook_send_ref_create(second_sockets[0])) != NULL);
    CHECK(group_init(&group));
    first = (turn_task) { .group = &group, .ref = first_ref, .event_id = 11u };
    second = (turn_task) { .group = &group, .ref = second_ref, .event_id = 12u };

    CHECK(pthread_create(&first_thread, NULL, run_turn, &first) == 0);
    CHECK(pthread_create(&second_thread, NULL, run_turn, &second) == 0);
    CHECK(wait_flag(&group, &first.entered, true, WAIT_MS));
    CHECK(wait_flag(&group, &second.entered, true, WAIT_MS));
    CHECK(group.max_active == 2u);

    release_task(&first);
    release_task(&second);
    CHECK(pthread_join(first_thread, NULL) == 0);
    CHECK(pthread_join(second_thread, NULL) == 0);
    group_destroy(&group);
    close_ref(first_ref, first_sockets[1]);
    close_ref(second_ref, second_sockets[1]);
    return true;
}

static bool test_send_pump_reenters_recursively_before_queued_root(void)
{
    int sockets[2];
    pthread_t ordinary_thread;
    pthread_t child_thread;
    turn_group group;
    turn_task ordinary = { 0 };
    turn_task child = { 0 };
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(group_init(&group));
    CHECK(hook_send_ref_wait_event_turn(ref, 21u, false, WAIT_MS));

    ordinary = (turn_task) { .group = &group, .ref = ref, .event_id = 22u };
    child = (turn_task) {
        .group = &group, .ref = ref, .event_id = 23u, .recursive = true
    };
    CHECK(pthread_create(&ordinary_thread, NULL, run_turn, &ordinary) == 0);
    CHECK(pthread_create(&child_thread, NULL, run_turn, &child) == 0);
    CHECK(wait_flag(&group, &ordinary.started, true, WAIT_MS));
    CHECK(wait_flag(&group, &child.started, true, WAIT_MS));
    CHECK(!wait_flag(&group, &child.entered, true, 100));

    CHECK(hook_send_ref_begin_pump(ref, 21u));
    CHECK(wait_flag(&group, &child.entered, true, WAIT_MS));
    CHECK(!wait_flag(&group, &ordinary.entered, true, 100));
    release_task(&child);
    CHECK(pthread_join(child_thread, NULL) == 0);
    CHECK(hook_send_ref_end_pump(ref, 21u));
    hook_send_ref_complete_event(ref, 21u);

    CHECK(wait_flag(&group, &ordinary.entered, true, WAIT_MS));
    release_task(&ordinary);
    CHECK(pthread_join(ordinary_thread, NULL) == 0);
    CHECK(group.order_count == 2u);
    CHECK(group.order[0] == 23u && group.order[1] == 22u);

    group_destroy(&group);
    close_ref(ref, sockets[1]);
    return true;
}

static bool test_nested_sends_unwind_lifo(void)
{
    int sockets[2];
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(hook_send_ref_wait_event_turn(ref, 31u, false, WAIT_MS));
    CHECK(hook_send_ref_begin_pump(ref, 31u));
    CHECK(hook_send_ref_wait_event_turn(ref, 32u, true, WAIT_MS));
    CHECK(hook_send_ref_begin_pump(ref, 32u));
    CHECK(hook_send_ref_wait_event_turn(ref, 33u, true, WAIT_MS));
    hook_send_ref_complete_event(ref, 33u);
    CHECK(hook_send_ref_end_pump(ref, 32u));
    hook_send_ref_complete_event(ref, 32u);
    CHECK(hook_send_ref_end_pump(ref, 31u));
    hook_send_ref_complete_event(ref, 31u);

    close_ref(ref, sockets[1]);
    return true;
}

static bool test_closing_parent_waits_for_entered_descendant(void)
{
    int sockets[2];
    pthread_t end_thread;
    turn_group group;
    pump_end_task end_task = { 0 };
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(group_init(&group));
    CHECK(hook_send_ref_wait_event_turn(ref, 34u, false, WAIT_MS));
    CHECK(hook_send_ref_begin_pump(ref, 34u));
    CHECK(hook_send_ref_wait_event_turn(ref, 35u, true, WAIT_MS));
    end_task = (pump_end_task) {
        .group = &group, .ref = ref, .event_id = 34u
    };

    CHECK(pthread_create(&end_thread, NULL, run_pump_end, &end_task) == 0);
    CHECK(wait_flag(&group, &end_task.started, true, WAIT_MS));
    CHECK(!wait_flag(&group, &end_task.finished, true, 100));
    hook_send_ref_complete_event(ref, 35u);
    CHECK(wait_flag(&group, &end_task.finished, true, WAIT_MS));
    CHECK(end_task.ended);
    CHECK(pthread_join(end_thread, NULL) == 0);

    hook_send_ref_complete_event(ref, 34u);
    group_destroy(&group);
    close_ref(ref, sockets[1]);
    return true;
}

static bool test_missing_parent_is_not_resumable(void)
{
    int sockets[2];
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(!hook_send_ref_end_pump(ref, 99u));

    close_ref(ref, sockets[1]);
    return true;
}

static bool test_parent_pump_restarts_without_a_publication_gap(void)
{
    int sockets[2];
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(hook_send_ref_wait_event_turn(ref, 36u, false, WAIT_MS));

    for (size_t i = 0u; i < 10000u; i++) {
        CHECK(hook_send_ref_begin_pump(ref, 36u));
        CHECK(hook_send_ref_end_pump(ref, 36u));
    }

    hook_send_ref_complete_event(ref, 36u);
    close_ref(ref, sockets[1]);
    return true;
}

static bool test_disconnect_wakes_only_its_waiters(void)
{
    int sockets[2];
    pthread_t waiter_thread;
    turn_group group;
    turn_task waiter = { 0 };
    ksi_hook_send_ref *ref;

    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK((ref = hook_send_ref_create(sockets[0])) != NULL);
    CHECK(group_init(&group));
    CHECK(hook_send_ref_wait_event_turn(ref, 41u, false, WAIT_MS));
    waiter = (turn_task) { .group = &group, .ref = ref, .event_id = 42u };
    CHECK(pthread_create(&waiter_thread, NULL, run_turn, &waiter) == 0);
    CHECK(wait_flag(&group, &waiter.started, true, WAIT_MS));

    hook_send_ref_invalidate(ref);
    CHECK(wait_flag(&group, &waiter.finished, true, 500));
    CHECK(!waiter.entered);
    hook_send_ref_complete_event(ref, 41u);
    CHECK(pthread_join(waiter_thread, NULL) == 0);

    group_destroy(&group);
    hook_send_ref_release(ref);
    close(sockets[1]);
    return true;
}

int main(void)
{
    const struct {
        const char *name;
        bool (*run)(void);
    } tests[] = {
        { "one HookStream serializes keyboard and mouse", test_one_hook_stream_serializes_keyboard_and_mouse },
        { "separate HookStreams run in parallel", test_separate_hook_streams_run_in_parallel },
        { "Send recursively re-enters before queued roots", test_send_pump_reenters_recursively_before_queued_root },
        { "nested Sends unwind LIFO", test_nested_sends_unwind_lifo },
        { "closing parent waits for entered descendant", test_closing_parent_waits_for_entered_descendant },
        { "missing parent is not resumable", test_missing_parent_is_not_resumable },
        { "parent pump restarts without a publication gap", test_parent_pump_restarts_without_a_publication_gap },
        { "disconnect wakes targeted waiters", test_disconnect_wakes_only_its_waiters },
    };

    for (size_t i = 0u; i < sizeof(tests) / sizeof(tests[0]); i++) {
        if (!tests[i].run()) {
            return 1;
        }

        printf("PASS %s\n", tests[i].name);
    }

    return 0;
}
