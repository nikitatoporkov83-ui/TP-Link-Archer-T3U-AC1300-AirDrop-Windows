#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"

# Preserve existing refspecs and make branch discovery repeatable.
git config --local --fixed-value --replace-all remote.origin.fetch \
    '+refs/heads/*:refs/remotes/origin/*' \
    '+refs/heads/*:refs/remotes/origin/*'
git fetch origin
git show-ref --verify --quiet refs/remotes/origin/main
git remote set-head origin main

python3 -c 'import sys; assert sys.version_info >= (3, 11), "Python 3.11+ is required"'
pulseit_venv="${PULSEIT_VENV:-/workspace/.venvs/pulseit}"
python3 -m venv "$pulseit_venv"
"$pulseit_venv/bin/python" -m pip install --disable-pip-version-check --no-cache-dir \
    -r installer/requirements.txt
git status --short --branch
