#!/usr/bin/env bash
set -euo pipefail
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
git -C "$fixture" init --quiet
git -C "$fixture" config user.name 'Calendar test'
git -C "$fixture" config user.email 'calendar-test@example.invalid'

check() {
    local author=$1 committer=$2 expected=$3 actual
    GIT_AUTHOR_DATE="$author" GIT_COMMITTER_DATE="$committer" git -C "$fixture" -c commit.gpgsign=false commit --quiet --allow-empty -m fixture
    actual=$(cd -- "$fixture" && bash "$script_directory/calendar-version.sh" HEAD 42)
    if [[ "$actual" != "$expected.42" ]]; then
        printf 'expected %s.42, got %s\n' "$expected" "$actual" >&2
        exit 1
    fi
}

check '2026-10-01T20:59:59Z' '2026-10-02T10:59:59+1400' '2026.10.1'
check '2026-10-01T21:00:00Z' '2026-10-01T11:00:00-1000' '2026.10.2'
check '2026-10-01T23:30:00Z' '2026-10-02T13:30:00+1400' '2026.10.2'
check '2026-10-02T00:30:00+1400' '2026-10-01T00:30:00-1000' '2026.10.1'
printf 'Calendar version boundary checks passed.\n'
