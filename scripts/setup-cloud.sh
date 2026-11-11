#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"

# Сохраняем правила Git и обеспечиваем повторяемое получение веток.
git config --local --fixed-value --replace-all remote.origin.fetch \
    '+refs/heads/*:refs/remotes/origin/*' \
    '+refs/heads/*:refs/remotes/origin/*'
git fetch origin
git show-ref --verify --quiet refs/remotes/origin/main
git remote set-head origin main

python3 -c 'import sys; assert sys.version_info >= (3, 11), "Нужен Python 3.11 или новее"'
pulsar_venv="${PULSAR_VENV:-/workspace/.venvs/pulsar}"
python3 -m venv "$pulsar_venv"
"$pulsar_venv/bin/python" -m pip install --disable-pip-version-check --no-cache-dir \
    -r installer/requirements.txt
git status --short --branch
