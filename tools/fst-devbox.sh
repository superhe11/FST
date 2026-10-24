#!/usr/bin/env bash
# Compatibility entry point for the former Distrobox setup.
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
command="${1:-help}"
[[ $# == 0 ]] || shift
case "$command" in enter) command=shell ;; up) command=create ;; esac
printf 'FST now uses a Podman sandbox. See LINUX_DEV.md.\n' >&2
exec "$ROOT/tools/fst-sandbox.sh" "$command" "$@"
