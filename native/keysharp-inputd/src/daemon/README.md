# Daemon ownership units

These private implementation units are included by `daemon.c` in dependency
order and compile as one translation unit. This keeps daemon-only types and
helpers private while making ownership boundaries explicit without introducing
an internal ABI.

- `privilege_workers.inc`: process identification and permission prompt jobs.
- `client_lifecycle.inc`: client removal and connection cleanup.
- `hook_lanes.inc`: bounded output sequencing, keyboard/mouse lanes, decisions,
  and lane shutdown.
- `grab_leases.inc`: capabilities, grabs, leases, fail-open state, and hook
  failure accounting.
- `hook_dispatch.inc`: physical event snapshotting, emergency replay, and
  backend hook callback dispatch.
- `protocol_server.inc`: protocol handlers, frame parsing, accept, and command
  result processing.

Standalone infrastructure with independently testable ownership lives beside
`daemon.c`:

- `connection_ref.c`: connection fd lifetime and per-connection send locking.
- `pipe_ring.c`: bounded pipe-woken inline ring.
- `worker_pool.c`: fixed worker threads, bounded jobs, and timed shutdown.

Do not add cross-unit globals. Shared daemon state and forward declarations
belong in `daemon.c`; reusable synchronization or lifecycle mechanisms belong
in standalone modules.
