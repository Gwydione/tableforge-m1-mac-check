#!/bin/bash
# TableForge Mac Check — packaging and Gatekeeper trial steps for the macOS runner (bash 3.2; macOS tools only).
# Every external command is bounded by a timeout; a timeout is recorded as TIMEOUT, never as a result. Nothing here
# disables Gatekeeper, removes a quarantine attribute or changes a security setting: quarantine is only ever ADDED, to a
# copy of a zip, to simulate a browser download.
#
#   seal       SRC_APP LABEL RID   copy the app, seal it ad hoc with Apple's codesign, verify it, write two zips:
#                                  <label>-plain.zip (ditto --norsrc: no extended attributes, like most zip tools)
#                                  <label>-meta.zip  (ditto --sequesterRsrc: extended attributes kept in __MACOSX)
#   roundtrip  ZIP NAME            extract ZIP with ditto, Archive Utility (the Finder's double-click), Archive Utility
#                                  on a quarantined copy, and Info-ZIP unzip; verify each extracted app's signature
#   gatekeeper APP NAME            bounded assessment (codesign, spctl, syspolicy_check), classified, then one
#                                  observed first open through LaunchServices (no clicks, nothing bypassed)
set -u
OUT="${OUT:-$PWD/out}"
SUMMARY="$OUT/packaging/summary.tsv"

# bounded SECONDS FILE CMD...: runs CMD with its output in FILE (after a "$ CMD" line); appends "exit N", or
# "TIMEOUT after N s" when it had to be stopped. Returns CMD's status, or 124 on timeout.
bounded() {
  local secs=$1 file=$2; shift 2
  echo "\$ $*" > "$file"
  "$@" >> "$file" 2>&1 &
  local pid=$! ticks=0
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$ticks" -ge $((secs * 10)) ]; then
      kill -TERM "$pid" 2>/dev/null; sleep 1; kill -KILL "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
      echo "TIMEOUT after ${secs} s (stopped; no result)" >> "$file"; return 124
    fi
    sleep 0.1; ticks=$((ticks + 1))
  done
  wait "$pid"; local rc=$?
  echo "exit $rc" >> "$file"; return $rc
}

# Files whose code signature lives in extended attributes (codesign does this for non-Mach-O files in code folders).
xattr_signatures() { xattr -r "$1" 2>/dev/null | grep -c ': com.apple.cs.CodeSignature$'; }

first_problem() { grep -v -E '^\$ |^exit [0-9]+$|valid on disk|satisfies its Designated Requirement|^--prepared|^--validated' "$1" | head -2 | tr '\n' ' ' | cut -c1-220; }

seal() {
  local src=$1 label=$2 rid=$3
  local d="$OUT/packaging/$label" w="$PWD/seal-$label"
  mkdir -p "$d" "$w/TableForge Mac Check" "$OUT/sealed-package"
  local app="$w/TableForge Mac Check/TableForge Mac Check.app"
  ditto "$src" "$app"
  {
    echo "files in Contents/MacOS: $(find "$app/Contents/MacOS" -type f | wc -l | tr -d ' ')"
    echo "Mach-O files in Contents/MacOS: $(find "$app/Contents/MacOS" -type f -exec file {} + | grep -c 'Mach-O')"
  } > "$d/layout.txt"
  bounded 180 "$d/codesign-sign.txt" codesign --force --deep --sign - "$app"
  bounded 180 "$d/verify-before-archive.txt" codesign --verify --deep --strict --verbose=2 "$app"; local v=$?
  bounded 60 "$d/codesign-display.txt" codesign -dv --verbose=2 "$app"
  local xs; xs=$(xattr_signatures "$app")
  echo "files whose signature is stored in extended attributes: $xs" >> "$d/layout.txt"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$label" "(sealed, before archiving)" "-" "-" "$v" "$xs" \
    "$(ls -l "$app/Contents/MacOS/TableForgeMacCheck" | cut -c1-10)" "none" "$(first_problem "$d/verify-before-archive.txt")" >> "$SUMMARY"
  local base="$OUT/sealed-package/TableForge-Mac-Check-$rid-$label"
  bounded 300 "$d/zip-plain.txt" ditto -c -k --norsrc --keepParent "$w/TableForge Mac Check" "$base-plain.zip"
  bounded 300 "$d/zip-meta.txt" ditto -c -k --sequesterRsrc --keepParent "$w/TableForge Mac Check" "$base-meta.zip"
  for z in "$base-plain.zip" "$base-meta.zip"; do
    { echo "$(basename "$z"): $(stat -f %z "$z") bytes, sha256 $(shasum -a 256 "$z" | cut -d' ' -f1)"
      echo "  entries: $(zipinfo -1 "$z" | wc -l | tr -d ' '); __MACOSX metadata entries: $(zipinfo -1 "$z" | grep -c '^__MACOSX/')"
      zipinfo "$z" | grep -E 'Contents/MacOS/TableForgeMacCheck$' | sed 's/^/  /'; } >> "$d/zips.txt"
  done
}

