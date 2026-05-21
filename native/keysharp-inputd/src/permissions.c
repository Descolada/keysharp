#include "keysharp_inputd/permissions.h"

#include "keysharp_inputd/protocol.h"

#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <pwd.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* Set to 1 by ksi_permissions_cancel() to abort any in-progress prompt. */
static atomic_int g_prompt_cancelled = 0;

void ksi_permissions_cancel(void)
{
    atomic_store(&g_prompt_cancelled, 1);
}

#define KSI_PERMISSION_STORE_DIRECTORY "/var/lib/keysharp-inputd"
#define KSI_PERMISSION_STORE_FILE_NAME "inputd-permissions.tsv"
#define KSI_PERMISSION_STORE_VERSION "v2"
#define KSI_PERMISSION_RECORD_TTL_SECONDS (60u * 24u * 60u * 60u)
#define KSI_PROMPT_TIMEOUT_SECONDS 60u

typedef struct ksi_permission_record {
    uid_t uid;
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    uint32_t persistent_allowed_capabilities;
    uint32_t session_allowed_capabilities;
    uint64_t last_seen_utc;
    char *exe_path;
} ksi_permission_record;

struct ksi_permission_store {
    char *path;
    ksi_permission_record *records;
    size_t count;
    size_t capacity;
};

typedef struct ksi_sha256_context {
    uint8_t data[64];
    uint32_t datalen;
    uint64_t bitlen;
    uint32_t state[8];
} ksi_sha256_context;

static uint64_t utc_now_seconds(void)
{
    time_t now = time(NULL);
    return now < 0 ? 0u : (uint64_t)now;
}

static void sha256_transform(ksi_sha256_context *context, const uint8_t data[64])
{
    static const uint32_t k[64] = {
        0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u,
        0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
        0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
        0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
        0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu,
        0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
        0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
        0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
        0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
        0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
        0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u,
        0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
        0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u,
        0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
        0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
        0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u,
    };
    uint32_t m[64];
    uint32_t a;
    uint32_t b;
    uint32_t c;
    uint32_t d;
    uint32_t e;
    uint32_t f;
    uint32_t g;
    uint32_t h;

    for (size_t i = 0; i < 16; i++) {
        size_t base = i * 4u;
        m[i] = ((uint32_t)data[base] << 24u)
            | ((uint32_t)data[base + 1u] << 16u)
            | ((uint32_t)data[base + 2u] << 8u)
            | (uint32_t)data[base + 3u];
    }

    for (size_t i = 16; i < 64; i++) {
        uint32_t s0 = ((m[i - 15u] >> 7u) | (m[i - 15u] << 25u))
            ^ ((m[i - 15u] >> 18u) | (m[i - 15u] << 14u))
            ^ (m[i - 15u] >> 3u);
        uint32_t s1 = ((m[i - 2u] >> 17u) | (m[i - 2u] << 15u))
            ^ ((m[i - 2u] >> 19u) | (m[i - 2u] << 13u))
            ^ (m[i - 2u] >> 10u);
        m[i] = m[i - 16u] + s0 + m[i - 7u] + s1;
    }

    a = context->state[0];
    b = context->state[1];
    c = context->state[2];
    d = context->state[3];
    e = context->state[4];
    f = context->state[5];
    g = context->state[6];
    h = context->state[7];

    for (size_t i = 0; i < 64; i++) {
        uint32_t s1 = ((e >> 6u) | (e << 26u))
            ^ ((e >> 11u) | (e << 21u))
            ^ ((e >> 25u) | (e << 7u));
        uint32_t ch = (e & f) ^ ((~e) & g);
        uint32_t temp1 = h + s1 + ch + k[i] + m[i];
        uint32_t s0 = ((a >> 2u) | (a << 30u))
            ^ ((a >> 13u) | (a << 19u))
            ^ ((a >> 22u) | (a << 10u));
        uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t temp2 = s0 + maj;

        h = g;
        g = f;
        f = e;
        e = d + temp1;
        d = c;
        c = b;
        b = a;
        a = temp1 + temp2;
    }

    context->state[0] += a;
    context->state[1] += b;
    context->state[2] += c;
    context->state[3] += d;
    context->state[4] += e;
    context->state[5] += f;
    context->state[6] += g;
    context->state[7] += h;
}

static void sha256_init(ksi_sha256_context *context)
{
    memset(context, 0, sizeof(*context));
    context->state[0] = 0x6a09e667u;
    context->state[1] = 0xbb67ae85u;
    context->state[2] = 0x3c6ef372u;
    context->state[3] = 0xa54ff53au;
    context->state[4] = 0x510e527fu;
    context->state[5] = 0x9b05688cu;
    context->state[6] = 0x1f83d9abu;
    context->state[7] = 0x5be0cd19u;
}

