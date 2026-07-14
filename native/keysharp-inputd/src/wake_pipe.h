#ifndef KSI_WAKE_PIPE_H
#define KSI_WAKE_PIPE_H

int ksi_wake_pipe_open(int *read_fd, int *write_fd);
void ksi_wake_pipe_drain(int fd);
void ksi_wake_pipe_close(int *read_fd, int *write_fd);

#endif
