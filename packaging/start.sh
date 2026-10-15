#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec dotnet DnsServerApp.dll --portable-app "$@"