static void sha256_update(ksi_sha256_context *context, const uint8_t *data, size_t len)
{
    for (size_t i = 0; i < len; i++) {
        context->data[context->datalen] = data[i];
        context->datalen++;

        if (context->datalen == 64u) {
            sha256_transform(context, context->data);
            context->bitlen += 512u;
            context->datalen = 0u;
        }
    }
}

static void sha256_final(ksi_sha256_context *context, uint8_t hash[32])
{
    uint32_t index = context->datalen;

    context->data[index++] = 0x80u;

    if (index > 56u) {
        while (index < 64u) {
            context->data[index++] = 0u;
        }

        sha256_transform(context, context->data);
        index = 0u;
    }

    while (index < 56u) {
        context->data[index++] = 0u;
    }

    context->bitlen += (uint64_t)context->datalen * 8u;
    context->data[63] = (uint8_t)(context->bitlen);
    context->data[62] = (uint8_t)(context->bitlen >> 8u);
    context->data[61] = (uint8_t)(context->bitlen >> 16u);
    context->data[60] = (uint8_t)(context->bitlen >> 24u);
    context->data[59] = (uint8_t)(context->bitlen >> 32u);
    context->data[58] = (uint8_t)(context->bitlen >> 40u);
    context->data[57] = (uint8_t)(context->bitlen >> 48u);
    context->data[56] = (uint8_t)(context->bitlen >> 56u);
    sha256_transform(context, context->data);

    for (size_t i = 0; i < 4u; i++) {
        hash[i] = (uint8_t)((context->state[0] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 4u] = (uint8_t)((context->state[1] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 8u] = (uint8_t)((context->state[2] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 12u] = (uint8_t)((context->state[3] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 16u] = (uint8_t)((context->state[4] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 20u] = (uint8_t)((context->state[5] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 24u] = (uint8_t)((context->state[6] >> (24u - (i * 8u))) & 0xffu);
        hash[i + 28u] = (uint8_t)((context->state[7] >> (24u - (i * 8u))) & 0xffu);
    }
}

static void hash_to_hex(const uint8_t hash[32], char output[KSI_PERMISSION_HASH_HEX_LENGTH + 1u])
{
    static const char hex[] = "0123456789abcdef";

    for (size_t i = 0; i < 32u; i++) {
        output[i * 2u] = hex[(hash[i] >> 4u) & 0x0fu];
        output[(i * 2u) + 1u] = hex[hash[i] & 0x0fu];
    }

    output[KSI_PERMISSION_HASH_HEX_LENGTH] = '\0';
}

static int compute_fd_sha256_hex(int fd, char output[KSI_PERMISSION_HASH_HEX_LENGTH + 1u])
{
    uint8_t buffer[8192];
    uint8_t hash[32];
    ksi_sha256_context context;
    ssize_t bytes_read;

    if (lseek(fd, 0, SEEK_SET) < 0) {
        return -1;
    }

    sha256_init(&context);

    while ((bytes_read = read(fd, buffer, sizeof(buffer))) > 0) {
        sha256_update(&context, buffer, (size_t)bytes_read);
    }

    if (bytes_read < 0) {
        return -1;
    }

    sha256_final(&context, hash);
    hash_to_hex(hash, output);
    return 0;
}

static void append_argument_display(
    char *output,
    size_t output_size,
    size_t *output_length,
    bool *skipping_argv0,
    const uint8_t *data,
    size_t data_length)
{
    if (output == NULL || output_size == 0u || output_length == NULL || skipping_argv0 == NULL) {
        return;
    }

    for (size_t i = 0; i < data_length; i++) {
        uint8_t value = data[i];

        if (*skipping_argv0) {
            if (value == '\0') {
                *skipping_argv0 = false;
            }

            continue;
        }

        if (*output_length + 1u >= output_size) {
            break;
        }

        if (value == '\0') {
            output[(*output_length)++] = ' ';
        } else if (isprint(value)) {
            output[(*output_length)++] = (char)value;
        } else {
            output[(*output_length)++] = '?';
        }
    }

    output[*output_length] = '\0';
}

static int hash_process_identity(
    int exe_fd,
    pid_t pid,
    char *command_line_buffer,
    size_t command_line_buffer_size,
    char output[KSI_PERMISSION_HASH_HEX_LENGTH + 1u])
{
    static const uint8_t domain[] = "keysharp-inputd-process-identity-v1";
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    char proc_path[64];
    uint8_t buffer[8192];
    uint8_t hash[32];
    ksi_sha256_context context;
    size_t display_length = 0u;
    bool has_command_line = false;
    bool skipping_argv0 = true;
    ssize_t bytes_read;
    int cmdline_fd;

    if (command_line_buffer == NULL || command_line_buffer_size == 0u) {
        return -1;
    }

    command_line_buffer[0] = '\0';

    if (compute_fd_sha256_hex(exe_fd, exe_hash) != 0) {
        return -1;
    }

    (void)snprintf(proc_path, sizeof(proc_path), "/proc/%ld/cmdline", (long)pid);
    cmdline_fd = open(proc_path, O_RDONLY | O_CLOEXEC);

    if (cmdline_fd < 0) {
        return -1;
    }

    sha256_init(&context);
    sha256_update(&context, domain, sizeof(domain));
    sha256_update(&context, (const uint8_t *)exe_hash, KSI_PERMISSION_HASH_HEX_LENGTH);

    while ((bytes_read = read(cmdline_fd, buffer, sizeof(buffer))) > 0) {
        has_command_line = true;
        sha256_update(&context, buffer, (size_t)bytes_read);
        append_argument_display(
            command_line_buffer, command_line_buffer_size, &display_length, &skipping_argv0,
            buffer, (size_t)bytes_read);
    }

    close(cmdline_fd);

    if (bytes_read < 0 || !has_command_line) {
        return -1;
    }

    while (display_length > 0u && command_line_buffer[display_length - 1u] == ' ') {
        command_line_buffer[--display_length] = '\0';
    }

    sha256_final(&context, hash);
    hash_to_hex(hash, output);
    return 0;
}

static bool looks_like_sha256(const char *text)
{
    if (text == NULL || strlen(text) != KSI_PERMISSION_HASH_HEX_LENGTH) {
        return false;
    }

    for (size_t i = 0; i < KSI_PERMISSION_HASH_HEX_LENGTH; i++) {
        if (!isxdigit((unsigned char)text[i])) {
            return false;
        }
    }

    return true;
}

/* Creates the directory if it does not exist and verifies it is a directory.
 * When fix_permissions is true, strips group/other bits if they are set
 * (used for the leaf directory that holds sensitive files). */
static int ensure_directory(const char *path, bool fix_permissions)
{
    struct stat info;

    if (path == NULL || path[0] == '\0') {
        return -1;
    }

    if (mkdir(path, S_IRWXU) != 0 && errno != EEXIST) {
        return -1;
    }

    if (stat(path, &info) != 0 || !S_ISDIR(info.st_mode)) {
        return -1;
    }

    if (fix_permissions && (info.st_mode & (S_IRWXG | S_IRWXO)) != 0) {
        (void)chmod(path, S_IRWXU);
    }

    return 0;
}

static int ensure_parent_directories(const char *path)
{
    char *copy;
    size_t length;

    if (path == NULL) {
        return -1;
    }

    copy = strdup(path);

    if (copy == NULL) {
        return -1;
    }

    length = strlen(copy);

    for (size_t i = 1; i < length; i++) {
        if (copy[i] != '/') {
            continue;
        }

        copy[i] = '\0';

        if (ensure_directory(copy, false) != 0) {
            free(copy);
            return -1;
        }

        copy[i] = '/';
    }

    free(copy);
    return 0;
}

static char *resolve_store_path(void)
{
    char full_path[KSI_PERMISSION_MAX_PATH];
    int written;

    if (ensure_parent_directories(KSI_PERMISSION_STORE_DIRECTORY) != 0
        || ensure_directory(KSI_PERMISSION_STORE_DIRECTORY, true) != 0) {
        return NULL;
    }

    written = snprintf(
        full_path, sizeof(full_path), "%s/%s",
        KSI_PERMISSION_STORE_DIRECTORY, KSI_PERMISSION_STORE_FILE_NAME);

    if (written < 0 || (size_t)written >= sizeof(full_path)) {
        return NULL;
    }

    return strdup(full_path);
}

static char *escape_field(const char *value)
{
    size_t length;
    char *result;
    char *cursor;

    if (value == NULL) {
        return strdup("");
    }

    length = strlen(value);
    result = calloc((length * 2u) + 1u, sizeof(char));

    if (result == NULL) {
        return NULL;
    }

    cursor = result;

    for (size_t i = 0; i < length; i++) {
        switch (value[i]) {
            case '\\':
                *cursor++ = '\\';
                *cursor++ = '\\';
                break;

            case '\t':
                *cursor++ = '\\';
                *cursor++ = 't';
                break;

            case '\n':
                *cursor++ = '\\';
                *cursor++ = 'n';
                break;

            case '\r':
                *cursor++ = '\\';
                *cursor++ = 'r';
                break;

            default:
                *cursor++ = value[i];
                break;
        }
    }

    *cursor = '\0';
    return result;
}

static char *unescape_field(const char *value)
{
    size_t length;
    char *result;
    char *cursor;

    if (value == NULL) {
        return strdup("");
    }

    length = strlen(value);
    result = calloc(length + 1u, sizeof(char));

    if (result == NULL) {
        return NULL;
    }

    cursor = result;

    for (size_t i = 0; i < length; i++) {
        if (value[i] == '\\' && i + 1u < length) {
            i++;

            switch (value[i]) {
                case 't':
                    *cursor++ = '\t';
                    break;

                case 'n':
                    *cursor++ = '\n';
                    break;

                case 'r':
                    *cursor++ = '\r';
                    break;

                case '\\':
                    *cursor++ = '\\';
                    break;

                default:
                    *cursor++ = value[i];
                    break;
            }
        } else {
            *cursor++ = value[i];
        }
    }

    *cursor = '\0';
    return result;
}

static void free_record(ksi_permission_record *record)
{
    if (record == NULL) {
        return;
    }

    free(record->exe_path);
    record->exe_path = NULL;
}

static bool ensure_capacity(ksi_permission_store *store, size_t required)
{
    ksi_permission_record *records;
    size_t capacity;

    if (store->capacity >= required) {
        return true;
    }

    capacity = store->capacity == 0u ? 8u : store->capacity * 2u;

    while (capacity < required) {
        capacity *= 2u;
    }

    records = realloc(store->records, capacity * sizeof(*records));

    if (records == NULL) {
        return false;
    }

    store->records = records;
    store->capacity = capacity;
    return true;
}

static ssize_t find_record_index(const ksi_permission_store *store, uid_t uid, const char *exe_hash)
{
    if (store == NULL || exe_hash == NULL) {
        return -1;
    }

    for (size_t i = 0; i < store->count; i++) {
        if (store->records[i].uid == uid && strcmp(store->records[i].exe_hash, exe_hash) == 0) {
            return (ssize_t)i;
        }
    }

    return -1;
}

static ksi_permission_record *get_or_add_record(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path)
{
    ssize_t index;
    ksi_permission_record *record;

    if (store == NULL || exe_hash == NULL || !looks_like_sha256(exe_hash)) {
        return NULL;
    }

    index = find_record_index(store, uid, exe_hash);

    if (index >= 0) {
        record = &store->records[index];
    } else {
        if (!ensure_capacity(store, store->count + 1u)) {
            return NULL;
        }

        record = &store->records[store->count++];
        memset(record, 0, sizeof(*record));
        record->uid = uid;
        (void)snprintf(record->exe_hash, sizeof(record->exe_hash), "%s", exe_hash);
    }

    if (exe_path != NULL && exe_path[0] != '\0') {
        char *copy = strdup(exe_path);

        if (copy == NULL) {
            return NULL;
        }

        free(record->exe_path);
        record->exe_path = copy;
    }

    record->last_seen_utc = utc_now_seconds();
    return record;
}

static bool prune_expired_records(ksi_permission_store *store)
{
    uint64_t now = utc_now_seconds();
    uint64_t cutoff = now > KSI_PERMISSION_RECORD_TTL_SECONDS
        ? now - KSI_PERMISSION_RECORD_TTL_SECONDS
        : 0u;
    bool changed = false;

    if (store == NULL) {
        return false;
    }

    for (size_t i = 0; i < store->count;) {
        ksi_permission_record *record = &store->records[i];
        bool expired = record->last_seen_utc < cutoff;
        bool empty_persistent = record->persistent_allowed_capabilities == 0u;
        bool empty_session = record->session_allowed_capabilities == 0u;

        if (expired || (empty_persistent && empty_session)) {
            free_record(record);

            if (i + 1u < store->count) {
                memmove(record, record + 1u, (store->count - i - 1u) * sizeof(*record));
            }

            store->count--;
            changed = true;
            continue;
        }

        i++;
    }

    return changed;
}

static int save_store(const ksi_permission_store *store)
{
    char *temp_path;
    FILE *file;
    int fd;

    if (store == NULL || store->path == NULL) {
        return -1;
    }

    temp_path = calloc(strlen(store->path) + 5u, sizeof(char));

    if (temp_path == NULL) {
        return -1;
    }

    (void)snprintf(temp_path, strlen(store->path) + 5u, "%s.tmp", store->path);
    file = fopen(temp_path, "w");

    if (file == NULL) {
        free(temp_path);
        return -1;
    }

    (void)fprintf(file, "%s\n", KSI_PERMISSION_STORE_VERSION);

    for (size_t i = 0; i < store->count; i++) {
        const ksi_permission_record *record = &store->records[i];
        char *escaped_path;

        if (record->persistent_allowed_capabilities == 0u) {
            continue;
        }

        escaped_path = escape_field(record->exe_path);

        if (escaped_path == NULL) {
            fclose(file);
            (void)unlink(temp_path);
            free(temp_path);
            return -1;
        }

        (void)fprintf(
            file,
            "%lu\t%s\t%08x\t%llu\t%s\n",
            (unsigned long)record->uid,
            record->exe_hash,
            record->persistent_allowed_capabilities,
            (unsigned long long)record->last_seen_utc,
            escaped_path);
        free(escaped_path);
    }

    if (fflush(file) != 0) {
        fclose(file);
        (void)unlink(temp_path);
        free(temp_path);
        return -1;
    }

    fd = fileno(file);

    if (fd >= 0) {
        (void)fsync(fd);
    }

    if (fclose(file) != 0) {
        (void)unlink(temp_path);
        free(temp_path);
        return -1;
    }

    if (rename(temp_path, store->path) != 0) {
        (void)unlink(temp_path);
        free(temp_path);
        return -1;
    }

    free(temp_path);
    return 0;
}

static int load_store(ksi_permission_store *store)
{
    FILE *file;
    char *line = NULL;
    size_t line_capacity = 0u;
    ssize_t line_length;

    if (store == NULL || store->path == NULL) {
        return -1;
    }

    file = fopen(store->path, "r");

    if (file == NULL) {
        return errno == ENOENT ? 0 : -1;
    }

    while ((line_length = getline(&line, &line_capacity, file)) >= 0) {
        char *uid_text;
        char *hash_text;
        char *caps_text;
        char *seen_text;
        char *path_text;
        char *cursor;
        char *unescaped_path;
        unsigned long caps_value;
        unsigned long long seen_value;
        ksi_permission_record *record;

        while (line_length > 0 && (line[line_length - 1] == '\n' || line[line_length - 1] == '\r')) {
            line[--line_length] = '\0';
        }

        if (line[0] == '\0' || strcmp(line, KSI_PERMISSION_STORE_VERSION) == 0) {
            continue;
        }

        uid_text = line;
        hash_text = strchr(uid_text, '\t');

        if (hash_text == NULL) {
            continue;
        }

        *hash_text++ = '\0';
        caps_text = strchr(hash_text, '\t');

        if (caps_text == NULL) {
            continue;
        }

        *caps_text++ = '\0';
        seen_text = strchr(caps_text, '\t');

        if (seen_text == NULL) {
            continue;
        }

        *seen_text++ = '\0';
        path_text = strchr(seen_text, '\t');

        if (path_text == NULL) {
            continue;
        }

        *path_text++ = '\0';

        errno = 0;
        unsigned long uid_value = strtoul(uid_text, &cursor, 10);

        if (errno != 0 || cursor == uid_text || *cursor != '\0'
            || !looks_like_sha256(hash_text)) {
            continue;
        }

        errno = 0;
        caps_value = strtoul(caps_text, &cursor, 16);

        if (errno != 0 || cursor == caps_text || *cursor != '\0') {
            continue;
        }

        errno = 0;
        seen_value = strtoull(seen_text, &cursor, 10);

        if (errno != 0 || cursor == seen_text || *cursor != '\0') {
            continue;
        }

        unescaped_path = unescape_field(path_text);

        if (unescaped_path == NULL) {
            continue;
        }

        record = get_or_add_record(store, (uid_t)uid_value, hash_text, unescaped_path);
        free(unescaped_path);

        if (record == NULL) {
            continue;
        }

        record->persistent_allowed_capabilities = (uint32_t)caps_value;
        record->last_seen_utc = (uint64_t)seen_value;
    }

    free(line);
    fclose(file);

    if (prune_expired_records(store)) {
        return save_store(store);
    }

    return 0;
}

typedef struct ksi_prompt_environment {
    char *display;
    char *wayland_display;
    char *runtime_dir;
    char *dbus_session_bus_address;
    char *xauthority;
} ksi_prompt_environment;

static void free_prompt_environment(ksi_prompt_environment *environment)
{
    if (environment == NULL) {
        return;
    }

    free(environment->display);
    free(environment->wayland_display);
    free(environment->runtime_dir);
    free(environment->dbus_session_bus_address);
    free(environment->xauthority);
    memset(environment, 0, sizeof(*environment));
}

static void copy_prompt_environment_value(
    ksi_prompt_environment *environment,
    const char *name,
    const char *value)
{
    char **target = NULL;

    if (strcmp(name, "DISPLAY") == 0) {
        target = &environment->display;
    } else if (strcmp(name, "WAYLAND_DISPLAY") == 0) {
        target = &environment->wayland_display;
    } else if (strcmp(name, "XDG_RUNTIME_DIR") == 0) {
        target = &environment->runtime_dir;
    } else if (strcmp(name, "DBUS_SESSION_BUS_ADDRESS") == 0) {
        target = &environment->dbus_session_bus_address;
    } else if (strcmp(name, "XAUTHORITY") == 0) {
        target = &environment->xauthority;
    }

    if (target != NULL && *target == NULL && value[0] != '\0') {
        *target = strdup(value);
    }
}

static int read_prompt_environment(pid_t pid, ksi_prompt_environment *environment)
{
    char proc_path[64];
    char buffer[32768];
    ssize_t bytes_read;
    int fd;
    size_t offset = 0u;

    memset(environment, 0, sizeof(*environment));
    (void)snprintf(proc_path, sizeof(proc_path), "/proc/%ld/environ", (long)pid);
    fd = open(proc_path, O_RDONLY | O_CLOEXEC);

    if (fd < 0) {
        return -1;
    }

    bytes_read = read(fd, buffer, sizeof(buffer) - 1u);
    close(fd);

    if (bytes_read <= 0) {
        return -1;
    }

    buffer[bytes_read] = '\0';

    while (offset < (size_t)bytes_read) {
        char *entry = &buffer[offset];
        size_t length = strnlen(entry, (size_t)bytes_read - offset);
        char *equals;

        if (length == 0u) {
            offset++;
            continue;
        }

        equals = memchr(entry, '=', length);

        if (equals != NULL) {
            *equals = '\0';
            copy_prompt_environment_value(environment, entry, equals + 1);
        }

        offset += length + 1u;
    }

    return 0;
}

static void set_prompt_environment_value(const char *name, const char *value)
{
    if (value != NULL && value[0] != '\0') {
        (void)setenv(name, value, 1);
    }
}

static int run_prompt_command(
    const char *file_name,
    char *const argv[],
    pid_t requester_pid,
    uid_t requester_uid,
    gid_t requester_gid,
    char *output,
    size_t output_size)
{
    int pipe_fds[2];
    pid_t child;
    int wait_status;
    bool child_reaped = false;
    struct timespec deadline;
    ksi_prompt_environment environment;
    size_t output_used = 0;

    if (file_name == NULL || file_name[0] != '/' || access(file_name, X_OK) != 0
        || read_prompt_environment(requester_pid, &environment) != 0) {
        return -1;
    }

    if (pipe(pipe_fds) != 0) {
        free_prompt_environment(&environment);
        return -1;
    }

    child = fork();

    if (child < 0) {
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        free_prompt_environment(&environment);
        return -1;
    }

    if (child == 0) {
        struct passwd *user = getpwuid(requester_uid);
        int devnull = open("/dev/null", O_WRONLY | O_CLOEXEC);

        (void)dup2(pipe_fds[1], STDOUT_FILENO);

        if (devnull >= 0) {
            (void)dup2(devnull, STDERR_FILENO);
            close(devnull);
        }

        close(pipe_fds[0]);
        close(pipe_fds[1]);

        if (geteuid() == 0) {
            if (setgroups(0, NULL) != 0
                || setresgid(requester_gid, requester_gid, requester_gid) != 0
                || setresuid(requester_uid, requester_uid, requester_uid) != 0) {
                _exit(127);
            }
        }

        (void)clearenv();
        (void)setenv("PATH", "/usr/sbin:/usr/bin:/sbin:/bin", 1);

        if (user != NULL && user->pw_dir != NULL) {
            (void)setenv("HOME", user->pw_dir, 1);
        }

        set_prompt_environment_value("DISPLAY", environment.display);
        set_prompt_environment_value("WAYLAND_DISPLAY", environment.wayland_display);
        set_prompt_environment_value("XDG_RUNTIME_DIR", environment.runtime_dir);
        set_prompt_environment_value("DBUS_SESSION_BUS_ADDRESS", environment.dbus_session_bus_address);
        set_prompt_environment_value("XAUTHORITY", environment.xauthority);
        execv(file_name, argv);
        _exit(127);
    }

    free_prompt_environment(&environment);
    close(pipe_fds[1]);

    /* Set the read end non-blocking so we can drain it in the wait loop
     * without risking a block if the child hasn't written output yet. */
    {
        int flags = fcntl(pipe_fds[0], F_GETFL);

        if (flags >= 0) {
            (void)fcntl(pipe_fds[0], F_SETFL, flags | O_NONBLOCK);
        }
    }

    /* Monotonic deadline — immune to NTP steps and DST transitions. */
    if (clock_gettime(CLOCK_MONOTONIC, &deadline) != 0) {
        (void)kill(child, SIGTERM);
        (void)waitpid(child, &wait_status, 0);
        close(pipe_fds[0]);
        return -1;
    }

    deadline.tv_sec += (time_t)KSI_PROMPT_TIMEOUT_SECONDS;

    while (!child_reaped) {
        struct timespec now;
        pid_t result;

        /* Drain the pipe into the output buffer so the child never blocks
         * on write even if its output exceeds the pipe buffer size. */
        if (output != NULL && output_size > 0u) {
            ssize_t n;

            while (output_used < output_size - 1u) {
                n = read(pipe_fds[0], output + output_used, output_size - 1u - output_used);

                if (n > 0) {
                    output_used += (size_t)n;
                } else {
                    break;
                }
            }
        }

        result = waitpid(child, &wait_status, WNOHANG);

        if (result == child) {
            child_reaped = true;
            break;
        }

        if (result < 0) {
            break;
        }

        if (atomic_load(&g_prompt_cancelled)) {
            break;
        }

        if (clock_gettime(CLOCK_MONOTONIC, &now) != 0
            || now.tv_sec > deadline.tv_sec
            || (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec)) {
            break;
        }

        usleep(100000);
    }

    if (!child_reaped) {
        (void)kill(child, SIGTERM);
        (void)waitpid(child, &wait_status, 0);
    }

    /* Final drain: collect any output written after the last poll iteration. */
    if (output != NULL && output_size > 0u) {
        ssize_t n;

        while (output_used < output_size - 1u) {
            n = read(pipe_fds[0], output + output_used, output_size - 1u - output_used);

            if (n > 0) {
                output_used += (size_t)n;
            } else {
                break;
            }
        }

        output[output_used] = '\0';

        while (output_used > 0
               && (output[output_used - 1u] == '\n' || output[output_used - 1u] == '\r')) {
            output[--output_used] = '\0';
        }
    }

    close(pipe_fds[0]);

    if (child_reaped && WIFEXITED(wait_status)) {
        return WEXITSTATUS(wait_status);
    }

    return -1;
}

static void append_capability_line(char *buffer, size_t buffer_size, const char *line)
{
    size_t used = strlen(buffer);

    if (used >= buffer_size) {
        return;
    }

    (void)snprintf(buffer + used, buffer_size - used, "- %s\n", line);
}

static void describe_capabilities(uint32_t capabilities, char *buffer, size_t buffer_size)
{
    if (buffer == NULL || buffer_size == 0u) {
        return;
    }

    buffer[0] = '\0';

    if ((capabilities & KSI_CAP_HOOK_KEYBOARD) != 0u) {
        append_capability_line(buffer, buffer_size, "Hook keyboard input (includes keyboard synthesis)");
    } else if ((capabilities & KSI_CAP_SYNTH_KEYBOARD) != 0u) {
        append_capability_line(buffer, buffer_size, "Synthesize keyboard input");
    }

    if ((capabilities & KSI_CAP_HOOK_MOUSE) != 0u) {
        append_capability_line(buffer, buffer_size, "Hook mouse input (includes mouse synthesis)");
    } else if ((capabilities & KSI_CAP_SYNTH_MOUSE) != 0u) {
        append_capability_line(buffer, buffer_size, "Synthesize mouse input");
    }

    if ((capabilities & KSI_CAP_BLOCK_INPUT) != 0u) {
        append_capability_line(buffer, buffer_size, "Block or suppress input");
    }
}

int ksi_permissions_create(ksi_permission_store **store)
{
    ksi_permission_store *created;

    if (store == NULL) {
        return -1;
    }

    created = calloc(1, sizeof(*created));

    if (created == NULL) {
        return -1;
    }

    created->path = resolve_store_path();

    if (created->path == NULL) {
        free(created);
        return -1;
    }

    if (load_store(created) != 0) {
        ksi_permissions_destroy(created);
        return -1;
    }

    *store = created;
    return 0;
}

void ksi_permissions_destroy(ksi_permission_store *store)
{
    if (store == NULL) {
        return;
    }

    for (size_t i = 0; i < store->count; i++) {
        free_record(&store->records[i]);
    }

    free(store->records);
    free(store->path);
    free(store);
}

int ksi_permissions_identify_process(
    pid_t pid,
    char *path_buffer,
    size_t path_buffer_size,
    char *command_line_buffer,
    size_t command_line_buffer_size,
    char hash_buffer[KSI_PERMISSION_HASH_HEX_LENGTH + 1u])
{
    char proc_path[64];
    int fd;
    ssize_t path_length;

    if (path_buffer == NULL || path_buffer_size == 0u
        || command_line_buffer == NULL || command_line_buffer_size == 0u
        || hash_buffer == NULL || pid <= 0) {
        return -1;
    }

    path_buffer[0] = '\0';
    command_line_buffer[0] = '\0';
    hash_buffer[0] = '\0';

    (void)snprintf(proc_path, sizeof(proc_path), "/proc/%ld/exe", (long)pid);
    fd = open(proc_path, O_RDONLY | O_CLOEXEC);

    if (fd < 0) {
        return -1;
    }

    path_length = readlink(proc_path, path_buffer, path_buffer_size - 1u);

    if (path_length < 0) {
        close(fd);
        return -1;
    }

    path_buffer[path_length] = '\0';

    if (hash_process_identity(fd, pid, command_line_buffer, command_line_buffer_size, hash_buffer) != 0) {
        close(fd);
        return -1;
    }

    close(fd);
    return 0;
}

uint32_t ksi_permissions_get_allowed_capabilities(
    const ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash)
{
    ssize_t index;
    const ksi_permission_record *record;

    if (store == NULL || exe_hash == NULL) {
        return 0u;
    }

    index = find_record_index(store, uid, exe_hash);

    if (index < 0) {
        return 0u;
    }

    record = &store->records[index];
    return record->persistent_allowed_capabilities | record->session_allowed_capabilities;
}

void ksi_permissions_note_seen(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path)
{
    ssize_t index;
    ksi_permission_record *record;

    if (store == NULL || exe_hash == NULL) {
        return;
    }

    index = find_record_index(store, uid, exe_hash);

    if (index < 0) {
        return;
    }

    record = &store->records[index];
    record->last_seen_utc = utc_now_seconds();

    if (exe_path != NULL && exe_path[0] != '\0') {
        char *copy = strdup(exe_path);

        if (copy != NULL) {
            free(record->exe_path);
            record->exe_path = copy;
        }
    }

    if (record->persistent_allowed_capabilities != 0u) {
        (void)prune_expired_records(store);
        (void)save_store(store);
    }
}

int ksi_permissions_grant_session(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path,
    uint32_t capabilities)
{
    ksi_permission_record *record;

    if (store == NULL || capabilities == 0u) {
        return -1;
    }

    record = get_or_add_record(store, uid, exe_hash, exe_path);

    if (record == NULL) {
        return -1;
    }

    record->session_allowed_capabilities |= capabilities;
    return 0;
}

int ksi_permissions_grant_persistent(
    ksi_permission_store *store,
    uid_t uid,
    const char *exe_hash,
    const char *exe_path,
    uint32_t capabilities)
{
    ksi_permission_record *record;

    if (store == NULL || capabilities == 0u) {
        return -1;
    }

    record = get_or_add_record(store, uid, exe_hash, exe_path);

    if (record == NULL) {
        return -1;
    }

    record->persistent_allowed_capabilities |= capabilities;
    record->session_allowed_capabilities |= capabilities;
    (void)prune_expired_records(store);
    return save_store(store);
}

ksi_permission_decision ksi_permissions_prompt(
    pid_t pid,
    uid_t uid,
    gid_t gid,
    const char *exe_path,
    const char *command_line,
    const char *exe_hash,
    uint32_t capabilities)
{
    char capability_text[512];
    char prompt_text[2048];

    describe_capabilities(capabilities, capability_text, sizeof(capability_text));
    (void)snprintf(
        prompt_text,
        sizeof(prompt_text),
        "An application is requesting access to keysharp-inputd.\n\n"
        "Executable:\n%s\n\n"
        "Arguments:\n%s\n\n"
        "Permission identity hash:\n%s\n\n"
        "Requested access:\n%s\n"
        "This access can capture or synthesize keyboard/mouse input.",
        exe_path != NULL && exe_path[0] != '\0' ? exe_path : "(unknown)",
        command_line != NULL && command_line[0] != '\0' ? command_line : "(none)",
        exe_hash != NULL && exe_hash[0] != '\0' ? exe_hash : "(unknown)",
        capability_text[0] != '\0' ? capability_text : "- No capabilities requested\n");

    {
        char output[64];
        static const char *zenity_paths[] = { "/usr/bin/zenity", "/bin/zenity", NULL };
        static const char *kdialog_paths[] = { "/usr/bin/kdialog", "/bin/kdialog", NULL };

        for (size_t i = 0; zenity_paths[i] != NULL; i++) {
            char title_arg[] = "--title=Keysharp input permissions";
            char text_arg[sizeof(prompt_text) + 8u];
            char *argv[] = {
                "zenity",
                "--list",
                "--radiolist",
                title_arg,
                text_arg,
                "--width=640",
                "--height=360",
                "--column=Pick",
                "--column=Action",
                "TRUE",
                "Allow once",
                "FALSE",
                "Allow always",
                "FALSE",
                "Deny",
                NULL,
            };

            (void)snprintf(text_arg, sizeof(text_arg), "--text=%s", prompt_text);

            if (run_prompt_command(zenity_paths[i], argv, pid, uid, gid, output, sizeof(output)) == 0) {
                if (strcmp(output, "Allow always") == 0) {
                    return KSI_PERMISSION_DECISION_ALLOW_ALWAYS;
                }

                if (strcmp(output, "Allow once") == 0) {
                    return KSI_PERMISSION_DECISION_ALLOW_ONCE;
                }
            }
        }

        for (size_t i = 0; kdialog_paths[i] != NULL; i++) {
            char *argv[] = {
                "kdialog",
                "--title",
                "Keysharp input permissions",
                "--menu",
                prompt_text,
                "once",
                "Allow once",
                "always",
                "Allow always",
                "deny",
                "Deny",
                NULL,
            };

            if (run_prompt_command(kdialog_paths[i], argv, pid, uid, gid, output, sizeof(output)) == 0) {
                if (strcmp(output, "always") == 0) {
                    return KSI_PERMISSION_DECISION_ALLOW_ALWAYS;
                }

                if (strcmp(output, "once") == 0) {
                    return KSI_PERMISSION_DECISION_ALLOW_ONCE;
                }
            }
        }
    }

    fprintf(stderr, "inputd: permission prompt unavailable for pid=%ld uid=%ld\n", (long)pid, (long)uid);
    return KSI_PERMISSION_DECISION_DENY;
}
