#!/usr/bin/env bash
set -Eeuo pipefail

# ==========================================================
# BONXIE FAST LOCAL BUILD (LINUX)
# - Builds the Linux client and plugins only.
# - Manager is excluded (Windows-only).
# - Does not pull, switch branches, or modify Git state.
# - Uses the existing Linux publish layout under Build/linux-x64.
# - Builds individual projects to support RuntimeIdentifier.
# ==========================================================

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$ROOT_DIR/Build"
OUTPUT_DIR="$BUILD_DIR/linux-x64"

AS3_TEMP_SWF=""
BUILD_PROGRESS=0

BUILD_WORKERS="${BUILD_WORKERS:-$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}"

DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO

START_NS="$(date +%s%N)"

# ----------------------------------------------------------
# PROJECTS
# ----------------------------------------------------------

PROJECTS=(
    "$ROOT_DIR/Skua.App.Avalonia/Skua.App.Avalonia.csproj"
    "$ROOT_DIR/Skua.Plugin.DailyTracker/Skua.Plugin.DailyTracker.csproj"
    "$ROOT_DIR/Skua.Plugin.ButlerV5/Skua.Plugin.ButlerV5.csproj"
    "$ROOT_DIR/Skua.Plugin.Cosmetics/Skua.Plugin.Cosmetics.csproj"
)

# ----------------------------------------------------------
# PROGRESS
# ----------------------------------------------------------

progress() {
    local percent="$1"
    local message="$2"
    local width=40
    local position=$((width * percent / 100))
    local bar=""
    local odd
    local i

    (( position < 1 )) && position=1
    (( percent >= 100 )) && position=$((width + 1))

    for ((i = 1; i <= width; i++)); do
        odd=$((i % 2))

        if (( i < position )); then
            bar+="-"
        elif (( i == position )); then
            if (( odd == 1 )); then
                bar+="C"
            else
                bar+="c"
            fi
        elif (( odd == 1 )); then
            bar+="o"
        else
            bar+=" "
        fi
    done

    printf '\033[36m[%s]\033[0m %3d%%  \033[97m%s\033[0m\n' \
        "$bar" "$percent" "$message"

    BUILD_PROGRESS="$percent"
}

# ----------------------------------------------------------
# CLEANUP
# ----------------------------------------------------------

cleanup() {
    if [[ -n "$AS3_TEMP_SWF" && -f "$AS3_TEMP_SWF" ]]; then
        rm -f -- "$AS3_TEMP_SWF"
    fi
}

finish() {
    local status="$1"
    local end_ns elapsed_ms total_seconds
    local hours minutes seconds centiseconds

    trap - EXIT
    cleanup

    if (( status == 0 )); then
        progress 100 "Build completed successfully"
    else
        progress "$BUILD_PROGRESS" "Build failed"
    fi

    end_ns="$(date +%s%N)"
    elapsed_ms=$(( (end_ns - START_NS) / 1000000 ))
    (( elapsed_ms < 0 )) && elapsed_ms=0

    total_seconds=$((elapsed_ms / 1000))
    hours=$((total_seconds / 3600))
    minutes=$(((total_seconds / 60) % 60))
    seconds=$((total_seconds % 60))
    centiseconds=$(((elapsed_ms % 1000) / 10))

    printf '\n==========================================================\n'
    printf '                    BUILD SUMMARY\n'
    printf '==========================================================\n\n'

    printf 'Total build time: %dh %dm %d.%02ds\n\n' \
        "$hours" "$minutes" "$seconds" "$centiseconds"

    if (( status == 0 )); then
        printf 'Status: SUCCESS\n'
        printf 'Output: Build/linux-x64/\n'
    else
        printf 'Status: FAILED\n'
    fi

    printf '\n==========================================================\n'

    exit "$status"
}

fail() {
    printf '\nERROR: %s\n' "$1" >&2
    exit 1
}

trap 'finish $?' EXIT

# ----------------------------------------------------------
# INITIALIZATION
# ----------------------------------------------------------

cd "$ROOT_DIR"

progress 0 "Initializing Linux local build"

CPU_MODEL="$(
    awk -F: '/model name/ {
        sub(/^[ \t]+/, "", $2)
        print $2
        exit
    }' /proc/cpuinfo 2>/dev/null || true
)"

MEMORY_GB="$(
    awk '/MemTotal/ {
        printf "%.0f GB", $2 / 1024 / 1024
    }' /proc/meminfo 2>/dev/null || true
)"