# Waits (bounded) for Archive Utility to finish in DIR: an app whose seal and executable exist and whose file count is
# stable. Prints the app's path. (Archive Utility puts a zip with several top-level items into a folder of its own.)
wait_extracted() {
  local dir=$1 secs=$2 i=0 last=-1 n app
  while [ "$i" -lt "$secs" ]; do
    app=$(find "$dir" -maxdepth 3 -type d -name "TableForge Mac Check.app" 2>/dev/null | head -1)
    if [ -n "$app" ] && [ -f "$app/Contents/_CodeSignature/CodeResources" ] && [ -f "$app/Contents/MacOS/TableForgeMacCheck" ]; then
      n=$(find "$app" -type f | wc -l | tr -d ' ')
      if [ "$n" = "$last" ]; then echo "$app"; return 0; fi
      last=$n
    fi
    sleep 1; i=$((i + 1))
  done
  return 124
}

roundtrip() {
  local zip=$1 name=$2
  local d="$OUT/packaging/$name"; mkdir -p "$d"
  for how in ditto archive-utility archive-utility-quarantined unzip; do
    local x="$PWD/x/$name/$how"; mkdir -p "$x"; cp "$zip" "$x/download.zip"
    local app="$x/TableForge Mac Check/TableForge Mac Check.app" ok=yes
    case $how in
      ditto) bounded 180 "$d/$how-extract.txt" ditto -x -k "$x/download.zip" "$x" || ok=no ;;
      unzip) bounded 180 "$d/$how-extract.txt" unzip -q "$x/download.zip" -d "$x" || ok=no ;;
      archive-utility*)
        if [ -f "$OUT/packaging/archive-utility-unavailable.txt" ]; then
          echo "skipped: Archive Utility did not extract on this runner earlier" > "$d/$how-extract.txt"; ok=no
        else
          if [ $how = archive-utility-quarantined ]; then
            xattr -w com.apple.quarantine "0081;$(printf %x "$(date +%s)");Safari;" "$x/download.zip"
          fi
          bounded 20 "$d/$how-extract.txt" open -g -a "Archive Utility" "$x/download.zip"
          if app=$(wait_extracted "$x" 120); then echo "extracted to ${app#$x/}" >> "$d/$how-extract.txt"
          else echo "not extracted within 120 s" | tee -a "$d/$how-extract.txt" > "$OUT/packaging/archive-utility-unavailable.txt"; ok=no; fi
        fi ;;
    esac
    if [ $ok = yes ] && [ -d "$app" ]; then
      bounded 180 "$d/$how-verify.txt" codesign --verify --deep --strict --verbose=2 "$app"; local v=$?
      local xs mode q
      xs=$(xattr_signatures "$app")
      mode=$(ls -l "$app/Contents/MacOS/TableForgeMacCheck" | cut -c1-10)
      q=$(xattr -p com.apple.quarantine "$app" 2>/dev/null | cut -d';' -f1,3 || true); [ -n "$q" ] || q=none
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$how" "$(basename "$zip")" "yes" "$v" "$xs" "$mode" "$q" "$(first_problem "$d/$how-verify.txt")" >> "$SUMMARY"
      echo "$app" > "$PWD/x/$name/$how.path"   # for the launch and Gatekeeper steps
    else
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$how" "$(basename "$zip")" "no" "-" "-" "-" "-" "extraction failed, skipped or timed out" >> "$SUMMARY"
    fi
  done
}

