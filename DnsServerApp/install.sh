#!/bin/sh
set -eu
installer=$(mktemp)
trap 'rm -f "$installer"' EXIT
curl --fail --location --proto '=https' --proto-redir '=https' https://github.com/tekmyster/Tectanium-DDI-Server/releases/download/v0.1.0-alpha.2/install.sh -o "$installer"
exec bash "$installer" "$@"
