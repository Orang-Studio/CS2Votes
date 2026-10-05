#!/usr/bin/env bash
# Prints the plugin version. Fails when the .csproj <Version> and ModuleVersion in the code differ,
# or when a tag is given (v1.2.3) that does not match them.
set -euo pipefail
cd "$(dirname "$0")/.."
csproj=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' src/CS2Votes.csproj)
module=$(sed -n 's|.*ModuleVersion => "\(.*\)";.*|\1|p' src/CS2Votes.cs)
if [[ -z "$csproj" || "$csproj" != "$module" ]]; then
    echo "Version mismatch: CS2Votes.csproj has '$csproj', CS2Votes.cs ModuleVersion has '$module'." >&2
    exit 1
fi
if [[ $# -gt 0 && "${1#v}" != "$csproj" ]]; then
    echo "Tag '$1' does not match the plugin version '$csproj'." >&2
    exit 1
fi
echo "$csproj"