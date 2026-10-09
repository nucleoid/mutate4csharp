#!/usr/bin/env bash
set -euo pipefail

ORCHESTRATION_REFUSAL=73
TOOL_SDK_RECEIPT=

fail() {
  printf 'agent-gate: %s\n' "$*" >&2
  exit "$ORCHESTRATION_REFUSAL"
}

canonical_existing() {
  local candidate=$1 label=$2
  [[ "$candidate" = /* ]] || fail "$label must be an absolute path"
  realpath -e -- "$candidate" 2>/dev/null || fail "$label does not exist"
}

canonical_boundary() {
  local candidate=$1 label=$2 lexical physical
  [[ "$candidate" = /* ]] || fail "$label must be an absolute path"
  lexical=$(realpath -ms -- "$candidate") || fail "$label cannot be canonicalized"
  physical=$(realpath -e -- "$candidate" 2>/dev/null) || fail "$label does not exist"
  [[ "$lexical" = "$physical" && ! -L "$candidate" ]] || fail "$label must not be symbolic"
  [[ -d "$physical" ]] || fail "$label must be a directory"
  printf '%s\n' "$physical"
}

canonical_new_target() {
  local candidate=$1 label=$2 parent lexical_parent physical_parent
  [[ "$candidate" = /* ]] || fail "$label must be an absolute path"
  [[ ! -e "$candidate" && ! -L "$candidate" ]] || fail "$label must not already exist"
  parent=$(dirname "$candidate")
  lexical_parent=$(realpath -ms -- "$parent") || fail "$label parent cannot be canonicalized"
  physical_parent=$(realpath -e -- "$parent" 2>/dev/null) || fail "$label parent does not exist"
  [[ "$lexical_parent" = "$physical_parent" ]] || fail "$label parent must not be symbolic"
  realpath -m -- "$candidate" 2>/dev/null || fail "$label cannot be canonicalized"
}

require_external() {
  local candidate=$1 repository=$2 label=$3
  case "$candidate/" in
    "$repository/"*) fail "$label must remain outside the target repository" ;;
  esac
}

sha256_file() {
  sha256sum "$1" | cut -d' ' -f1
}

payload_sha256() {
  local root=$1
  [[ -d "$root" && ! -L "$root" ]] || fail "payload directory is missing or symbolic"
  if find "$root" -type l -print -quit | grep -q .; then
    fail "payload contains a symbolic link"
  fi
  (
    cd "$root"
    find . -type f -print0 |
      LC_ALL=C sort -z |
      while IFS= read -r -d '' path; do
        printf '%s\0' "$path"
        stat -c '%a' -- "$path"
        sha256sum "$path" | cut -d' ' -f1
      done
  ) | sha256sum | cut -d' ' -f1
}

trusted_environment() {
  local -a allowed_environment=("PATH=${PATH:-/usr/bin:/bin}" "HOME=${HOME:-/nonexistent}")
  local name
  for name in TMPDIR TMP TEMP LANG LC_ALL TZ SSL_CERT_FILE SSL_CERT_DIR XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME \
      DOTNET_CLI_HOME NUGET_PACKAGES NUGET_HTTP_CACHE_PATH NUGET_PLUGINS_CACHE_PATH NUGET_SCRATCH; do
    [[ -v "$name" ]] && allowed_environment+=("$name=${!name}")
  done
  # env -i deliberately drops every other caller value, including DOTNET_STARTUP_HOOKS,
  # DOTNET_ADDITIONAL_DEPS, DOTNET_ROOT_*, CORECLR_*/COR_* profiler injection,
  # MSBuildSDKsPath, MSBUILD_EXE_PATH, MSBuildExtensionsPath*,
  # MSBUILDADDITIONALSDKRESOLVERSFOLDER, DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR, and
  # DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR.
  env -i "${allowed_environment[@]}" \
    DOTNET_ROOT="$(dirname "$TRUSTED_DOTNET")" \
    DOTNET_HOST_PATH="$TRUSTED_DOTNET" \
    DOTNET_MULTILEVEL_LOOKUP=0 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_NOLOGO=true \
    "$@"
}

bind_trusted_dotnet() {
  local requested=${1:-}
  [[ -n "$requested" ]] || fail "dotnet host is unavailable"
  TRUSTED_DOTNET=$(realpath -e -- "$requested" 2>/dev/null) || fail "dotnet host cannot be canonicalized"
  [[ -f "$TRUSTED_DOTNET" && -x "$TRUSTED_DOTNET" && ! -L "$TRUSTED_DOTNET" ]] ||
    fail "dotnet host must resolve to a regular executable"
}

trusted_dotnet() {
  trusted_environment "$TRUSTED_DOTNET" "$@"
}

bind_neutral_sdk_directory() {
  local candidate=$1
  SDK_DIRECTORY=$(canonical_boundary "$candidate" "neutral SDK directory")
  [[ -f "$SDK_DIRECTORY/global.json" && ! -L "$SDK_DIRECTORY/global.json" ]] ||
    fail "neutral SDK directory must contain a regular global.json"
}

trusted_sdk_version() {
  (cd "$SDK_DIRECTORY" && trusted_dotnet --version)
}

reject_ancestor_build_controls() {
  local repository=$1 current name
  current=$(dirname "$repository")
  while [[ "$current" != / ]]; do
    for name in Directory.Build.props Directory.Build.targets Directory.Packages.props Directory.Build.rsp MSBuild.rsp .globalconfig global.json; do
      [[ ! -e "$current/$name" && ! -L "$current/$name" ]] ||
        fail "ancestor build-control input is not allowed: $current/$name"
    done
    current=$(dirname "$current")
  done
}

write_receipt() {
  local receipt=$1
  shift
  local fields=("$@") temporary index
  (( ${#fields[@]} % 2 == 0 )) || fail "receipt fields are incomplete"
  for ((index=0; index<${#fields[@]}; index+=2)); do
    [[ -n "${fields[index]}" && -n "${fields[index+1]}" &&
       "${fields[index]}" != *$'\n'* && "${fields[index]}" != *$'\t'* &&
       "${fields[index+1]}" != *$'\n'* && "${fields[index+1]}" != *$'\t'* ]] ||
      fail "receipt fields must be non-empty single-line tab-free values"
  done
  temporary=$(mktemp "$(dirname "$receipt")/.receipt.XXXXXXXXXX") || fail "could not stage receipt"
  if ! {
    for ((index=0; index<${#fields[@]}; index+=2)); do
      printf '%s\t%s\n' "${fields[index]}" "${fields[index+1]}"
    done > "$temporary"
    chmod 600 "$temporary"
    mv -n -- "$temporary" "$receipt"
    [[ ! -e "$temporary" ]]
  }; then
    rm -f -- "$temporary"
    [[ ! -e "$receipt" && ! -L "$receipt" ]] || fail "receipt must not already exist"
    fail "could not write receipt without clobbering"
  fi
}

read_receipt() {
  local receipt=$1 key value
  [[ -f "$receipt" && ! -L "$receipt" ]] || fail "receipt is missing or symbolic"
  RECEIPT_FORMAT=
  LOCAL_TOOL_VERSION=
  TOOL_PACKAGE=
  TOOL_PAYLOAD=
  ORCHESTRATION_SDK_VERSION=
  DOTNET_HOST=
  while IFS=$'\t' read -r key value; do
    [[ -n "$key" && -n "$value" && "$value" != *$'\t'* ]] || fail "receipt has an invalid record"
    case "$key" in
      format) [[ -z "$RECEIPT_FORMAT" ]] || fail "duplicate receipt key"; RECEIPT_FORMAT=$value ;;
      local_tool_version) [[ -z "$LOCAL_TOOL_VERSION" ]] || fail "duplicate receipt key"; LOCAL_TOOL_VERSION=$value ;;
      tool_package) [[ -z "$TOOL_PACKAGE" ]] || fail "duplicate receipt key"; TOOL_PACKAGE=$value ;;
      tool_payload) [[ -z "$TOOL_PAYLOAD" ]] || fail "duplicate receipt key"; TOOL_PAYLOAD=$value ;;
      runtime_version) [[ -z "$ORCHESTRATION_SDK_VERSION" ]] || fail "duplicate receipt key"; ORCHESTRATION_SDK_VERSION=$value ;;
      dotnet_host) [[ -z "$DOTNET_HOST" ]] || fail "duplicate receipt key"; DOTNET_HOST=$value ;;
      *) fail "receipt contains unknown key: $key" ;;
    esac
  done < "$receipt"
  [[ "$RECEIPT_FORMAT" = mutate4csharp-agent-gate-v3 ]] || fail "unsupported receipt format"
  [[ -n "$LOCAL_TOOL_VERSION" && -n "$TOOL_PACKAGE" && -n "$TOOL_PAYLOAD" && -n "$ORCHESTRATION_SDK_VERSION" && -n "$DOTNET_HOST" ]] ||
    fail "receipt is incomplete"
}

trusted_tool() {
  trusted_environment "$TOOL_COMMAND" "$@"
}

validate_report() {
  local report=$1 process_exit=$2 state_mode=$3 target_repository=$4
  [[ -f "$report" && ! -L "$report" ]] || fail "tool did not publish a fresh report"
  (cd "$SDK_DIRECTORY" && python3 -I - "$report" "$process_exit" "$state_mode" "$target_repository" "$TOOL_SDK_RECEIPT") <<'PY' || exit "$ORCHESTRATION_REFUSAL"
import glob, hashlib, json, os, stat, sys
path, process_exit, state_mode, repository, sdk_receipt = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
try:
    metadata = os.lstat(path)
    if not stat.S_ISREG(metadata.st_mode):
        raise ValueError("report is not a regular file")
    with open(path, "rb") as stream:
        report = json.load(stream)
    if not isinstance(report, dict):
        raise ValueError("report root must be an object")
    if report.get("exitCode") != process_exit:
        raise ValueError("report exitCode does not match process result")
    if process_exit != 4 or report.get("outcome") != "INCOMPLETE":
        raise ValueError("current gate requires exit 4 with outcome INCOMPLETE")
    conditions = report.get("incompleteConditions")
    if not isinstance(conditions, list) or not any(
        isinstance(item, dict) and item.get("code") == "EXECUTION_NOT_IMPLEMENTED"
        for item in conditions
    ):
        raise ValueError("report lacks EXECUTION_NOT_IMPLEMENTED incomplete condition")
    counts = report.get("counts")
    if not isinstance(counts, dict) or not isinstance(counts.get("enumerated"), int) or \
            isinstance(counts.get("enumerated"), bool) or counts["enumerated"] < 0:
        raise ValueError("report lacks a bounded nonnegative enumeration count")
    if any(isinstance(item, dict) and item.get("code") == "SIDECAR_WRITE_FAILED" for item in conditions):
        raise ValueError("report contains SIDECAR_WRITE_FAILED")
    evidence = report.get("evidence", [])
    if not isinstance(evidence, list):
        raise ValueError("report evidence is not an array")
    if any(isinstance(item, dict) and item.get("kind") == "SIDECAR_PUBLICATION_FAILURE" for item in evidence):
        raise ValueError("report contains SIDECAR_PUBLICATION_FAILURE")
    if state_mode == "default-state":
        with open(path, "rb") as stream:
            report_bytes = stream.read()
        expected_hash = hashlib.sha256(report_bytes).hexdigest()
        discovery_directory = os.path.join(repository, ".mutate4csharp", "discovery")
        matched = False
        for candidate in glob.glob(os.path.join(discovery_directory, "*.json")):
            candidate_metadata = os.lstat(candidate)
            if not stat.S_ISREG(candidate_metadata.st_mode):
                continue
            with open(candidate, "rb") as stream:
                discovery = json.load(stream)
            if (isinstance(discovery, dict) and discovery.get("recordKind") == "DISCOVERY" and
                    discovery.get("reportSha256") == expected_hash and
                    discovery.get("reportLength") == len(report_bytes)):
                matched = True
                break
        if not matched:
            raise ValueError("default-state report lacks a report-bound discovery record")
    if sdk_receipt:
        matches = [item for item in evidence
                   if isinstance(item, dict) and item.get("kind") == "TOOL_INTERNAL_SDK"]
        if len(matches) != 1:
            raise ValueError("accepted default-state report lacks exactly one tool-internal SDK identity")
        diagnostics = matches[0].get("diagnostics")
        if (not isinstance(diagnostics, list) or len(diagnostics) != 1 or
                not isinstance(diagnostics[0], str) or not diagnostics[0].startswith("dotnet-sdk=")):
            raise ValueError("tool-internal SDK evidence is malformed")
        identity = diagnostics[0]
        if any(character in identity for character in "\t\r\n"):
            raise ValueError("tool-internal SDK identity is not TSV-safe")
        with open(sdk_receipt, "w", encoding="utf-8", newline="\n") as stream:
            stream.write("label\tidentity\n")
            stream.write(f"tool-internal\t{identity}\n")
except (OSError, ValueError, json.JSONDecodeError) as error:
    print(f"agent-gate: report validation failed: {error}", file=sys.stderr)
    sys.exit(1)
PY
}

record_project_sdk_resolution() {
  local project=$1 label=$2 receipt=$3 output expected_extensions
  shift 3
  output=$(cd "$SDK_DIRECTORY" && trusted_dotnet msbuild "$project" \
    -getProperty:NETCoreSdkVersion -getProperty:MSBuildExtensionsPath "$@")
  expected_extensions="$(dirname "$TRUSTED_DOTNET")/sdk/$ORCHESTRATION_SDK_VERSION"
  (cd "$SDK_DIRECTORY" && python3 -I - "$label" "$project" "$ORCHESTRATION_SDK_VERSION" \
    "$expected_extensions" "$receipt" "$output") <<'PY' || exit "$ORCHESTRATION_REFUSAL"
import json, os, sys
label, project, expected_sdk, expected_extensions, receipt, raw = sys.argv[1:]
try:
    properties = json.loads(raw).get("Properties")
    if not isinstance(properties, dict):
        raise ValueError("MSBuild property query did not return a Properties object")
    sdk = properties.get("NETCoreSdkVersion")
    extensions = properties.get("MSBuildExtensionsPath")
    if sdk != expected_sdk:
        raise ValueError(f"project resolved SDK {sdk!r}, expected {expected_sdk!r}")
    if not isinstance(extensions, str) or os.path.realpath(extensions) != os.path.realpath(expected_extensions):
        raise ValueError("project MSBuildExtensionsPath does not match the receipt-bound SDK")
    if any("\t" in value or "\n" in value for value in (label, project, sdk, extensions)):
        raise ValueError("SDK resolution receipt value is not TSV-safe")
    with open(receipt, "a", encoding="utf-8", newline="\n") as stream:
        stream.write(f"{label}\t{project}\t{sdk}\t{extensions}\n")
except (OSError, ValueError, json.JSONDecodeError) as error:
    print(f"agent-gate: project SDK validation failed: {error}", file=sys.stderr)
    sys.exit(1)
PY
}

bind_canonical_tool() {
  local payload_canonical expected_command
  [[ "$TOOL_PAYLOAD" = "$(realpath -m -- "$TOOL_PAYLOAD")" ]] || fail "payload path is not canonical"
  payload_canonical=$(canonical_existing "$TOOL_PAYLOAD" payload)
  [[ ! -L "$TOOL_PAYLOAD" ]] || fail "payload path is symbolic"
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) expected_command="$payload_canonical/mutate4csharp.exe" ;;
    *) expected_command="$payload_canonical/mutate4csharp" ;;
  esac
  [[ -f "$expected_command" && ! -L "$expected_command" && -x "$expected_command" ]] ||
    fail "canonical payload executable is missing, symbolic, or not executable"
  TOOL_COMMAND=$(canonical_existing "$expected_command" "canonical payload executable")
  [[ "$TOOL_COMMAND" = "$expected_command" ]] || fail "canonical payload executable escapes the payload"
}

prepare() {
  [[ $# -eq 3 ]] || fail "prepare requires SOURCE_REPOSITORY WORKSPACE RECEIPT"
  local source_repository workspace receipt
  source_repository=$(canonical_boundary "$1" "source repository")
  workspace=$(canonical_new_target "$2" workspace)
  receipt=$(canonical_new_target "$3" receipt)
  require_external "$workspace" "$source_repository" workspace
  require_external "$receipt" "$source_repository" receipt
  [[ -z "$(git -C "$source_repository" status --porcelain=v1 --untracked-files=all)" ]] ||
    fail "source repository must be clean"

  local source_commit local_tool_version feed config payload package build_repository dotnet_command
  source_commit=$(git -C "$source_repository" rev-parse HEAD)
  [[ "$source_commit" =~ ^[0-9a-f]{40}$ ]] || fail "source repository HEAD is not a full commit"
  local_tool_version="0.1.0-local.$source_commit"
  feed="$workspace/feed"
  config="$workspace/NuGet.local.config"
  payload="$workspace/tool"
  package="$feed/mutate4csharp.$local_tool_version.nupkg"
  mkdir -p "$feed" "$(dirname "$receipt")" "$workspace/dotnet-home" "$workspace/nuget-packages" \
    "$workspace/msbuild/obj" "$workspace/msbuild/bin" "$workspace/msbuild/user-extensions" "$workspace/sdk"
  build_repository="$workspace/source"
  git clone --quiet --no-local "$source_repository" "$build_repository"
  git -C "$build_repository" checkout --quiet --detach "$source_commit"
  [[ -z "$(git -C "$build_repository" status --porcelain=v1 --untracked-files=all)" ]] ||
    fail "external source clone is not clean"
  reject_ancestor_build_controls "$build_repository"
  [[ -f "$build_repository/global.json" && ! -L "$build_repository/global.json" ]] ||
    fail "source repository must contain a regular global.json"
  cp -- "$build_repository/global.json" "$workspace/sdk/global.json"
  printf '<Project />\n' > "$workspace/msbuild/empty.props"
  printf '<Project />\n' > "$workspace/msbuild/empty.targets"
  printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources><clear />' "    <add key=\"mutate4csharp-local\" value=\"$feed\" />" '  </packageSources>' '</configuration>' > "$config"

  dotnet_command=${DOTNET_HOST_PATH:-$(command -v dotnet || true)}
  bind_trusted_dotnet "$dotnet_command"
  bind_neutral_sdk_directory "$workspace/sdk"
  export DOTNET_CLI_HOME="$workspace/dotnet-home"
  export NUGET_PACKAGES="$workspace/nuget-packages"
  local project="$build_repository/src/Mutate4CSharp/Mutate4CSharp.csproj"
  local isolation=(
    "-p:Mutate4CSharpPackIsolation=true"
    "-p:ImportDirectoryBuildProps=false"
    "-p:ImportDirectoryBuildTargets=false"
    "-p:ImportDirectoryPackagesProps=false"
    "-p:DirectoryBuildPropsPath=$workspace/msbuild/empty.props"
    "-p:DirectoryBuildTargetsPath=$workspace/msbuild/empty.targets"
    "-p:CustomBeforeMicrosoftCommonProps="
    "-p:CustomAfterMicrosoftCommonProps="
    "-p:CustomBeforeMicrosoftCommonTargets="
    "-p:CustomAfterMicrosoftCommonTargets="
    "-p:CustomBeforeMicrosoftCSharpTargets="
    "-p:CustomAfterMicrosoftCSharpTargets="
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false"
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false"
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false"
    "-p:MSBuildUserExtensionsPath=$workspace/msbuild/user-extensions"
    "-p:MSBuildProjectExtensionsPath=$workspace/msbuild/obj/"
    "-p:BaseIntermediateOutputPath=$workspace/msbuild/obj/"
    "-p:BaseOutputPath=$workspace/msbuild/bin/"
    "-p:OutputPath=$workspace/msbuild/bin/"
    "-p:PackageOutputPath=$feed"
    "-p:Mutate4CSharpNoAutoResponse=true"
  )
  # -noAutoResponse disables MSBuild.rsp and Directory.Build.rsp discovery.
  (cd "$SDK_DIRECTORY" && trusted_dotnet msbuild "$project" -noAutoResponse -t:Restore -m:1 "-p:RestoreConfigFile=$build_repository/NuGet.Config" "-p:RestorePackagesPath=$NUGET_PACKAGES" -p:RestoreLockedMode=true "${isolation[@]}")
  (cd "$SDK_DIRECTORY" && trusted_dotnet msbuild "$project" -noAutoResponse -t:Pack -m:1 -p:Configuration=Release -p:NoRestore=true "-p:Version=$local_tool_version" "-p:SourceRevisionId=$source_commit" "-p:RepositoryCommit=$source_commit" "${isolation[@]}")
  [[ -f "$package" ]] || fail "pack did not produce the expected package"
  (cd "$SDK_DIRECTORY" && trusted_dotnet tool install mutate4csharp --tool-path "$payload" --version "$local_tool_version" --configfile "$config" --no-cache)
  TOOL_PAYLOAD=$payload
  bind_canonical_tool
  [[ "$(trusted_tool --version)" = "$local_tool_version+$source_commit" ]] ||
    fail "installed tool identity does not match the source commit"
  payload_sha256 "$payload" >/dev/null
  local orchestration_sdk_version
  orchestration_sdk_version=$(trusted_sdk_version)
  # Receipt v3 retains the historical runtime_version key for compatibility; its value is the orchestration SDK.
  write_receipt "$receipt" format mutate4csharp-agent-gate-v3 local_tool_version "$local_tool_version" tool_package "$package" tool_payload "$payload" runtime_version "$orchestration_sdk_version" dotnet_host "$TRUSTED_DOTNET"
  printf 'RECEIPT=%s\nPACKAGE_SHA256=%s\nPAYLOAD_SHA256=%s\nTOOL_SOURCE_COMMIT=%s\nSDK_VERSION=%s\nDOTNET_HOST=%s\n' "$receipt" "$(sha256_file "$package")" "$(payload_sha256 "$payload")" "$source_commit" "$orchestration_sdk_version" "$TRUSTED_DOTNET"
}

gate() {
  [[ $# -eq 9 ]] ||
    fail "gate requires TARGET_REPOSITORY RECEIPT EXPECTED_PACKAGE_SHA256 EXPECTED_PAYLOAD_SHA256 EXPECTED_TOOL_SOURCE_COMMIT TASK_START EXPECTED_TARGET_HEAD REPORT (no-state|default-state)"
  local target_repository receipt report
  target_repository=$(canonical_boundary "$1" "target repository")
  receipt=$(canonical_existing "$2" receipt)
  report=$(canonical_new_target "$8" report)
  local EXPECTED_PACKAGE_SHA256=$3 EXPECTED_PAYLOAD_SHA256=$4 EXPECTED_TOOL_SOURCE_COMMIT=$5
  local TASK_START=$6 EXPECTED_TARGET_HEAD=$7 state_mode=$9
  [[ "$EXPECTED_PACKAGE_SHA256" =~ ^[0-9a-f]{64}$ ]] || fail "invalid approved package SHA256"
  [[ "$EXPECTED_PAYLOAD_SHA256" =~ ^[0-9a-f]{64}$ ]] || fail "invalid approved payload SHA256"
  [[ "$EXPECTED_TOOL_SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || fail "invalid approved tool source commit"
  [[ "$TASK_START" =~ ^[0-9a-f]{40}$ ]] || fail "TASK_START must be an approved full commit"
  [[ "$EXPECTED_TARGET_HEAD" =~ ^[0-9a-f]{40}$ ]] || fail "expected target HEAD must be a full commit"
  [[ "$state_mode" = no-state || "$state_mode" = default-state ]] || fail "unknown state mode"
  require_external "$receipt" "$target_repository" receipt
  require_external "$report" "$target_repository" report
  read_receipt "$receipt"
  bind_trusted_dotnet "${DOTNET_HOST_PATH:-$DOTNET_HOST}"
  [[ "$TRUSTED_DOTNET" = "$DOTNET_HOST" ]] || fail "trusted dotnet host does not match receipt"
  [[ "$TOOL_PACKAGE" = "$(realpath -m -- "$TOOL_PACKAGE")" ]] || fail "package path is not canonical"
  TOOL_PACKAGE=$(canonical_existing "$TOOL_PACKAGE" package)
  require_external "$TOOL_PACKAGE" "$target_repository" package
  require_external "$TOOL_PAYLOAD" "$target_repository" payload
  bind_canonical_tool
  bind_neutral_sdk_directory "$(dirname "$TOOL_PAYLOAD")/sdk"
  require_external "$SDK_DIRECTORY" "$target_repository" neutral-sdk-directory
  [[ "$(trusted_sdk_version)" = "$ORCHESTRATION_SDK_VERSION" ]] ||
    fail "trusted runtime version does not match receipt (legacy field contains orchestration SDK)"
  [[ "$(sha256_file "$TOOL_PACKAGE")" = "$EXPECTED_PACKAGE_SHA256" ]] || fail "package SHA256 mismatch"
  [[ "$(payload_sha256 "$TOOL_PAYLOAD")" = "$EXPECTED_PAYLOAD_SHA256" ]] || fail "payload SHA256 mismatch"
  [[ "$LOCAL_TOOL_VERSION" = "0.1.0-local.$EXPECTED_TOOL_SOURCE_COMMIT" ]] ||
    fail "receipt version does not match approved tool source commit"
  local observed_version
  observed_version=$(trusted_tool --version)
  [[ "$observed_version" = "$LOCAL_TOOL_VERSION+$EXPECTED_TOOL_SOURCE_COMMIT" ]] ||
    fail "tool identity does not match approved source commit"
  [[ "$(git -C "$target_repository" rev-parse "$TASK_START^{commit}")" = "$TASK_START" ]] ||
    fail "TASK_START is not the approved direct target-repository commit"
  [[ "$(git -C "$target_repository" rev-parse HEAD)" = "$EXPECTED_TARGET_HEAD" ]] ||
    fail "target repository HEAD changed from its approved current commit"
  local arguments=(check --base "$TASK_START" --report "$report")
  [[ "$state_mode" = no-state ]] && arguments+=(--no-state)
  local exit_code
  if (cd "$target_repository" && trusted_tool "${arguments[@]}"); then
    exit_code=0
  else
    exit_code=$?
  fi
  [[ "$(sha256_file "$TOOL_PACKAGE")" = "$EXPECTED_PACKAGE_SHA256" ]] || fail "package changed during gate"
  [[ "$(payload_sha256 "$TOOL_PAYLOAD")" = "$EXPECTED_PAYLOAD_SHA256" ]] || fail "payload changed during gate"
  [[ "$(trusted_tool --version)" = "$observed_version" ]] || fail "tool identity changed during gate"
  [[ "$(git -C "$target_repository" rev-parse HEAD)" = "$EXPECTED_TARGET_HEAD" ]] ||
    fail "target repository HEAD changed during gate"
  validate_report "$report" "$exit_code" "$state_mode" "$target_repository"
  return "$exit_code"
}

verify_example() {
  [[ $# -eq 8 ]] ||
    fail "verify-example requires TARGET_REPOSITORY RECEIPT EXPECTED_PACKAGE_SHA256 EXPECTED_PAYLOAD_SHA256 EXPECTED_TOOL_SOURCE_COMMIT TASK_START EXPECTED_TARGET_HEAD REPORT_DIRECTORY"
  local target_repository receipt=$2 package_sha=$3 payload_sha=$4 tool_commit=$5
  local task_start=$6 target_head=$7 report_directory
  target_repository=$(canonical_boundary "$1" "target repository")
  report_directory=$(canonical_new_target "$8" "report directory")
  require_external "$report_directory" "$target_repository" report-directory
  reject_ancestor_build_controls "$target_repository"
  mkdir -p "$report_directory/nuget-packages" "$report_directory/user-home" \
    "$report_directory/dotnet-home" "$report_directory/nuget-http-cache" \
    "$report_directory/nuget-plugins-cache" "$report_directory/nuget-scratch" \
    "$report_directory/xdg-config" "$report_directory/xdg-data" "$report_directory/xdg-cache"
  export HOME="$report_directory/user-home"
  export DOTNET_CLI_HOME="$report_directory/dotnet-home"
  export NUGET_PACKAGES="$report_directory/nuget-packages"
  export NUGET_HTTP_CACHE_PATH="$report_directory/nuget-http-cache"
  export NUGET_PLUGINS_CACHE_PATH="$report_directory/nuget-plugins-cache"
  export NUGET_SCRATCH="$report_directory/nuget-scratch"
  export XDG_CONFIG_HOME="$report_directory/xdg-config"
  export XDG_DATA_HOME="$report_directory/xdg-data"
  export XDG_CACHE_HOME="$report_directory/xdg-cache"

  receipt=$(canonical_existing "$receipt" receipt)
  read_receipt "$receipt"
  bind_trusted_dotnet "${DOTNET_HOST_PATH:-$DOTNET_HOST}"
  [[ "$TRUSTED_DOTNET" = "$DOTNET_HOST" ]] || fail "trusted dotnet host does not match receipt"
  TOOL_PACKAGE=$(canonical_existing "$TOOL_PACKAGE" package)
  bind_canonical_tool
  bind_neutral_sdk_directory "$(dirname "$TOOL_PAYLOAD")/sdk"
  require_external "$SDK_DIRECTORY" "$target_repository" neutral-sdk-directory
  [[ "$(trusted_sdk_version)" = "$ORCHESTRATION_SDK_VERSION" ]] ||
    fail "trusted runtime version does not match receipt (legacy field contains orchestration SDK)"
  [[ "$(sha256_file "$TOOL_PACKAGE")" = "$package_sha" ]] || fail "package SHA256 mismatch"
  [[ "$(payload_sha256 "$TOOL_PAYLOAD")" = "$payload_sha" ]] || fail "payload SHA256 mismatch"
  [[ "$(trusted_tool --version)" = "$LOCAL_TOOL_VERSION+$tool_commit" ]] || fail "tool identity does not match approved source commit"

  local mapping_control="$report_directory/mapping-control"
  local surrogate_public="$report_directory/surrogate-public"
  local local_feed="$report_directory/mutate4csharp-local-feed"
  local negative_packages="$report_directory/negative-packages"
  local negative_home="$report_directory/negative-home"
  local positive_packages="$report_directory/positive-packages"
  local positive_home="$report_directory/positive-home"
  mkdir -p "$mapping_control/.config" "$surrogate_public" "$local_feed" "$negative_packages" "$negative_home" "$positive_packages" "$positive_home"
  cp -R "$target_repository/." "$mapping_control/"
  cp "$TOOL_PACKAGE" "$surrogate_public/"
  printf '%s\n' '{' '  "version": 1,' '  "isRoot": true,' '  "tools": {' '    "mutate4csharp": {' "      \"version\": \"$LOCAL_TOOL_VERSION\"," '      "commands": [ "mutate4csharp" ]' '    }' '  }' '}' > "$mapping_control/.config/dotnet-tools.json"
  (cd "$SDK_DIRECTORY" && python3 -I - "$mapping_control/NuGet.Config" "$surrogate_public") <<'PY'
import sys
import xml.etree.ElementTree as ET
path, replacement = sys.argv[1:]
tree = ET.parse(path)
for element in tree.findall(".//packageSources/add"):
    if element.attrib.get("value") == "https://api.nuget.org/v3/index.json":
        element.set("value", replacement)
tree.write(path, encoding="utf-8", xml_declaration=True)
PY
  local negative_log="$report_directory/negative-restore.log"
  if (cd "$SDK_DIRECTORY" && NUGET_PACKAGES="$negative_packages" DOTNET_CLI_HOME="$negative_home" trusted_dotnet tool restore --tool-manifest "$mapping_control/.config/dotnet-tools.json" --configfile "$mapping_control/NuGet.Config" --no-cache >"$negative_log" 2>&1); then
    fail "plain restore accepted mutate4csharp from the public/wildcard source"
  fi
  grep -Fqi 'mutate4csharp' "$negative_log" || fail "negative restore did not identify mutate4csharp"
  grep -Fqi 'mutate4csharp-local-feed' "$negative_log" || fail "negative restore did not identify the empty mapped local feed"
  cp "$TOOL_PACKAGE" "$local_feed/"
  (cd "$SDK_DIRECTORY" && NUGET_PACKAGES="$positive_packages" DOTNET_CLI_HOME="$positive_home" trusted_dotnet tool restore --tool-manifest "$mapping_control/.config/dotnet-tools.json" --configfile "$mapping_control/NuGet.Config" --no-cache)

  local example_project="$target_repository/src/Example/Example.csproj"
  local example_tests="$target_repository/tests/Example.Tests/Example.Tests.csproj"
  local example_config="$target_repository/NuGet.Config"
  local sdk_resolution="$report_directory/sdk-resolution.tsv"
  local tool_sdk_resolution="$report_directory/tool-sdk-resolution.tsv"
  local example_user_extensions="$report_directory/msbuild-user-extensions"
  mkdir -p "$example_user_extensions"
  local example_isolation=(
    "-p:CustomBeforeMicrosoftCommonProps="
    "-p:CustomAfterMicrosoftCommonProps="
    "-p:CustomBeforeMicrosoftCommonTargets="
    "-p:CustomAfterMicrosoftCommonTargets="
    "-p:CustomBeforeMicrosoftCSharpTargets="
    "-p:CustomAfterMicrosoftCSharpTargets="
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false"
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false"
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false"
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false"
    "-p:MSBuildUserExtensionsPath=$example_user_extensions"
  )
  printf 'label\tproject\tNETCoreSdkVersion\tMSBuildExtensionsPath\n' > "$sdk_resolution"
  record_project_sdk_resolution "$example_project" production "$sdk_resolution" "${example_isolation[@]}"
  (cd "$SDK_DIRECTORY" && trusted_dotnet restore "$example_project" --configfile "$example_config" --locked-mode --no-cache --packages "$NUGET_PACKAGES" "${example_isolation[@]}")
  (cd "$SDK_DIRECTORY" && trusted_dotnet build "$example_project" -c Release -m:1 --no-restore "${example_isolation[@]}")
  record_project_sdk_resolution "$example_tests" tests "$sdk_resolution" "${example_isolation[@]}"
  (cd "$SDK_DIRECTORY" && trusted_dotnet restore "$example_tests" --configfile "$example_config" --locked-mode --no-cache --packages "$NUGET_PACKAGES" "${example_isolation[@]}")
  (cd "$SDK_DIRECTORY" && trusted_dotnet build "$example_tests" -c Release -m:1 --no-restore "${example_isolation[@]}")
  (cd "$SDK_DIRECTORY" && trusted_dotnet test "$example_tests" -c Release -m:1 --no-build --no-restore "${example_isolation[@]}" -- xUnit.MaxParallelThreads=1)

  local no_state_exit default_state_exit
  if gate "$target_repository" "$receipt" "$package_sha" "$payload_sha" "$tool_commit" "$task_start" "$target_head" "$report_directory/no-state.json" no-state; then no_state_exit=0; else no_state_exit=$?; fi
  TOOL_SDK_RECEIPT=$tool_sdk_resolution
  if gate "$target_repository" "$receipt" "$package_sha" "$payload_sha" "$tool_commit" "$task_start" "$target_head" "$report_directory/default-state.json" default-state; then default_state_exit=0; else default_state_exit=$?; fi
  TOOL_SDK_RECEIPT=
  [[ "$no_state_exit" -eq 4 ]] || fail "strict no-state example did not return expected incomplete exit 4"
  [[ "$default_state_exit" -eq 4 ]] || fail "strict default-state example did not return expected incomplete exit 4"
}

case "${1:-}" in
  prepare) shift; prepare "$@" ;;
  gate) shift; gate "$@" ;;
  verify-example) shift; verify_example "$@" ;;
  *) fail "usage: agent-gate.sh prepare ... | gate ... | verify-example ..." ;;
esac
