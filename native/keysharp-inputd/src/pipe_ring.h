#ifndef KEYSHARP_INPUTD_PIPE_RING_H
#define KEYSHARP_INPUTD_PIPE_RING_H

#include <stdbool.h>
#include <stddef.h>

typedef struct ksi_pipe_ring {
    void *implementation;
} ksi_pipe_ring;

int ksi_pipe_ring_init(ksi_pipe_ring *ring, size_t element_size, size_t capacity);
void ksi_pipe_ring_close(ksi_pipe_ring *ring);
bool ksi_pipe_ring_push(ksi_pipe_ring *ring, const void *item);
bool ksi_pipe_ring_pop(ksi_pipe_ring *ring, void *item);
int ksi_pipe_ring_wake_fd(const ksi_pipe_ring *ring);
void ksi_pipe_ring_wake(const ksi_pipe_ring *ring);
void ksi_pipe_ring_drain_wake(const ksi_pipe_ring *ring);

#endif
