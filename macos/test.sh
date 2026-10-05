#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
xcrun swiftc -swift-version 5 Sources/Quota.swift Sources/AppServer.swift Tests/main.swift -o build/quota-tests
build/quota-tests
