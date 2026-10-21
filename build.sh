#!/usr/bin/env bash
#
# Build and package this Vintage Story mod into a distributable zip.
#
# Produces releases/<modid>_<version>.zip containing modinfo.json, the assets tree,
# and the compiled assembly. Works in Git Bash on Windows as well as Linux/macOS.
#
# Run with --help for usage.

set -euo pipefail

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------

if [ -t 1 ]; then
    C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
    C_RED=$'\033[31m';  C_RESET=$'\033[0m'
else
    C_CYAN=''; C_GREEN=''; C_YELLOW=''; C_RED=''; C_RESET=''
fi

step() { printf '\n%s==> %s%s\n' "$C_CYAN"   "$1" "$C_RESET"; }
info() { printf '    %s\n' "$1"; }
ok()   { printf '    %s%s%s\n' "$C_GREEN"  "$1" "$C_RESET"; }
warn() { printf '    %s%s%s\n' "$C_YELLOW" "$1" "$C_RESET"; }
die()  { printf '\n%sERROR: %s%s\n\n' "$C_RED" "$1" "$C_RESET" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Usage
# ---------------------------------------------------------------------------

usage() {
    cat <<'EOF'
Build and package this Vintage Story mod.

USAGE
    ./build.sh [options]

OPTIONS
    -v, --version <ver>     Version to stamp into modinfo.json and use in the zip
                            filename (e.g. 1.1.0). Defaults to whatever modinfo.json
                            already says.
    -c, --config <cfg>      Build configuration: Release (default) or Debug.
        --vs-dir <path>     Vintage Story install folder containing VintagestoryAPI.dll.
                            Falls back to $VINTAGE_STORY, then common install locations.
    -i, --install           Copy the finished zip into the game's Mods folder for testing.
        --clean             Remove bin/ and obj/ before building.
        --no-build          Skip compilation; just repackage the existing build output.
                            Handy after an assets-only change.
        --symbols           Include .pdb debug symbols in the zip.
        --skip-tests        Don't run the Atlas test suite in tests/ before packaging.
    -h, --help              Show this help.

EXAMPLES
    ./build.sh
        Build and package using the version already in modinfo.json.

    ./build.sh -v 1.1.0 --clean --install
        Bump to 1.1.0, rebuild from scratch, package, and install for testing.

NOTES
    Git Bash does not ship a 'zip' binary. The script falls back to Windows' bundled
    bsdtar (C:\Windows\System32\tar.exe), which writes valid zip archives. If neither
    is present it will tell you what to install.
EOF
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------

VERSION=""
CONFIG="Release"
VS_DIR=""
DO_INSTALL=0
DO_CLEAN=0
NO_BUILD=0
WITH_SYMBOLS=0
SKIP_TESTS=0

while [ $# -gt 0 ]; do
    case "$1" in
        -v|--version)  VERSION="${2:-}";  shift 2 ;;
        -c|--config)   CONFIG="${2:-}";   shift 2 ;;
        --vs-dir)      VS_DIR="${2:-}";   shift 2 ;;
        -i|--install)  DO_INSTALL=1;      shift ;;
        --clean)       DO_CLEAN=1;        shift ;;
        --no-build)    NO_BUILD=1;        shift ;;
        --symbols)     WITH_SYMBOLS=1;    shift ;;
        --skip-tests)  SKIP_TESTS=1;      shift ;;
        -h|--help)     usage; exit 0 ;;
        *)             die "Unknown option: $1  (try --help)" ;;
    esac
done

case "$CONFIG" in
    Release|Debug) ;;
    *) die "Config must be Release or Debug, got '$CONFIG'" ;;
esac

# ---------------------------------------------------------------------------
# Platform helpers
# ---------------------------------------------------------------------------

IS_WINDOWS=0
case "${OSTYPE:-}" in msys*|cygwin*|win32) IS_WINDOWS=1 ;; esac

# Convert a possibly-Windows path (C:\foo\bar) into a unix path bash can stat.
to_unix() {
    if [ "$IS_WINDOWS" -eq 1 ] && command -v cygpath >/dev/null 2>&1; then
        cygpath -u "$1"
    else
        printf '%s' "$1"
    fi
}

