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
- `hook_ingress.inc`: physical/synthetic hook ingress fairness and Send input
  conversion into hook events.
- `protocol_server.inc`: protocol handlers, frame parsing, accept, and command
  result processing.

Standalone infrastructure with independently testable ownership lives beside
`daemon.c`:

- `connection_ref.c`: connection lifetime, serialized writes, and the per-script
  keyboard/mouse callback call stack.
- `pipe_ring.c`: bounded pipe-woken inline ring.
- `synthetic_hooks.c`: pure expansion of SendInput records into the individual
  low-level hook events observed by Windows callbacks.
- `wake_pipe.c`: nonblocking self-pipe lifecycle and draining shared by queues.
- `worker_pool.c`: fixed worker threads, bounded jobs, and timed shutdown.

Do not add cross-unit globals. Shared daemon state and forward declarations
belong in `daemon.c`; reusable synchronization or lifecycle mechanisms belong
in standalone modules.

## Concurrency invariants

- The main thread exclusively owns `clients[]`, subscription snapshots, device
  discovery, and protocol parsing.
- Each hook lane owns its current event context. Other threads communicate with
  it only through bounded action, decision, and nested-transaction queues.
- One `ksi_hook_send_ref` owns each script's shared keyboard/mouse callback stack.
  Root turns enter only an empty stack; recursive turns enter only while the top
  callback is synchronously pumping Send.
- The output sequencer is the only thread that writes to or recreates uinput
  devices. Admission order is fixed before work reaches that thread.
- A `ksi_synth_completion` counts every admitted fragment; exactly the transition
  from one to zero releases the atomic-transaction count and destroys it. Recursive
  completions also own the HookStream reply reference; ordinary batches are detached
  because their RPC already acknowledged admission.
- `flush_generation` invalidates queued snapshots during fail-open or teardown;
  stale events may release resources but must not invoke callbacks or output
  synthetic replacements.
- `active_input_generation` independently fences every queued output across a
  seat-owner transition.
- Each entered callback has its own monotonic deadline. A parent's deadline is
  suspended while recursive child transactions consume their own turns.

The white-box CTest target includes `daemon.c` in a test translation unit so it
can exercise private queue and ownership invariants without exporting a
daemon-internal API. Reusable modules such as `pipe_ring` and `synthetic_hooks`
have separate black-box test executables. Keep production symbols private; add
focused module tests when changing expansion or synchronization behavior and
white-box tests for daemon-only admission, completion, or ownership rules.
