#!/usr/bin/env bash
set -euo pipefail

server_name="decompiler"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="${INSTALL_DIR:-$HOME/.codex/mcp-servers/decompiler}"
target="$install_dir/DecompilerServer"
codex_home="${CODEX_HOME:-$HOME/.codex}"
codex_skill_source="$repo_root/skills/decompiler-mcp"
codex_skill_destination="$codex_home/skills/decompiler-mcp"
codex_agents_file="$codex_home/AGENTS.md"
codex_agents_start_marker="<!-- decompiler-mcp-managed:start -->"
codex_agents_end_marker="<!-- decompiler-mcp-managed:end -->"
publish_dir="$(mktemp -d "${TMPDIR:-/tmp}/decompiler-server-publish.XXXXXX")"
spctl_log="$(mktemp "${TMPDIR:-/tmp}/decompiler-server-spctl.XXXXXX")"

cleanup() {
  rm -rf "$publish_dir"
  rm -f "$spctl_log"
}
trap cleanup EXIT

find_codex() {
  if [ -n "${CODEX_BIN:-}" ]; then
    printf '%s\n' "$CODEX_BIN"
    return
  fi

  if [ -x "/Applications/Codex.app/Contents/Resources/codex" ]; then
    printf '%s\n' "/Applications/Codex.app/Contents/Resources/codex"
    return
  fi

  if command -v codex >/dev/null 2>&1; then
    command -v codex
  fi
}

find_codesign_identity() {
  if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    printf '%s\n' "$CODESIGN_IDENTITY"
    return
  fi

  security find-identity -v -p codesigning 2>/dev/null | awk -F\" '
    /"Developer ID Application:/ {
      print $2
      found = 1
      exit
    }
    /"Apple Development:/ {
      if (apple == "") {
        apple = $2
      }
    }
    END {
      if (!found && apple != "") {
        print apple
      }
    }
  '
}

codex_agents_pointer() {
  cat <<'EOF'
- For any C# binary or .NET assembly inspection, use the `decompiler-mcp` skill and DecompilerServer MCP first.
- Do not use `monodis`, `ilspycmd`, `ildasm`, dnSpy, or similar shell decompiler tools for normal symbol/member/source/IL inspection unless explicitly asked.
- Shell search is fine for repository source files; binary inspection belongs in DecompilerServer.
EOF
}

install_codex_support() {
  if [ -d "$codex_skill_source" ]; then
    mkdir -p "$(dirname "$codex_skill_destination")"
    rm -rf "$codex_skill_destination"
    cp -R "$codex_skill_source" "$codex_skill_destination"
    echo "Installed Codex skill at $codex_skill_destination"
  else
    echo "warning: Codex skill source not found at $codex_skill_source" >&2
  fi

  upsert_codex_agents_pointer
}

upsert_codex_agents_pointer() {
  local pointer_body managed_block tmp_file

  pointer_body="$(codex_agents_pointer)"
  managed_block="$(cat <<EOF
$codex_agents_start_marker
$pointer_body
$codex_agents_end_marker
EOF
)"

  mkdir -p "$(dirname "$codex_agents_file")"
  tmp_file="$(mktemp "${TMPDIR:-/tmp}/decompiler-server-agents.XXXXXX")"

  if [ -f "$codex_agents_file" ] \
    && grep -Fq "$codex_agents_start_marker" "$codex_agents_file" \
    && grep -Fq "$codex_agents_end_marker" "$codex_agents_file"; then
    START_MARKER="$codex_agents_start_marker" \
      END_MARKER="$codex_agents_end_marker" \
      MANAGED_BLOCK="$managed_block" \
      perl -0pe '
        BEGIN {
          $start = quotemeta($ENV{"START_MARKER"});
          $end = quotemeta($ENV{"END_MARKER"});
          $block = $ENV{"MANAGED_BLOCK"};
        }
        s/$start.*?$end/$block/s;
      ' "$codex_agents_file" > "$tmp_file"
  elif [ -f "$codex_agents_file" ] && [ -s "$codex_agents_file" ]; then
    cat "$codex_agents_file" > "$tmp_file"
    printf '\n%s\n' "$managed_block" >> "$tmp_file"
  else
    printf '%s\n' "$managed_block" > "$tmp_file"
  fi

  if [ -f "$codex_agents_file" ] && cmp -s "$tmp_file" "$codex_agents_file"; then
    rm -f "$tmp_file"
    echo "Codex global AGENTS.md already contains the DecompilerServer pointer"
    return
  fi

  mv "$tmp_file" "$codex_agents_file"
  echo "Updated Codex global AGENTS.md at $codex_agents_file"
}

echo "Publishing DecompilerServer from $repo_root"
dotnet publish "$repo_root/DecompilerServer.csproj" \
  -c Release \
  -r osx-arm64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "$publish_dir"

mkdir -p "$install_dir"
install -m 755 "$publish_dir/DecompilerServer" "$target"

# Downloaded release artifacts can carry Safari/Gatekeeper quarantine state.
# This install path is built locally, so quarantine should never be preserved.
xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true

identity="$(find_codesign_identity)"
if [ -n "$identity" ]; then
  echo "Signing $target with $identity"
  codesign --force --timestamp=none --sign "$identity" "$target"
else
  echo "No local code-signing identity found; applying ad-hoc signature"
  codesign --force --sign - "$target"
fi

codesign --verify --strict --verbose=2 "$target"

if xattr -p com.apple.quarantine "$target" >/dev/null 2>&1; then
  echo "error: $target is still quarantined" >&2
  exit 1
fi

codex_bin="$(find_codex || true)"
if [ -z "$codex_bin" ]; then
  echo "warning: Codex CLI not found; update ~/.codex/config.toml manually to use $target" >&2
else
  "$codex_bin" mcp remove "$server_name" >/dev/null 2>&1 || true
  "$codex_bin" mcp add "$server_name" -- "$target"
fi

install_codex_support

echo "Installed local Codex MCP server at $target"
echo "Local checks passed: signed, executable, and not quarantined."
if spctl -a -vv -t execute "$target" >"$spctl_log" 2>&1; then
  cat "$spctl_log"
else
  cat "$spctl_log"
  echo "Gatekeeper distribution assessment still requires Developer ID signing and notarization."
  echo "For local Codex stdio use, the installed binary is locally built, signed, executable, and not quarantined."
fi