# Convert a unix path into the native form external Windows programs expect.
# MSBuild cannot interpret /c/Users/... so VINTAGE_STORY must be handed over as C:\Users\...
to_native() {
    if [ "$IS_WINDOWS" -eq 1 ] && command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

# ---------------------------------------------------------------------------
# Paths and manifest
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODINFO="$ROOT/modinfo.json"
ASSETS="$ROOT/assets"
RELEASES="$ROOT/releases"

[ -f "$MODINFO" ] || die "No modinfo.json found in $ROOT"

# Pull a top-level string field out of modinfo.json.
#
# Deliberately regex-based rather than a real JSON parser: Vintage Story accepts JSON5
# (unquoted keys, trailing commas, // comments), which strict parsers reject. jq also
# isn't present in a default Git Bash install.
modinfo_field() {
    sed -n -E "s/^[[:space:]]*\"?$1\"?[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" "$MODINFO" \
        | head -1
}

step "Reading manifest"

MODID="$(modinfo_field modid)"
MODTYPE="$(modinfo_field type)"
CUR_VERSION="$(modinfo_field version)"

[ -n "$MODID" ] || die "Could not read 'modid' from modinfo.json"
[ -n "$MODTYPE" ] || MODTYPE="code"

if [ -n "$VERSION" ]; then
    if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+(\.[0-9]+)?([-.+][0-9A-Za-z.-]+)?$'; then
        die "Version '$VERSION' doesn't look valid (expected e.g. 1.0.0 or 1.0.0-rc1)"
    fi
    if [ "$VERSION" != "$CUR_VERSION" ]; then
        # Rewrite in place via a temp file so the existing formatting and comments survive,
        # and so we don't depend on GNU-vs-BSD differences in `sed -i`.
        tmp_manifest="$(mktemp)"
        sed -E "0,/\"?version\"?[[:space:]]*:/s/(\"?version\"?[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1$VERSION\2/" \
            "$MODINFO" > "$tmp_manifest"
        grep -q "\"$VERSION\"" "$tmp_manifest" || { rm -f "$tmp_manifest"; die "Failed to update version in modinfo.json"; }
        mv "$tmp_manifest" "$MODINFO"
        ok "version: ${CUR_VERSION:-none} -> $VERSION"
    else
        info "version: $VERSION (unchanged)"
    fi
else
    [ -n "$CUR_VERSION" ] || die "modinfo.json has no 'version'; pass --version to set one"
    VERSION="$CUR_VERSION"
    info "version: $VERSION (from modinfo.json)"
fi

info "modid:   $MODID"
info "type:    $MODTYPE"

# ---------------------------------------------------------------------------
# Pick an archiver
#
# Git Bash ships no `zip`. Windows 10+ bundles bsdtar (libarchive), which can write
# zip and always uses forward-slash entry paths. GNU tar cannot write zip, so we must
# confirm the flavour rather than trusting the name.
# ---------------------------------------------------------------------------

ARCHIVER=""
BSDTAR=""

if command -v zip >/dev/null 2>&1; then
    ARCHIVER="zip"
else
    for candidate in /c/Windows/System32/tar.exe "$(command -v tar 2>/dev/null || true)"; do
        [ -n "$candidate" ] && [ -x "$candidate" ] || continue
        if "$candidate" --version 2>&1 | head -1 | grep -qi bsdtar; then
            ARCHIVER="bsdtar"; BSDTAR="$candidate"; break
        fi
    done
fi

if [ -z "$ARCHIVER" ] && command -v python3 >/dev/null 2>&1; then
    ARCHIVER="python3"
fi

[ -n "$ARCHIVER" ] || die "No usable archiver found. Install 'zip', or use a system with bsdtar or python3."

# ---------------------------------------------------------------------------
# Locate the game install (code mods only)
# ---------------------------------------------------------------------------

PROJECT="$(find "$ROOT" -maxdepth 1 -name '*.csproj' -type f | head -1 || true)"
IS_CODE_MOD=0
if [ "$MODTYPE" = "code" ] && [ -n "$PROJECT" ]; then IS_CODE_MOD=1; fi

if [ "$IS_CODE_MOD" -eq 1 ] && [ "$NO_BUILD" -eq 0 ]; then
    step "Locating Vintage Story install"

    candidates=()
    [ -n "$VS_DIR" ]                  && candidates+=("$(to_unix "$VS_DIR")")
    [ -n "${VINTAGE_STORY:-}" ]       && candidates+=("$(to_unix "$VINTAGE_STORY")")
    [ -n "${APPDATA:-}" ]             && candidates+=("$(to_unix "$APPDATA")/Vintagestory")
    candidates+=("/c/Program Files/Vintagestory")
    candidates+=("$HOME/.local/share/Steam/steamapps/common/VintageStory")
    candidates+=("$ROOT/../vs-game")

    FOUND=""
    for c in "${candidates[@]}"; do
        if [ -f "$c/VintagestoryAPI.dll" ]; then FOUND="$c"; break; fi
    done

    if [ -z "$FOUND" ]; then
        printf '\n%sERROR: Could not find VintagestoryAPI.dll in any of:%s\n' "$C_RED" "$C_RESET" >&2
        for c in "${candidates[@]}"; do printf '  %s\n' "$c" >&2; done
        printf '\nPass --vs-dir <path>, or export VINTAGE_STORY.\n\n' >&2
        exit 1
    fi

    VS_DIR="$FOUND"
    ok "$VS_DIR"
fi

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------

if [ "$DO_CLEAN" -eq 1 ]; then
    step "Cleaning"
    for d in bin obj; do
        if [ -d "$ROOT/$d" ]; then rm -rf "${ROOT:?}/$d"; info "removed $d/"; fi
    done
fi

# ---------------------------------------------------------------------------
# Localize shape textures
#
# VS Model Creator rewrites every texture it found in the game install as an absolute
# disk path on each save, which the game can't load. Point them back at our copies
# (copying any we don't have yet) before anything is built, tested or packaged.
# ---------------------------------------------------------------------------

LOCALIZE="$ROOT/tools/localize-textures.py"
if [ -f "$LOCALIZE" ]; then
    step "Localizing shape textures"
    PYTHON=""
    for candidate in python3 python; do
        # The Windows Store stub answers to the name but can't run anything; check it works.
        if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c '' >/dev/null 2>&1; then
            PYTHON="$candidate"; break
        fi
    done
    if [ -n "$PYTHON" ]; then
        "$PYTHON" "$(to_native "$LOCALIZE")" || die "Some shape textures could not be localized (see above)"
        ok "shape textures OK"
    else
        warn "python not found -- skipping (absolute texture paths will fail the asset tests)"
    fi
fi

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

OUTDIR="$ROOT/bin/$CONFIG"

if [ "$IS_CODE_MOD" -eq 1 ] && [ "$NO_BUILD" -eq 0 ]; then
    step "Building $(basename "$PROJECT") [$CONFIG]"
    command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH"

    # MSBuild is a native Windows process and cannot read /c/... style paths.
    export VINTAGE_STORY="$(to_native "$VS_DIR")"

    dotnet build "$PROJECT" -c "$CONFIG" --nologo || die "Build failed"
    ok "build succeeded"
elif [ "$IS_CODE_MOD" -eq 1 ]; then
    step "Skipping build (--no-build)"
fi

# ---------------------------------------------------------------------------
# Test
#
# Every test project under tests/ runs against a headless server with this mod
# staged (Atlas). A failure stops here, so a broken build is never packaged.
# ---------------------------------------------------------------------------

if [ "$SKIP_TESTS" -eq 0 ] && [ -d "$ROOT/tests" ]; then
    command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH"
    # Without a located install (--no-build), the test project falls back to $VINTAGE_STORY or %APPDATA%.
    [ -n "$VS_DIR" ] && export VINTAGE_STORY="$(to_native "$VS_DIR")"

    while IFS= read -r test_project; do
        step "Testing $(basename "$test_project") [$CONFIG]"
        dotnet test "$test_project" -c "$CONFIG" --nologo             || die "Tests failed -- not packaging. Fix them, or re-run with --skip-tests to package anyway."
        ok "tests passed"
    done < <(find "$ROOT/tests" -mindepth 2 -maxdepth 2 -name '*.csproj' -type f | LC_ALL=C sort)
elif [ "$SKIP_TESTS" -eq 1 ]; then
    step "Skipping tests (--skip-tests)"
fi

# ---------------------------------------------------------------------------
# Stage the package contents
#
# Everything is copied into a temp directory first so that all archivers see an
# identical layout and the entry names fall out of the directory structure.
# ---------------------------------------------------------------------------

step "Collecting package contents"

STAGE="$(mktemp -d)"
LIST="$(mktemp)"
cleanup() { rm -rf "$STAGE" "$LIST"; }
trap cleanup EXIT

cp "$MODINFO" "$STAGE/modinfo.json"

if [ "$IS_CODE_MOD" -eq 1 ]; then
    [ -d "$OUTDIR" ] || die "Build output not found at $OUTDIR (run without --no-build first)"
    dll_count=0
    for f in "$OUTDIR"/*.dll; do
        [ -e "$f" ] || continue
        cp "$f" "$STAGE/"; dll_count=$((dll_count + 1))
    done
    [ "$dll_count" -gt 0 ] || die "No assemblies found in $OUTDIR"
    if [ "$WITH_SYMBOLS" -eq 1 ]; then
        for f in "$OUTDIR"/*.pdb; do [ -e "$f" ] && cp "$f" "$STAGE/"; done
    fi
fi

if [ -d "$ASSETS" ]; then
    cp -r "$ASSETS" "$STAGE/assets"
else
    warn "no assets/ folder found -- packaging code only"
fi

( cd "$STAGE" && find . -type f | sed 's|^\./||' | LC_ALL=C sort ) > "$LIST"
while IFS= read -r line; do info "$line"; done < "$LIST"

# ---------------------------------------------------------------------------
# Write the archive
# ---------------------------------------------------------------------------

step "Writing archive"

mkdir -p "$RELEASES"
ZIP="$RELEASES/${MODID}_${VERSION}.zip"
[ -f "$ZIP" ] && { rm -f "$ZIP"; info "replaced existing archive"; }

case "$ARCHIVER" in
    zip)
        ( cd "$STAGE" && zip -q -X "$ZIP" -@ < "$LIST" )
        ;;
    bsdtar)
        ( cd "$STAGE" && "$BSDTAR" --format=zip -c -f "$ZIP" -T "$LIST" )
        ;;
    python3)
        ( cd "$STAGE" && python3 -c '
import sys, zipfile
out = sys.argv[1]
names = [l.rstrip("\n") for l in open(sys.argv[2], encoding="utf-8") if l.strip()]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for n in names:
        z.write(n, n)
' "$(to_native "$ZIP")" "$(to_native "$LIST")" )
        ;;
esac

[ -f "$ZIP" ] || die "Archiver ($ARCHIVER) did not produce $ZIP"

size_kb=$(( ($(wc -c < "$ZIP") + 512) / 1024 ))
file_count=$(wc -l < "$LIST" | tr -d ' ')
ok "$ZIP  (${size_kb} KB, ${file_count} files, via $ARCHIVER)"

# ---------------------------------------------------------------------------
# Verify
# ---------------------------------------------------------------------------

step "Verifying archive"

if command -v unzip >/dev/null 2>&1; then
    names="$(unzip -Z1 "$ZIP")"

    # Backslash entry names look fine on Windows but break on Linux servers, where they
    # become literal filenames rather than a folder tree.
    if printf '%s\n' "$names" | grep -q '\\'; then
        die "Archive contains backslash paths, which break on Linux servers"
    fi

    printf '%s\n' "$names" | grep -qx 'modinfo.json' \
        || die "modinfo.json is missing from the archive root"

    if [ "$IS_CODE_MOD" -eq 1 ]; then
        printf '%s\n' "$names" | grep -Eqx '[^/]+\.dll' \
            || die "No assembly found at the archive root"
    fi

    ok "layout OK (root-level modinfo.json, forward-slash paths)"
else
    warn "unzip not available -- skipping verification"
fi

# ---------------------------------------------------------------------------
# Optional install
# ---------------------------------------------------------------------------

if [ "$DO_INSTALL" -eq 1 ]; then
    step "Installing to game Mods folder"

    if [ -n "${VINTAGE_STORY_DATA:-}" ]; then
        MODS_DIR="$(to_unix "$VINTAGE_STORY_DATA")/Mods"
    elif [ "$IS_WINDOWS" -eq 1 ] && [ -n "${APPDATA:-}" ]; then
        MODS_DIR="$(to_unix "$APPDATA")/VintagestoryData/Mods"
    else
        MODS_DIR="$HOME/.config/VintagestoryData/Mods"
    fi

    [ -d "$MODS_DIR" ] || die "Mods folder not found at $MODS_DIR"

    # Drop older builds of this mod so the game doesn't try to load two copies.
    for old in "$MODS_DIR/${MODID}_"*.zip; do
        [ -e "$old" ] || continue
        [ "$(basename "$old")" = "${MODID}_${VERSION}.zip" ] && continue
        rm -f "$old"; info "removed stale $(basename "$old")"
    done

    cp "$ZIP" "$MODS_DIR/"
    ok "$MODS_DIR/${MODID}_${VERSION}.zip"
fi

printf '\n%sDone. %s %s packaged.%s\n\n' "$C_GREEN" "$MODID" "$VERSION" "$C_RESET"
