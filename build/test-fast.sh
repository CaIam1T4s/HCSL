#!/usr/bin/env bash
# Fast, targeted build/test loops. Run from anywhere.
#
#   ./build/test-fast.sh server maps      # build ServerTests, run --only maps
#   ./build/test-fast.sh host cfg         # build HostTests, run --only cfg
#   ./build/test-fast.sh tui              # build + run TuiTests
#   ./build/test-fast.sh deployment
#   ./build/test-fast.sh full             # build solution, run every test project
#
# MSBuild node reuse is disabled and build servers are shut down at exit so
# repeated runs do not leave background dotnet/MSBuild processes behind.
set -euo pipefail
cd "$(dirname "$0")/.."

cleanup() { dotnet build-server shutdown >/dev/null 2>&1 || true; }
trap cleanup EXIT

mode="${1:-server}"
section="${2:-}"
args=()
if [[ -n "$section" ]]; then
    args+=(--only "$section")
fi

build_only() {
    local project=$1
    dotnet build "tests/$project/$project.csproj" --no-restore -v:q -nodeReuse:false
}

run_built() {
    local project=$1
    shift
    dotnet run --project "tests/$project/$project.csproj" --no-build -- "$@"
}

case "$mode" in
    server)
        build_only HCSL.ServerTests
        run_built HCSL.ServerTests "${args[@]}"
        ;;
    host)
        build_only HCSL.HostTests
        run_built HCSL.HostTests "${args[@]}"
        ;;
    tui|deployment|ipc|instance|integration|player|bootstrap|steamcmd|core|application)
        declare -A projects=(
            [tui]=HCSL.TuiTests [deployment]=HCSL.DeploymentTests [ipc]=HCSL.IpcTests
            [instance]=HCSL.InstanceTests [integration]=HCSL.IntegrationTests
            [player]=HCSL.PlayerTests [bootstrap]=HCSL.BootstrapTests
            [steamcmd]=HCSL.SteamCmdTests [core]=HCSL.Core.Tests [application]=HCSL.Application.Tests
        )
        project="${projects[$mode]}"
        build_only "$project"
        run_built "$project" "${args[@]}"
        ;;
    full)
        dotnet build Hello_Counter_Strike_Launcher.slnx --no-restore -v:q -nodeReuse:false
        for project in HCSL.Core.Tests HCSL.Application.Tests HCSL.DeploymentTests \
            HCSL.ServerTests HCSL.HostTests HCSL.TuiTests HCSL.IpcTests HCSL.InstanceTests \
            HCSL.IntegrationTests HCSL.PlayerTests HCSL.BootstrapTests HCSL.SteamCmdTests; do
            echo "=== $project ==="
            run_built "$project"
        done
        ;;
    *)
        echo "Usage: $0 {server|host|tui|deployment|ipc|instance|integration|player|bootstrap|steamcmd|core|application|full} [section]" >&2
        exit 2
        ;;
esac
