#!/usr/bin/env bash
# Creates a project, installs this brick from its folder, targets the project at Android and exports it, the way a
# game's own pipeline would. Usage: android-smoke.sh <work folder>. TURIAN_DIR is the Turian checkout the CLI and the
# APK checks come from; by default the one beside the Bricks folder, as Directory.Build.props expects.
set -euo pipefail

work=$1
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
brick=$(dirname "$here")
turian=$(cd "${TURIAN_DIR:-$brick/../../Turian}" && pwd)
cli="${TURIAN_CLI:-dotnet $turian/Turian/Editor/CLI/bin/Debug/net11.0/Turian.Editor.CLI.dll}"
application=org.mass4.turian.androidsmoke

mkdir -p "$work"
cd "$work"
$cli new sample

sed -i 's|"org.mass4.turian.vulkan": "builtin:org.mass4.turian.vulkan"|&,\n    "org.mass4.turian.android": "file:'"$brick"'"|' sample/Bricks/manifest.json
profile=sample/Assets/Settings/Build/Client.dataasset
sed -i -e 's/"Name": "Client"/"Name": "Android"/' -e 's/"Platform": "desktop"/"Platform": "android"/' \
  -e 's/"RuntimeIdentifier": null/"RuntimeIdentifier": "android-arm64"/' -e 's/"Development": false/"Development": true/' "$profile"
sed -i "s/\"ApplicationIdentifier\": \"[^\"]*\"/\"ApplicationIdentifier\": \"$application\"/" sample/Assets/Settings/PlayerSettings.dataasset

# A new project has no lock yet, and the CLI restores locked whenever CI is true.
CI=false $cli brick restore --project sample
$cli export sample --profile Android --configuration Debug

apk=$(find sample/Output -name '*-Signed.apk' | head -n1)
test -n "$apk"
bash "$turian/ci/android-verify.sh" "$apk" "$application"
