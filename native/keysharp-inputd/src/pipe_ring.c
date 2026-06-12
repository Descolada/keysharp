#include "pipe_ring.h"

#include <fcntl.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef struct ksi_pipe_ring_implementation {
    pthread_mutex_t mutex;
    uint8_t *buffer;
    size_t element_size;
    size_t capacity;
    size_t head;
    size_t count;
    int wake_read_fd;
    int wake_write_fd;
} ksi_pipe_ring_implementation;

static void wake_pipe_write(int fd)
{
    uint8_t byte = 1;
    ssize_t written = write(fd, &byte, sizeof(byte));
    (void)written;
}

int ksi_pipe_ring_init(ksi_pipe_ring *ring, size_t element_size, size_t capacity)
{
    ksi_pipe_ring_implementation *implementation;
    int pipe_fds[2];

    if (ring == NULL || element_size == 0u || capacity == 0u) {
        return -1;
    }

    ring->implementation = NULL;
    implementation = calloc(1, sizeof(*implementation));

    if (implementation == NULL) {
        return -1;
    }

    implementation->wake_read_fd = -1;
    implementation->wake_write_fd = -1;
    implementation->buffer = calloc(capacity, element_size);

    if (implementation->buffer == NULL) {
        free(implementation);
        return -1;
    }

    if (pthread_mutex_init(&implementation->mutex, NULL) != 0) {
        free(implementation->buffer);
        free(implementation);
        return -1;
    }

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&implementation->mutex);
        free(implementation->buffer);
        free(implementation);
        return -1;
    }

    implementation->element_size = element_size;
    implementation->capacity = capacity;
    implementation->wake_read_fd = pipe_fds[0];
    implementation->wake_write_fd = pipe_fds[1];

    {
        int flags = fcntl(implementation->wake_read_fd, F_GETFL);
        if (flags >= 0) {
            (void)fcntl(implementation->wake_read_fd, F_SETFL, flags | O_NONBLOCK);
        }

        flags = fcntl(implementation->wake_write_fd, F_GETFL);
        if (flags >= 0) {
            (void)fcntl(implementation->wake_write_fd, F_SETFL, flags | O_NONBLOCK);
        }
    }

    ring->implementation = implementation;
    return 0;
}

void ksi_pipe_ring_close(ksi_pipe_ring *ring)
{
    ksi_pipe_ring_implementation *implementation;

    if (ring == NULL || ring->implementation == NULL) {
        return;
    }

    implementation = ring->implementation;

    if (implementation->wake_read_fd >= 0) {
        close(implementation->wake_read_fd);
    }

    if (implementation->wake_write_fd >= 0) {
        close(implementation->wake_write_fd);
    }

    pthread_mutex_destroy(&implementation->mutex);
    free(implementation->buffer);
    free(implementation);
    ring->implementation = NULL;
}

bool ksi_pipe_ring_push(ksi_pipe_ring *ring, const void *item)
{
    ksi_pipe_ring_implementation *implementation;
    bool pushed = false;

    if (ring == NULL || ring->implementation == NULL || item == NULL) {
        return false;
    }

    implementation = ring->implementation;
    pthread_mutex_lock(&implementation->mutex);

    if (implementation->count < implementation->capacity) {
        size_t tail = (implementation->head + implementation->count) % implementation->capacity;
        memcpy(
            implementation->buffer + tail * implementation->element_size,
            item,
            implementation->element_size);
        implementation->count++;
        pushed = true;
    }

    pthread_mutex_unlock(&implementation->mutex);

    if (pushed) {
        wake_pipe_write(implementation->wake_write_fd);
    }

    return pushed;
}

bool ksi_pipe_ring_pop(ksi_pipe_ring *ring, void *item)
{
    ksi_pipe_ring_implementation *implementation;

    if (ring == NULL || ring->implementation == NULL || item == NULL) {
        return false;
    }

    implementation = ring->implementation;
    pthread_mutex_lock(&implementation->mutex);

    if (implementation->count == 0u) {
        pthread_mutex_unlock(&implementation->mutex);
        return false;
    }

    memcpy(
        item,
        implementation->buffer + implementation->head * implementation->element_size,
        implementation->element_size);
    memset(
        implementation->buffer + implementation->head * implementation->element_size,
        0,
        implementation->element_size);
    implementation->head = (implementation->head + 1u) % implementation->capacity;
    implementation->count--;
    pthread_mutex_unlock(&implementation->mutex);
    return true;
}

int ksi_pipe_ring_wake_fd(const ksi_pipe_ring *ring)
{
    const ksi_pipe_ring_implementation *implementation;

    if (ring == NULL || ring->implementation == NULL) {
        return -1;
    }

    implementation = ring->implementation;
    return implementation->wake_read_fd;
}

void ksi_pipe_ring_wake(const ksi_pipe_ring *ring)
{
    const ksi_pipe_ring_implementation *implementation;

    if (ring == NULL || ring->implementation == NULL) {
        return;
    }

    implementation = ring->implementation;
    wake_pipe_write(implementation->wake_write_fd);
}

void ksi_pipe_ring_drain_wake(const ksi_pipe_ring *ring)
{
    const ksi_pipe_ring_implementation *implementation;
    uint8_t buffer[64];

    if (ring == NULL || ring->implementation == NULL) {
        return;
    }

    implementation = ring->implementation;

    while (read(implementation->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}
