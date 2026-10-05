#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build dist
sdk="$(xcrun --sdk macosx --show-sdk-path)"
sources=(Sources/Quota.swift Sources/AppServer.swift Sources/Anchor.swift Sources/QuotaView.swift Sources/main.swift)
for arch in arm64 x86_64; do
  xcrun swiftc -swift-version 5 -O -sdk "$sdk" -target "$arch-apple-macosx13.0" \
    -framework AppKit -framework ApplicationServices -framework ServiceManagement \
    "${sources[@]}" -o "build/CodexQuotaBar-$arch"
done
app="dist/CodexQuotaBar.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
lipo -create build/CodexQuotaBar-arm64 build/CodexQuotaBar-x86_64 -output "$app/Contents/MacOS/CodexQuotaBar"
cp Info.plist "$app/Contents/Info.plist"
cp ../docs/macos.html "$app/Contents/Resources/使用说明.html"
cp ../docs/macos.md dist/使用说明.md
codesign --force --sign - "$app"
codesign --verify --strict "$app"
lipo -verify_arch arm64 x86_64 "$app/Contents/MacOS/CodexQuotaBar"
ditto -c -k --sequesterRsrc --keepParent "$app" dist/CodexQuotaBar-macOS.zip
echo "Built universal app: $app"
