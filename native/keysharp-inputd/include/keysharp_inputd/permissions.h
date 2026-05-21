#ifndef KEYSHARP_INPUTD_PERMISSIONS_H
#define KEYSHARP_INPUTD_PERMISSIONS_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/types.h>

#define KSI_PERMISSION_HASH_HEX_LENGTH 64u
#define KSI_PERMISSION_MAX_PATH 4096u
#define KSI_PERMISSION_MAX_COMMAND_LINE 8192u

typedef struct ksi_permission_store ksi_permission_store;

typedef enum ksi_permission_decision {
    KSI_PERMISSION_DECISION_DENY = 0,
    KSI_PERMISSION_DECISION_ALLOW_ONCE = 1,
    KSI_PERMISSION_DECISION_ALLOW_ALWAYS = 2,
} ksi_permission_decision;

int ksi_permissions_create(ksi_permission_store **store);
void ksi_permissions_destroy(ksi_permission_store *store);

int ksi_permissions_identify_process(
    pid_t pid,
    char *path_buffer,
    size_t path_buffer_size,
    char *command_line_buffer,
    size_t command_line_buffer_size,
    /* SHA-256 process identity: executable digest plus raw argv bytes. */
    char hash_buffer[KSI_PERMISSION_HASH_HEX_LENGTH + 1u]);

uint32_t ksi_permissions_get_allowed_capabilities(
    const ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash);

void ksi_permissions_note_seen(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path);

int ksi_permissions_grant_session(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path,
    uint32_t capabilities);

int ksi_permissions_grant_persistent(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path,
    uint32_t capabilities);

ksi_permission_decision ksi_permissions_prompt(
    pid_t pid,
    uid_t uid,
    gid_t gid,
    const char *exe_path,
    const char *command_line,
    const char *exe_hash,
    uint32_t capabilities);

/* Signal any in-progress prompt to abort and return DENY immediately.
 * Safe to call from any thread.  Idempotent. */
void ksi_permissions_cancel(void);

#endif
