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
git status --short --branch
