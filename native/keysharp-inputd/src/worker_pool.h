#ifndef KEYSHARP_INPUTD_WORKER_POOL_H
#define KEYSHARP_INPUTD_WORKER_POOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define KSI_WORKER_POOL_MAX_THREADS 2u
#define KSI_WORKER_POOL_MAX_JOBS 128u

typedef struct ksi_worker_job {
    void *(*run)(void *);
    void (*destroy)(void *);
    void *context;
} ksi_worker_job;

typedef struct ksi_worker_pool {
    void *implementation;
} ksi_worker_pool;

int ksi_worker_pool_init(ksi_worker_pool *pool);
bool ksi_worker_pool_submit(
    ksi_worker_pool *pool,
    void *(*run)(void *),
    void (*destroy)(void *),
    void *context);
bool ksi_worker_pool_has_work(ksi_worker_pool *pool);
void ksi_worker_pool_request_stop(ksi_worker_pool *pool);
bool ksi_worker_pool_join_before(ksi_worker_pool *pool, uint64_t deadline_ms);
void ksi_worker_pool_destroy(ksi_worker_pool *pool);

#endif
