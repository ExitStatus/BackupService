#!/usr/bin/env bash
#
# Reusable version-injection step for the CI workflows.
#
# Composes VERSION=MAJOR.MINOR.BUILD and exports it to:
#   - $GITHUB_ENV    so later `run:` steps see $VERSION
#   - $GITHUB_OUTPUT so steps can use ${{ steps.<id>.outputs.version }}
#
#   BUILD  = $GITHUB_RUN_NUMBER  (GitHub's global, monotonically-increasing run counter).
#   MAJOR/MINOR are taken from a `release/<major>.<minor>` branch name when the run is on such a
#     branch (release builds), otherwise from the MAJOR/MINOR environment variables (e.g. the
#     nightly workflow sets both to 0, giving 0.0.<build>).
set -euo pipefail

: "${GITHUB_RUN_NUMBER:?GITHUB_RUN_NUMBER must be set}"

# Release builds derive MAJOR.MINOR from the branch name (release/<major>.<minor>);
# other builds (e.g. nightly) supply MAJOR/MINOR via env.
ref="${GITHUB_REF_NAME:-}"
if [[ "$ref" == release/* ]]; then
  spec="${ref#release/}"
  if [[ ! "$spec" =~ ^([0-9]+)\.([0-9]+)$ ]]; then
    echo "Release branch '$ref' must be named release/<major>.<minor> (e.g. release/1.2)" >&2
    exit 1
  fi
  MAJOR="${BASH_REMATCH[1]}"
  MINOR="${BASH_REMATCH[2]}"
fi

: "${MAJOR:?MAJOR must be set (or use a release/<major>.<minor> branch)}"
: "${MINOR:?MINOR must be set (or use a release/<major>.<minor> branch)}"

VERSION="${MAJOR}.${MINOR}.${GITHUB_RUN_NUMBER}"
echo "VERSION=${VERSION}" >> "$GITHUB_ENV"
echo "version=${VERSION}" >> "$GITHUB_OUTPUT"
echo "Computed version: ${VERSION}"
