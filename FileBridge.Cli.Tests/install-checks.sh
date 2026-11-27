#!/bin/sh
set -eu

if [ ! -f /.dockerenv ]; then
    printf '%s\n' 'Run installation checks only in a disposable Docker container.' >&2
    exit 1
fi
filebridge_package=${1:?Pass the extracted Linux package directory.}
filebridge_bin="$HOME/.local/bin/filebridge"
filebridge_line='export PATH="$HOME/.local/bin:$PATH"'
if [ -e "$filebridge_bin" ] || [ -L "$filebridge_bin" ]; then
    printf '%s\n' 'Use a fresh container without an existing filebridge command.' >&2
    exit 1
fi
mkdir -p "$HOME/.local/bin"
printf '%s\n' 'existing command' >"$filebridge_bin"
if env SHELL=/bin/bash "$filebridge_package/install" >/tmp/filebridge-conflict.log 2>&1; then
    printf '%s\n' 'Installer overwrote an existing regular file.' >&2
    exit 1
fi
test "$(cat "$filebridge_bin")" = 'existing command'
rm "$filebridge_bin"

for filebridge_shell in bash zsh sh; do
    case "$filebridge_shell" in
        bash) filebridge_profile="$HOME/.bashrc" ;;
        zsh) filebridge_profile="$HOME/.zshrc" ;;
        sh) filebridge_profile="$HOME/.profile" ;;
    esac
    env SHELL="/bin/$filebridge_shell" "$filebridge_package/install"
    env SHELL="/bin/$filebridge_shell" "$filebridge_package/install"
    test "$(grep -Fxc "$filebridge_line" "$filebridge_profile")" -eq 1
done
test "$(readlink "$filebridge_bin")" = "$filebridge_package/filebridge"
bash -ic 'command -v filebridge && filebridge --help >/dev/null'
printf '%s\n' 'PASS installer / regular-file conflict / repeat install / Bash-Zsh-POSIX PATH entries / command startup'
FILEBRIDGE_CLI_EXECUTABLE="$filebridge_bin" dotnet /work/FileBridge.Cli.Tests/bin/Release/net10.0/FileBridge.Cli.Tests.dll
