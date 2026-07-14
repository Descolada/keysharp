#include "pipe_ring.h"

#include <pthread.h>
#include <sched.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct ring_stress_context {
    ksi_pipe_ring ring;
    unsigned int count;
    atomic_bool failed;
} ring_stress_context;

static void *produce(void *argument)
{
    ring_stress_context *context = argument;

    for (unsigned int value = 1u; value <= context->count; value++) {
        while (!ksi_pipe_ring_push(&context->ring, &value)) {
            sched_yield();
        }
    }

    return NULL;
}

static void *consume(void *argument)
{
    ring_stress_context *context = argument;

    for (unsigned int expected = 1u; expected <= context->count; expected++) {
        unsigned int actual = 0u;

        while (!ksi_pipe_ring_pop(&context->ring, &actual)) {
            sched_yield();
        }

        if (actual != expected) {
            atomic_store(&context->failed, true);
            break;
        }
    }

    return NULL;
}

int main(void)
{
    ring_stress_context context;
    pthread_t producer;
    pthread_t consumer;

    memset(&context, 0, sizeof(context));
    context.count = 250000u;
    atomic_init(&context.failed, false);

    if (ksi_pipe_ring_init(&context.ring, sizeof(unsigned int), 64u) != 0) {
        fputs("FAIL pipe ring initialization\n", stderr);
        return EXIT_FAILURE;
    }

    if (pthread_create(&producer, NULL, produce, &context) != 0
        || pthread_create(&consumer, NULL, consume, &context) != 0) {
        fputs("FAIL pipe ring stress thread creation\n", stderr);
        ksi_pipe_ring_close(&context.ring);
        return EXIT_FAILURE;
    }

    if (pthread_join(producer, NULL) != 0
        || pthread_join(consumer, NULL) != 0
        || atomic_load(&context.failed)) {
        fputs("FAIL pipe ring concurrent FIFO stress\n", stderr);
        return EXIT_FAILURE;
    }

    ksi_pipe_ring_close(&context.ring);
    puts("PASS pipe ring concurrent FIFO stress");
    return EXIT_SUCCESS;
}
