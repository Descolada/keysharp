#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include "worker_pool.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct ksi_worker_pool_implementation {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    bool stopping;
    pthread_t threads[KSI_WORKER_POOL_MAX_THREADS];
    size_t thread_count;
    ksi_worker_job jobs[KSI_WORKER_POOL_MAX_JOBS];
    size_t head;
    size_t count;
    size_t active;
} ksi_worker_pool_implementation;

static uint64_t monotonic_ms(void)
{
    struct timespec value;

    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) {
        return 0u;
    }

    return ((uint64_t)value.tv_sec * 1000u) + ((uint64_t)value.tv_nsec / 1000000u);
}

static bool join_thread_before(pthread_t thread, uint64_t deadline_ms)
{
    struct timespec absolute;
    uint64_t now = monotonic_ms();
    uint64_t remaining_ms;

    if (deadline_ms == 0u) {
        return pthread_join(thread, NULL) == 0;
    }

    /* pthread_timedjoin_np only honors CLOCK_REALTIME, so a backward wall-clock
     * step could extend this wait.  That is acceptable here: this is a
     * shutdown-only deadline reached after grabs are already released, so the
     * worst case is a slower shutdown, never a held-grab input freeze.  (The
     * hook-decision wait, which CAN hold grabs, uses CLOCK_MONOTONIC instead.) */
    if (now >= deadline_ms || clock_gettime(CLOCK_REALTIME, &absolute) != 0) {
        return false;
    }

    remaining_ms = deadline_ms - now;
    absolute.tv_sec += (time_t)(remaining_ms / 1000u);
    absolute.tv_nsec += (long)((remaining_ms % 1000u) * 1000000u);

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    return pthread_timedjoin_np(thread, NULL, &absolute) == 0;
}

static void *worker_thread_main(void *context)
{
    ksi_worker_pool_implementation *pool = context;

    for (;;) {
        ksi_worker_job job;

        pthread_mutex_lock(&pool->mutex);

        while (pool->count == 0u && !pool->stopping) {
            pthread_cond_wait(&pool->condition, &pool->mutex);
        }

        if (pool->stopping) {
            pthread_mutex_unlock(&pool->mutex);
            return NULL;
        }

        job = pool->jobs[pool->head];
        memset(&pool->jobs[pool->head], 0, sizeof(pool->jobs[pool->head]));
        pool->head = (pool->head + 1u) % KSI_WORKER_POOL_MAX_JOBS;
        pool->count--;
        pool->active++;
        pthread_mutex_unlock(&pool->mutex);

        (void)job.run(job.context);

        pthread_mutex_lock(&pool->mutex);
        pool->active--;
        pthread_mutex_unlock(&pool->mutex);
    }
}

int ksi_worker_pool_init(ksi_worker_pool *pool)
{
    ksi_worker_pool_implementation *implementation;

    if (pool == NULL || pool->implementation != NULL) {
        return -1;
    }

    implementation = calloc(1, sizeof(*implementation));

    if (implementation == NULL) {
        return -1;
    }

    if (pthread_mutex_init(&implementation->mutex, NULL) != 0) {
        free(implementation);
        return -1;
    }

    if (pthread_cond_init(&implementation->condition, NULL) != 0) {
        pthread_mutex_destroy(&implementation->mutex);
        free(implementation);
        return -1;
    }

    for (size_t i = 0; i < KSI_WORKER_POOL_MAX_THREADS; i++) {
        if (pthread_create(
                &implementation->threads[i], NULL, worker_thread_main, implementation) != 0) {
            pthread_mutex_lock(&implementation->mutex);
            implementation->stopping = true;
            pthread_cond_broadcast(&implementation->condition);
            pthread_mutex_unlock(&implementation->mutex);

            for (size_t j = 0; j < implementation->thread_count; j++) {
                pthread_join(implementation->threads[j], NULL);
            }

            pthread_cond_destroy(&implementation->condition);
            pthread_mutex_destroy(&implementation->mutex);
            free(implementation);
            return -1;
        }

        implementation->thread_count++;
    }

    pool->implementation = implementation;
    return 0;
}

bool ksi_worker_pool_submit(
    ksi_worker_pool *pool,
    void *(*run)(void *),
    void (*destroy)(void *),
    void *context)
{
    ksi_worker_pool_implementation *implementation;
    size_t tail;

    if (pool == NULL || run == NULL || context == NULL) {
        return false;
    }

    implementation = pool->implementation;

    if (implementation == NULL) {
        return false;
    }

    pthread_mutex_lock(&implementation->mutex);

    if (implementation->stopping || implementation->count >= KSI_WORKER_POOL_MAX_JOBS) {
        pthread_mutex_unlock(&implementation->mutex);
        return false;
    }

    tail = (implementation->head + implementation->count) % KSI_WORKER_POOL_MAX_JOBS;
    implementation->jobs[tail].run = run;
    implementation->jobs[tail].destroy = destroy;
    implementation->jobs[tail].context = context;
    implementation->count++;
    pthread_cond_signal(&implementation->condition);
    pthread_mutex_unlock(&implementation->mutex);
    return true;
}

bool ksi_worker_pool_has_work(ksi_worker_pool *pool)
{
    ksi_worker_pool_implementation *implementation;
    bool has_work;

    if (pool == NULL || pool->implementation == NULL) {
        return false;
    }

    implementation = pool->implementation;
    pthread_mutex_lock(&implementation->mutex);
    has_work = implementation->count != 0u || implementation->active != 0u;
    pthread_mutex_unlock(&implementation->mutex);
    return has_work;
}

void ksi_worker_pool_request_stop(ksi_worker_pool *pool)
{
    ksi_worker_pool_implementation *implementation;

    if (pool == NULL || pool->implementation == NULL) {
        return;
    }

    implementation = pool->implementation;
    pthread_mutex_lock(&implementation->mutex);
    implementation->stopping = true;
    pthread_cond_broadcast(&implementation->condition);
    pthread_mutex_unlock(&implementation->mutex);
}

bool ksi_worker_pool_join_before(ksi_worker_pool *pool, uint64_t deadline_ms)
{
    ksi_worker_pool_implementation *implementation;

    if (pool == NULL || pool->implementation == NULL) {
        return true;
    }

    implementation = pool->implementation;

    for (size_t i = 0; i < implementation->thread_count; i++) {
        if (!join_thread_before(implementation->threads[i], deadline_ms)) {
            return false;
        }
    }

    implementation->thread_count = 0u;
    return true;
}

void ksi_worker_pool_destroy(ksi_worker_pool *pool)
{
    ksi_worker_pool_implementation *implementation;

    if (pool == NULL || pool->implementation == NULL) {
        return;
    }

    implementation = pool->implementation;

    for (size_t i = 0; i < implementation->count; i++) {
        size_t index = (implementation->head + i) % KSI_WORKER_POOL_MAX_JOBS;

        if (implementation->jobs[index].destroy != NULL) {
            implementation->jobs[index].destroy(implementation->jobs[index].context);
        }
    }

    pthread_cond_destroy(&implementation->condition);
    pthread_mutex_destroy(&implementation->mutex);
    free(implementation);
    pool->implementation = NULL;
}
