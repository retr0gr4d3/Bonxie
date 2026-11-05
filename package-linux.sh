#!/usr/bin/env bash
# Builds the Linux release: a self-contained linux-x64 Bonxie with its game player, skua.swf and plugins,
# packed as Build/Bonxie-<version>-linux-x64.tar.gz (one Bonxie/ folder; extract it and run ./Bonxie.Manager),
# plus, when vpk is installed, a self-updating Velopack AppImage in Build/Releases-linux.
#
#   ./package-linux.sh            (version from Directory.Build.props)
#   ./package-linux.sh 2.2.0
#
# Set SkuaPlayerLocal=<folder with the Mugetsu binary> to use a local player instead of downloading it.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)}"
out="$root/Build/linux-x64"

rm -rf "$out"
dotnet publish "$root/Skua.App.Avalonia/Skua.App.Avalonia.csproj" -c Release -r linux-x64 --self-contained true -o "$out"
# The manager is the launcher (and Velopack entry point); it starts ./Bonxie from the same folder.
dotnet publish "$root/Skua.Manager.Avalonia/Skua.Manager.Avalonia.csproj" -c Release -r linux-x64 --self-contained true -o "$out"

# Plugins next to the app are copied into the data folder at startup (see PluginManager).
mkdir -p "$out/plugins"
for plugin in Skua.Plugin.DailyTracker Skua.Plugin.ButlerV5 Skua.Plugin.Cosmetics; do
    dotnet build "$root/$plugin/$plugin.csproj" -c Release
done
cp "$root/Skua.Plugin.DailyTracker/bin/Release/net10.0/Skua.Plugin.DailyTracker.dll" \
   "$root/Skua.Plugin.ButlerV5/bin/Release/net10.0/ButlerV5.dll" \
   "$root/Skua.Plugin.Cosmetics/bin/Release/net10.0/Skua.Plugin.Cosmetics.dll" \
   "$out/plugins/"

chmod +x "$out/Bonxie" "$out/Bonxie.Manager" "$out/player/Mugetsu"
if [[ -f "$out/player-x86-64-v2/Mugetsu" ]]; then chmod +x "$out/player-x86-64-v2/Mugetsu"; fi
archive="$root/Build/Bonxie-$version-linux-x64.tar.gz"
tar -C "$root/Build" --transform 's,^linux-x64,Bonxie,' --owner=0 --group=0 --numeric-owner -czf "$archive" linux-x64
echo "Built $archive"

# Velopack AppImage + update feed. The channel must match ClientUpdateService.Channel; it also keeps
# the nupkg/releases json names apart from the Windows ones in the same GitHub release.
if command -v vpk >/dev/null; then
    rm -rf "$root/Build/Releases-linux"
    vpk pack -u Bonxie -v "$version" -p "$out" -e Bonxie.Manager --channel linux-x64 \
        --packTitle "Bonxie Manager" -i "$root/Skua.Shared.Avalonia/Assets/SkuaImage.png" \
        -o "$root/Build/Releases-linux"
    echo "Built Velopack release in $root/Build/Releases-linux"
else
    echo "vpk not found; skipped the AppImage (dotnet tool install -g vpk)"
fi
