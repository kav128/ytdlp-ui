#!/usr/bin/env bash
set -euo pipefail
commit=${1:?commit is required}
run_number=${2:?run number is required}
if [[ ! "$run_number" =~ ^[0-9]+$ ]]; then
    printf 'run number must be a non-negative integer\n' >&2
    exit 1
fi
calendar=$(TZ=Europe/Moscow git show --no-patch --format=%ad --date=format-local:'%Y.%-m.%-d' "$commit")
printf '%s.%s\n' "$calendar" "$run_number"