[[ -n "$CPU_MODEL" ]] || CPU_MODEL="Unknown CPU"
[[ -n "$MEMORY_GB" ]] || MEMORY_GB="Unknown"

printf '\n==========================================================\n'
printf '                 BONXIE LINUX LOCAL BUILD\n'
printf '==========================================================\n'
printf ' CPU:       %s\n' "$CPU_MODEL"
printf ' Workers:   %s MSBuild workers\n' "$BUILD_WORKERS"
printf ' Memory:    %s\n' "$MEMORY_GB"
printf ' Mode:      Release / linux-x64 / Parallel / Incremental\n'
printf ' Git:       No pull or checkout\n'
printf ' Target:    Linux client + plugins (no Manager)\n'
printf '==========================================================\n\n'

# ----------------------------------------------------------
# VALIDATION
# ----------------------------------------------------------

printf 'Checking repository and build tools...\n'

git rev-parse --show-toplevel >/dev/null 2>&1 \
    || fail "Run this script from inside the Bonxie Git repository."

[[ -f "$ROOT_DIR/Skua.sln" ]] \
    || fail "Skua.sln was not found."

command -v dotnet >/dev/null 2>&1 \
    || fail ".NET SDK was not found in PATH."

command -v cargo >/dev/null 2>&1 \
    || fail "Rust cargo was not found in PATH (needed for Skua.X11Host)."

for project in "${PROJECTS[@]}"; do
    [[ -f "$project" ]] \
        || fail "Project file not found: $project"
done

BRANCH="$(git branch --show-current 2>/dev/null || true)"
[[ -n "$BRANCH" ]] || BRANCH="detached HEAD"

printf 'Current branch: %s\n' "$BRANCH"
printf 'Working tree changes will be built as-is.\n'

progress 8 "Repository and SDK validated"

# ----------------------------------------------------------
# STOP RUNNING INSTANCES
# ----------------------------------------------------------

printf '\nClosing running Bonxie and Mugetsu instances...\n'

if command -v pkill >/dev/null 2>&1; then
    pkill -x Bonxie 2>/dev/null || true
    pkill -x Mugetsu 2>/dev/null || true
fi

mkdir -p "$BUILD_DIR"

progress 15 "Running instances stopped"

# ----------------------------------------------------------
# AS3 CLIENT
# ----------------------------------------------------------

printf '\nChecking AS3 client...\n'

AS3_SWF="$ROOT_DIR/Skua.AS3/skua/bin/skua.swf"

AS3_TEMP_SWF="$ROOT_DIR/Skua.AS3/skua/bin/skua.build-${RANDOM}-${RANDOM}.tmp.swf"

AS3_REBUILD=0

[[ -f "$AS3_SWF" ]] || AS3_REBUILD=1

if ! git diff --quiet HEAD -- "Skua.AS3/skua/bin/skua.swf"; then
    AS3_REBUILD=1
fi

if (( AS3_REBUILD == 1 )); then

    command -v pwsh >/dev/null 2>&1 \
        || fail "AS3 sources changed or skua.swf is missing, but PowerShell 7 (pwsh) is not installed."

    printf 'AS3 changes detected. Compiling client...\n'

    if ! pwsh -NoLogo -NoProfile \
        -File "$ROOT_DIR/Skua.AS3/compile-as3.ps1" \
        -OutputPath "$AS3_TEMP_SWF"; then

        rm -f -- "$AS3_TEMP_SWF"

        fail "AS3 compilation failed; the existing skua.swf was preserved."
    fi

    [[ -s "$AS3_TEMP_SWF" ]] \
        || fail "AS3 compilation did not produce a SWF file."

    mv -f -- "$AS3_TEMP_SWF" "$AS3_SWF" \
        || fail "Failed to replace skua.swf with the compiled output."

    AS3_TEMP_SWF=""

else
    printf 'AS3 client unchanged. Skipping compilation.\n'
fi

progress 23 "AS3 client ready"

# ----------------------------------------------------------
# PREPARE BUILD TARGETS
# ----------------------------------------------------------

printf '\nPreparing Linux build targets...\n'

# Remove only our plugin outputs so stale DLLs cannot be packaged.

mkdir -p "$BUILD_DIR/plugins"

rm -f -- \
    "$BUILD_DIR/plugins/Skua.Plugin.DailyTracker.dll" \
    "$BUILD_DIR/plugins/ButlerV5.dll" \
    "$BUILD_DIR/plugins/Skua.Plugin.Cosmetics.dll"

progress 28 "Linux build targets prepared"

# ----------------------------------------------------------
# RESTORE
# ----------------------------------------------------------

