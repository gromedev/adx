#!/bin/bash
# One-shot auto-continue for the adx-runner container. Fire this from cron at whatever
# wall-clock time the usage-limit reset is expected. Equivalent of typing "continue" in the
# container's Claude Code session after the limit resets, automated.
#
# Self-removing when installed via crontab: the crontab entry (marker: adx-runner-auto-continue)
# is deleted first thing, so a cron-triggered run fires exactly once no matter what happens
# afterwards. Running it by hand (e.g. to test it) has the same side effect - it will remove
# that crontab entry if one exists, so re-install the cron entry afterward if you still want
# the scheduled run.
#
# Never touches git remotes; the container has none.

D=/usr/local/bin/docker
LOG=/Users/thomas/git/max/.github/adx-runner-out/agent.log

# Remove our own crontab entry immediately - "run once" must hold even if the rest fails.
crontab -l 2>/dev/null | grep -v adx-runner-auto-continue | crontab - 2>/dev/null

echo "[$(date '+%F %T')] one-shot auto-continue firing"

# Container present? Start it if Docker is up but the container is stopped.
state=$($D inspect -f '{{.State.Running}}' adx-runner 2>/dev/null) || { echo "container missing; nothing to do"; exit 0; }
if [ "$state" != "true" ]; then
    $D start adx-runner >/dev/null 2>&1 || { echo "docker not available; giving up"; exit 0; }
    sleep 3
fi

# Never start a second runner beside a live one (two agents in one tree - been there).
# Match string is concatenated so this command's own cmdline never matches itself.
active=$($D exec adx-runner bash -c 'a="run"; b="ner.sh"
for p in /proc/[0-9]*; do
    c=$(tr "\0" " " < "$p/cmdline" 2>/dev/null) || continue
    case "$c" in *"$a$b"*) echo yes; break;; esac
done')
if [ "$active" = "yes" ]; then echo "runner already active; nothing to do"; exit 0; fi

# Respect terminal-by-design stops - a "continue" adds nothing to a finished or blocked run.
last=$(grep "STOP:" "$LOG" 2>/dev/null | tail -1)
case "$last" in
    *"plan complete"*) echo "runner finished (plan complete); not restarting"; exit 0 ;;
    *"blocked"*)       echo "runner reported blocked; not restarting";        exit 0 ;;
esac

echo "restarting runner (last stop: ${last:-none})"
$D exec -d adx-runner bash /runner/runner.sh
echo "restarted"
