#ifndef KEYSHARP_INPUTD_LINUX_DEVICE_FILTER_H
#define KEYSHARP_INPUTD_LINUX_DEVICE_FILTER_H

#include <stdbool.h>
#include <string.h>

/* Select the first (nearest) ID_SEAT found while walking from an event node to
 * its parents.  A property on the event node must win over a conflicting
 * parent property; a missing child property falls back to the parent. */
static inline const char *ksi_linux_device_prefer_nearest_id_seat(
    const char *nearest_id_seat,
    const char *candidate_id_seat)
{
    return nearest_id_seat != NULL ? nearest_id_seat : candidate_id_seat;
}

/* systemd-logind assigns initialized devices without ID_SEAT to the default
 * seat.  Absence is meaningful only after udev has finished processing the
 * device: an uninitialized object may merely be missing ID_SEAT because the
 * lookup raced udev, so it must fail closed. Keep these policy predicates
 * independent of libudev so both discovery paths use identical semantics and
 * the boundary values remain directly testable. */
static inline bool ksi_linux_device_id_seat_is_seat0(const char *id_seat)
{
    return id_seat == NULL || strcmp(id_seat, "seat0") == 0;
}

static inline bool ksi_linux_device_seat_metadata_is_admitted(
    bool metadata_initialized,
    const char *id_seat)
{
    return metadata_initialized && ksi_linux_device_id_seat_is_seat0(id_seat);
}

#endif