printf '\nRestoring NuGet dependencies for Linux projects...\n'

for project in "${PROJECTS[@]}"; do
    project_name="$(basename "$(dirname "$project")")"

    printf 'Restoring %s...\n' "$project_name"

    dotnet restore "$project" \
        -r linux-x64 \
        --verbosity quiet \
        --nologo \
        || fail "NuGet restore failed for $project_name."
done

progress 38 "NuGet restore complete"

# ----------------------------------------------------------
# BUILD
# ----------------------------------------------------------

printf '\n==========================================================\n'
printf '             COMPILING BONXIE LINUX PROJECTS\n'
printf '==========================================================\n\n'

printf 'Building the Linux client and three plugins with %s MSBuild workers.\n' "$BUILD_WORKERS"
printf 'The Manager is intentionally excluded because the Linux release does not include it yet.\n'
printf 'Incremental compilation and shared compiler are enabled.\n\n'

progress 42 "Compiling native X11 host and Linux projects"

printf '\nBuilding native X11 host (Skua.X11Host)...\n'
cargo build --release --manifest-path "$ROOT_DIR/Skua.X11Host/Cargo.toml" \
    || fail "Skua.X11Host build failed."

# Build each project individually.
# MSBuild handles project references and dependency ordering.

for project in "${PROJECTS[@]}"; do

    project_name="$(basename "$(dirname "$project")")"

    printf '%s\n' "" "----------------------------------------------------------"
    printf 'Building: %s\n' "$project_name"
    printf '%s\n' "----------------------------------------------------------"

    dotnet build "$project" \
        -c Release \
        -r linux-x64 \
        --no-restore \
        --verbosity minimal \
        --nologo \
        -m:"$BUILD_WORKERS" \
        -p:BuildInParallel=true \
        -p:UseSharedCompilation=true \
        -p:SelfContained=true \
        || fail "Linux build failed: $project_name"

done

progress 82 "All Linux projects compiled"


# ----------------------------------------------------------
# PUBLISH
# ----------------------------------------------------------

printf '\nPublishing self-contained Linux client...\n'

rm -rf -- "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

dotnet publish "$ROOT_DIR/Skua.App.Avalonia/Skua.App.Avalonia.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    --no-build \
    -o "$OUTPUT_DIR" \
    --nologo \
    || fail "Linux publish failed."

# ----------------------------------------------------------
# COPY PLUGINS
# ----------------------------------------------------------

printf '\nCopying plugins into Linux output...\n'

mkdir -p "$OUTPUT_DIR/plugins"

for plugin in \
    Skua.Plugin.DailyTracker.dll \
    ButlerV5.dll \
    Skua.Plugin.Cosmetics.dll
do
    [[ -s "$BUILD_DIR/plugins/$plugin" ]] \
        || fail "Expected plugin output is missing: Build/plugins/$plugin"

    cp -f -- \
        "$BUILD_DIR/plugins/$plugin" \
        "$OUTPUT_DIR/plugins/$plugin"
done

# ----------------------------------------------------------
# VERIFY OUTPUT
# ----------------------------------------------------------

[[ -s "$OUTPUT_DIR/Bonxie" ]] \
    || fail "Publish completed without producing Build/linux-x64/Bonxie."

[[ -s "$OUTPUT_DIR/player/Mugetsu" ]] \
    || fail "The Linux Mugetsu game player is missing from the published output."

chmod +x \
    "$OUTPUT_DIR/Bonxie" \
    "$OUTPUT_DIR/player/Mugetsu"

if [[ -f "$OUTPUT_DIR/player-x86-64-v2/Mugetsu" ]]; then
    chmod +x "$OUTPUT_DIR/player-x86-64-v2/Mugetsu"
fi

progress 95 "Linux packaging complete"

# ----------------------------------------------------------
# LAUNCH
# ----------------------------------------------------------

printf '\nLaunching Bonxie Linux client...\n'

LAUNCH_LOG="$BUILD_DIR/Bonxie-linux-launch.log"

nohup "$OUTPUT_DIR/Bonxie" > "$LAUNCH_LOG" 2>&1 &

LAUNCH_PID=$!

sleep 1

if ! kill -0 "$LAUNCH_PID" 2>/dev/null; then

    printf 'Bonxie did not remain running. Launch output:\n' >&2

    cat "$LAUNCH_LOG" >&2 || true

    fail "Linux client failed to launch. See Build/Bonxie-linux-launch.log."
fi

printf 'Bonxie started (PID %s).\n' "$LAUNCH_PID"
