typedef struct ksi_identify_task {
    ksi_daemon_command_queue *commands;
    pid_t pid;
    int client_fd;
    uint64_t connection_id;
} ksi_identify_task;

typedef struct ksi_prompt_task {
    ksi_daemon_command_queue *commands;
    int client_fd;
    uint64_t connection_id;
    uint32_t requested_capabilities;
    uint32_t missing_capabilities;
    uint32_t available_capabilities_at_start;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    pid_t pid;
    uid_t uid;
    gid_t gid;
} ksi_prompt_task;

static void *identify_worker(void *arg)
{
    ksi_identify_task *task = arg;
    ksi_daemon_command command;
    ksi_client_identified_result *result;

    result = calloc(1, sizeof(*result));

    if (result != NULL) {
        result->has_identity = ksi_permissions_identify_process(
            task->pid,
            result->exe_path, sizeof(result->exe_path),
            result->command_line, sizeof(result->command_line),
            result->exe_hash) == 0;
        result->start_time = ksi_permissions_get_process_start_time(task->pid);
    }

    memset(&command, 0, sizeof(command));
    command.type = KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED;
    command.client_fd = task->client_fd;
    command.connection_id = task->connection_id;
    command.data.identified.result = result;

    if (!command_queue_push(task->commands, &command)) {
        free(result);
    }

    free(task);
    return NULL;
}

static bool start_identify_task(
    ksi_daemon_command_queue *commands,
    pid_t pid,
    int client_fd,
    uint64_t connection_id)
{
    ksi_identify_task *task;
    task = malloc(sizeof(*task));

    if (task == NULL) {
        return false;
    }

    task->commands = commands;
    task->pid = pid;
    task->client_fd = client_fd;
    task->connection_id = connection_id;

    if (!ksi_worker_pool_submit(&g_identify_worker_pool, identify_worker, free, task)) {
        free(task);
        return false;
    }

    return true;
}

static void *prompt_worker(void *arg)
{
    ksi_prompt_task *task = arg;
    ksi_daemon_command command;

    memset(&command, 0, sizeof(command));
    command.type = KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE;
    command.client_fd = task->client_fd;
    command.connection_id = task->connection_id;
    command.data.prompt_done.decision =
        ksi_permissions_prompt(
            task->pid, task->uid, task->gid,
            task->exe_path, task->command_line, task->exe_hash, task->missing_capabilities);
    command.data.prompt_done.requested_capabilities = task->requested_capabilities;
    command.data.prompt_done.missing_capabilities = task->missing_capabilities;
    command.data.prompt_done.available_capabilities_at_start = task->available_capabilities_at_start;

    (void)command_queue_push(task->commands, &command);
    free(task);
    return NULL;
}

static bool start_prompt_task(
    ksi_daemon_command_queue *commands,
    int client_fd,
    uint64_t connection_id,
    uint32_t requested,
    uint32_t missing,
    uint32_t available_capabilities_at_start,
    const char *exe_path,
    const char *command_line,
    const char *exe_hash,
    pid_t pid,
    uid_t uid,
    gid_t gid)
{
    ksi_prompt_task *task;
    task = malloc(sizeof(*task));

    if (task == NULL) {
        return false;
    }

    task->commands = commands;
    task->client_fd = client_fd;
    task->connection_id = connection_id;
    task->requested_capabilities = requested;
    task->missing_capabilities = missing;
    task->available_capabilities_at_start = available_capabilities_at_start;
    (void)snprintf(task->exe_path, sizeof(task->exe_path), "%s", exe_path != NULL ? exe_path : "");
    (void)snprintf(
        task->command_line, sizeof(task->command_line), "%s",
        command_line != NULL ? command_line : "");
    (void)snprintf(task->exe_hash, sizeof(task->exe_hash), "%s", exe_hash != NULL ? exe_hash : "");
    task->pid = pid;
    task->uid = uid;
    task->gid = gid;

    if (!ksi_worker_pool_submit(&g_worker_pool, prompt_worker, free, task)) {
        free(task);
        return false;
    }

    return true;
}

/* --- End async workers --- */

