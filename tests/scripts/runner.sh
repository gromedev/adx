#!/bin/bash
# Autonomous ADx loop: kickoff once (tracked via marker so restarts resume with -c),
# then `claude -c -p` until done / blocked / usage limit.
set -u
LOG=/out/agent.log
MARK=/home/agent/.kickoff-sent
MODELARGS="--model claude-fable-5 --fallback-model claude-fable-5 --effort max"
cd /work/max

echo "===== runner start $(date -u '+%F %T')  [claude-fable-5, effort=max] =====" >> "$LOG"

iter=0
fails=0
while [ $iter -lt 100 ]; do
  iter=$((iter+1))
  echo "" >> "$LOG"
  echo "########## iteration $iter  $(date -u '+%F %T') ##########" >> "$LOG"

  if [ ! -f "$MARK" ]; then
    claude -p "$(cat /runner/kickoff.md)" $MODELARGS --dangerously-skip-permissions >> "$LOG" 2>&1
    rc=$?
    touch "$MARK"
  else
    claude -c -p "Continue executing the ADx plan per /runner/kickoff.md. Re-read -PLANS/ADx-progress.md to re-orient. Remember: local commits only, never push, and the status tokens are only for their exact meanings." $MODELARGS --dangerously-skip-permissions >> "$LOG" 2>&1
    rc=$?
  fi
  echo "---------- iteration $iter rc=$rc ----------" >> "$LOG"

  # Always keep a retrievable snapshot of the work so far.
  git bundle create /out/max-adx.bundle --all >> /dev/null 2>&1
  cp -f ./-PLANS/ADx-progress.md /out/ADx-progress.md 2>/dev/null

  tailtxt=$(tail -c 20000 "$LOG")
  case "$tailtxt" in
    *"PLAN COMPLETE"*) echo "STOP: plan complete" >> "$LOG"; break ;;
    *"ADX-BLOCKED"*)   echo "STOP: agent reports blocked" >> "$LOG"; break ;;
  esac
  if printf '%s' "$tailtxt" | grep -qiE "usage limit|limit reached|rate.?limit"; then
    echo "STOP: usage limit" >> "$LOG"
    break
  fi

  if [ $rc -ne 0 ]; then fails=$((fails+1)); else fails=0; fi
  if [ $fails -ge 3 ]; then echo "STOP: 3 consecutive non-zero exits" >> "$LOG"; break; fi

  sleep 10
done

git bundle create /out/max-adx.bundle --all >> "$LOG" 2>&1
cp -f ./-PLANS/ADx-progress.md /out/ADx-progress.md 2>/dev/null
echo "===== runner end $(date -u '+%F %T') =====" >> "$LOG"
