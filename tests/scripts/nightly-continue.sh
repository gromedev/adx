#!/bin/bash
# Nightly auto-continue, run BY CRON INSIDE THIS CONTAINER (not the host).
#
# Runs the runner loop if it is not already active and the previous run did not stop for a
# terminal-by-design reason (plan complete / blocked). Idempotent - safe for cron to fire
# every night indefinitely; it is a no-op every time except right after a usage-limit stop,
# so no self-removal step is needed.
#
# Why in-container rather than on the host: the host's macOS `crontab` WRITE hung repeatedly
# during testing (reads were instant; writes took 2+ minutes or never returned - almost
# certainly a Full Disk Access / TCC gate on the terminal's process, which cannot be fixed
# from inside a script). This script never touches crontab at all, so that failure mode does
# not apply here. Installed once via `crontab` for the agent user; after that, cron itself
# invokes this file - nothing here ever rewrites the schedule.

set -u
LOG=/out/agent.log

echo "===== nightly-continue fired $(date '+%F %T') =====" >> "$LOG"

active=0
for p in /proc/[0-9]*; do
    c=$(tr '\0' ' ' < "$p/cmdline" 2>/dev/null) || continue
    case "$c" in *"runner.sh"*) active=1; break;; esac
done
if [ "$active" = 1 ]; then
    echo "[$(date '+%F %T')] nightly-continue: runner already active, no-op" >> "$LOG"
    exit 0
fi

last=$(grep "STOP:" "$LOG" 2>/dev/null | tail -1)
case "$last" in
    *"plan complete"*)
        echo "[$(date '+%F %T')] nightly-continue: plan already complete, no-op" >> "$LOG"
        exit 0 ;;
    *"blocked"*)
        echo "[$(date '+%F %T')] nightly-continue: agent reported blocked, no-op" >> "$LOG"
        exit 0 ;;
esac

echo "[$(date '+%F %T')] nightly-continue: runner idle (last stop: ${last:-none}) - restarting" >> "$LOG"
nohup bash /runner/runner.sh >/dev/null 2>&1 &
disown
