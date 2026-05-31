#ifndef KEYSHARP_TRUST_PERMISSIONS_H
#define KEYSHARP_TRUST_PERMISSIONS_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/types.h>

#define KSI_PERMISSION_HASH_HEX_LENGTH 64u
#define KSI_PERMISSION_MAX_PATH 4096u
#define KSI_PERMISSION_MAX_COMMAND_LINE 8192u

#define KST_CAP_INPUT_HOOK_KEYBOARD 0x00000001u
#define KST_CAP_INPUT_HOOK_MOUSE 0x00000002u
#define KST_CAP_INPUT_SYNTH_KEYBOARD 0x00000004u
#define KST_CAP_INPUT_SYNTH_MOUSE 0x00000008u
#define KST_CAP_INPUT_BLOCK 0x00000010u
#define KST_CAP_SCREEN_CAPTURE 0x00000020u
#define KST_CAP_ACCESSIBILITY_AUTOMATION 0x00000040u

#define KST_CAP_INPUT_HOOK (KST_CAP_INPUT_HOOK_KEYBOARD | KST_CAP_INPUT_HOOK_MOUSE)
#define KST_CAP_INPUT_SYNTH (KST_CAP_INPUT_SYNTH_KEYBOARD | KST_CAP_INPUT_SYNTH_MOUSE)

typedef struct ksi_permission_store ksi_permission_store;

typedef enum ksi_permission_decision {
    KSI_PERMISSION_DECISION_DENY = 0,
    KSI_PERMISSION_DECISION_ALLOW_ONCE = 1,
    KSI_PERMISSION_DECISION_ALLOW_ALWAYS = 2,
    /* The prompt could not be displayed (no zenity/kdialog reachable, or the
     * requester process environment could not be read). Callers must treat
     * this as a transient deny — do not persist a denial, so the next attempt
     * gets another chance to show the prompt. */
    KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE = 3,
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

/* Returns the persistently-denied capability bits recorded for {uid, exe_hash}.
 * Persistent denials suppress future prompts until cleared via
 * ksi_permissions_clear_persistent or until the caller opts into a forced
 * re-prompt. */
uint32_t ksi_permissions_get_denied_capabilities(
    const ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash);

/* Records a persistent denial of `capabilities` for {uid, exe_hash}. The
 * record is flushed to disk so the denial survives restarts. Returns 0 on
 * success. */
int ksi_permissions_deny_persistent(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path,
    uint32_t capabilities);

/* Clears the specified capability bits from the persistent allow set and the
 * persistent deny set for {uid, exe_hash}. Used by RequestCapabilities and the
 * keysharp-trust CLI to wipe a prior decision so the next prompt re-asks the
 * user. Session grants are also cleared so a fresh prompt is required.
 * Returns 0 on success. */
int ksi_permissions_clear_persistent(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    uint32_t capabilities);

/* Clears the specified capability bits from every record whose uid equals uid,
 * or from every record when uid == (uid_t)-1. Clears persistent allow,
 * persistent deny, and session allow bits. Used by keysharp-trust reset --all.
 * Returns 0 on success (including when no records matched). */
int ksi_permissions_clear_all(
    ksi_permission_store *store,
    uid_t uid,
    uint32_t capabilities);

/* Clears the specified capability bits from the in-memory session allow set
 * for {uid, exe_hash}, leaving persistent state untouched. Called by the
 * daemon when the last client with that exe_hash disconnects so an "Allow
 * once" decision does not survive across script runs. Returns 0 on success
 * or when no record exists for {uid, exe_hash}. */
int ksi_permissions_clear_session(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    uint32_t capabilities);

/* Snapshot of a single stored record used for listing/enumeration. */
typedef struct ksi_permission_entry {
    uid_t uid;
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    const char *exe_path;
    uint32_t persistent_allowed_capabilities;
    uint32_t persistent_denied_capabilities;
    uint64_t last_seen_utc;
} ksi_permission_entry;

typedef bool (*ksi_permissions_visit_fn)(
    const ksi_permission_entry *entry,
    void *user_data);

/* Invokes `visit` for each stored record whose uid equals `uid_filter`, or
 * for every record when `uid_filter == (uid_t)-1`. Iteration stops early
 * when `visit` returns false. */
void ksi_permissions_for_each(
    const ksi_permission_store *store,
    uid_t uid_filter,
    ksi_permissions_visit_fn visit,
    void *user_data);

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

/* --- PID-keyed session grants ---
 *
 * A PID-keyed session grant is stored in /run/keysharp-trust/sessions/ and is
 * visible to all daemons running as root.  It lets a single combined prompt (e.g.
 * in keysharp-inputd) cover capabilities that span multiple daemons (e.g. screen
 * capture handled by keysharp-screencap) without each daemon showing its own
 * prompt.
 *
 * The grant is scoped to a unique (uid, pid, start_time) triplet where start_time
 * is field 22 from /proc/<pid>/stat (jiffies since boot).  This combination is
 * globally unique for the lifetime of the system, preventing PID reuse from
 * inheriting a prior session's grants.
 *
 * Session files are automatically absent after a reboot because they live under
 * /run (tmpfs).  Stale files left by crashes are harmless — the PID will have
 * been reused with a different start_time, so the old session file will never
 * match.
 */

/* Returns field 22 (start time in jiffies) from /proc/<pid>/stat, or 0 on
 * failure.  Combine with the PID to form a collision-resistant session key. */
uint64_t ksi_permissions_get_process_start_time(pid_t pid);

/* Returns the capability bits previously granted for this (uid, pid, start_time)
 * triplet, or 0 if no session exists or the file cannot be read. */
uint32_t ksi_permissions_get_session_by_pid(uid_t uid, pid_t pid, uint64_t start_time);

/* Grants additional capability bits to the (uid, pid, start_time) session,
 * merging with any previously granted bits.  Creates the session file if it does
 * not yet exist.  Returns 0 on success. */
int ksi_permissions_grant_session_for_pid(uid_t uid, pid_t pid, uint64_t start_time,
                                          uint32_t capabilities);

#endif