gatekeeper() {
  local app=$1 name=$2
  local g="$OUT/gatekeeper/$name"; mkdir -p "$g"
  bounded 30 "$g/spctl-status.txt" spctl --status
  echo "quarantine: $(xattr -p com.apple.quarantine "$app" 2>/dev/null || echo none)" > "$g/quarantine.txt"
  bounded 180 "$g/codesign-verify.txt" codesign --verify --deep --strict --verbose=2 "$app"; local sig=$?
  bounded 90 "$g/spctl-assess.txt" spctl --assess --type execute -vvv "$app"; local gk=$?
  bounded 180 "$g/syspolicy-check.txt" syspolicy_check distribution "$app"
  local s a
  case $sig in 0) s="VALID" ;; 124) s="INCONCLUSIVE (codesign timed out)" ;; *) s="INVALID: $(first_problem "$g/codesign-verify.txt")" ;; esac
  if [ $gk -eq 0 ]; then a="ACCEPTED"
  elif [ $gk -eq 124 ]; then a="INCONCLUSIVE (spctl timed out)"
  elif grep -q -E 'no usable signature|not signed at all|code has no resources|sealed resource is missing|invalid signature' "$g/spctl-assess.txt"; then a="REJECTED because of the signature"
  elif [ $sig -eq 0 ]; then a="REJECTED with a valid ad-hoc signature: not notarized / no Developer ID (expected without the Apple Developer Program)"
  else a="REJECTED (reason not stated)"; fi
  { echo "code signature: $s"
    echo "Gatekeeper assessment (spctl --assess --type execute, exit $gk): $a"
    echo "syspolicy_check fatal findings: $(awk '/^[A-Z]/ {t=$0} /Severity: Fatal/ {printf "%s; ", t}' "$g/syspolicy-check.txt")"
  } > "$g/classification.txt"

  # One first open through LaunchServices, as a Finder double-click would ask for it. On a runner nobody can see or answer
  # a dialog, so this only records whether the app started and what syspolicyd/CoreServicesUIAgent logged.
  local start; start="$(date '+%Y-%m-%d %H:%M:%S')"
  echo "\$ open \"$app\"   (started $start; observed for 30 s)" > "$g/first-open.txt"
  open "$app" >> "$g/first-open.txt" 2>&1 &
  local opid=$! i=0
  while kill -0 "$opid" 2>/dev/null && [ "$i" -lt 300 ]; do sleep 0.1; i=$((i + 1)); done
  if kill -0 "$opid" 2>/dev/null; then echo "open: had not returned after 30 s (waiting on the system)" >> "$g/first-open.txt"
  else wait "$opid"; echo "open: returned, exit $?" >> "$g/first-open.txt"; fi
  sleep 3
  if pgrep -f "$app/Contents/MacOS/TableForgeMacCheck" > /dev/null; then echo "app process: RUNNING (Gatekeeper let it start)" >> "$g/first-open.txt"
  else echo "app process: not running (did not start)" >> "$g/first-open.txt"; fi
  echo "CoreServicesUIAgent (shows Gatekeeper dialogs) running: $(pgrep -x CoreServicesUIAgent > /dev/null && echo yes || echo no)" >> "$g/first-open.txt"
  bounded 15 "$g/screen-capture.txt" screencapture -x "$g/first-open-screen.png"
  bounded 90 "$g/log-raw.txt" log show --start "$start" --style compact \
    --predicate 'process == "syspolicyd" OR process == "CoreServicesUIAgent" OR process == "XprotectService"'
  grep -i -E 'gatekeeper|notar|damaged|malware|assess|prompt|reject|adhoc|ad-hoc|block|allow|unsigned|invalid|verify|TableForge|TIMEOUT|^exit' "$g/log-raw.txt" | head -150 > "$g/syspolicy-log.txt"
  rm -f "$g/log-raw.txt"
  kill "$opid" 2>/dev/null
  pkill -f "$app/Contents/MacOS/TableForgeMacCheck" 2>/dev/null
  true
}

cmd=$1; shift
mkdir -p "$OUT/packaging" "$OUT/gatekeeper"
[ -f "$SUMMARY" ] || printf 'package\textraction\tzip\textracted\tcodesign verify exit\txattr signatures\tmain executable\tquarantine\tfirst problem\n' > "$SUMMARY"
case $cmd in
  seal) seal "$@" ;;
  roundtrip) roundtrip "$@" ;;
  gatekeeper) gatekeeper "$@" ;;
  *) echo "unknown command $cmd"; exit 2 ;;
esac
true
