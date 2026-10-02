# SignalFish .NET devcontainer.
#
# Multi-stage COPY instead of curl-installers: registry layers are cached by
# digest (faster and more reproducible than downloading tarballs per build),
# and pinned image args make toolchain bumps a one-line diff.

ARG BASE_IMAGE=mcr.microsoft.com/devcontainers/dotnet:10.0-noble
ARG SDK8_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0-noble
ARG NODE_IMAGE=node:22-bookworm
ARG UV_IMAGE=ghcr.io/astral-sh/uv:0.9

# Named import stages: BuildKit does not expand ARGs in COPY --from, so each
# source image gets a FROM alias instead. The last stage is the final image.
FROM ${SDK8_IMAGE} AS sdk8
FROM ${NODE_IMAGE} AS node
FROM ${UV_IMAGE} AS uv
FROM ${BASE_IMAGE}

# CI (dotnet.yml) builds on 10.0.x and 8.0.x. The image provides SDK 10; the
# SDK 8 image's versioned subtrees are merged into the SAME root so every
# SDK/runtime stays visible to one muxer. Merging the versioned parents
# (sdk/, shared/, packs/) brings exactly the 8.0-era content and preserves
# the image's 10.0 muxer; the trailing `dotnet --list-sdks` fails the build
# if the merge ever breaks.
COPY --from=sdk8 /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=sdk8 /usr/share/dotnet/shared /usr/share/dotnet/shared
COPY --from=sdk8 /usr/share/dotnet/packs /usr/share/dotnet/packs
RUN /usr/share/dotnet/dotnet --list-sdks \
    && /usr/share/dotnet/dotnet --list-sdks | grep -q "^8\." \
    && /usr/share/dotnet/dotnet --list-sdks | grep -q "^10\."

# Node.js 22 LTS from the official image, system-wide and nvm-free: nvm
# refuses to run when NPM_CONFIG_PREFIX is set (needed below for sudo-free
# npm -g), so the nvm-based node feature cannot be combined with that design.
# npm/npx/corepack are symlinks into lib/node_modules, hence both copies.
COPY --from=node /usr/local/bin /usr/local/bin
COPY --from=node /usr/local/lib /usr/local/lib
RUN node --version && npm --version

# uv (and uvx) for the git MCP server (uvx mcp-server-git); a single static
# pair of binaries, no Python at build time.
COPY --from=uv /uv /uvx /usr/local/bin/
RUN uv --version && uvx --version

# The .NET base image currently ships an ARM64 PowerShell launcher whose
# nested managed payload is x86-64. Direct `pwsh --version` succeeds, but a
# nested invocation (used by the repo's git hooks) fails with exec format
# error. Replace it with the official, checksum-verified archive for the
# target architecture so both direct and nested invocations are usable.
ARG POWERSHELL_VERSION=7.6.6
ARG POWERSHELL_ARM64_SHA256=924829e54c983648f6f1419a2dc7f9433c861b2fb5bd57736ff096c24f133729
ARG POWERSHELL_X64_SHA256=ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc
RUN set -eux; \
    case "$(dpkg --print-architecture)" in \
      amd64) ps_arch=x64; ps_sha256="${POWERSHELL_X64_SHA256}" ;; \
      arm64 | aarch64) ps_arch=arm64; ps_sha256="${POWERSHELL_ARM64_SHA256}" ;; \
      *) echo "unsupported PowerShell architecture: $(dpkg --print-architecture)" >&2; exit 1 ;; \
    esac; \
    ps_archive="/tmp/powershell-${POWERSHELL_VERSION}-linux-${ps_arch}.tar.gz"; \
    curl --fail --location --retry 3 --retry-all-errors \
      "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-${ps_arch}.tar.gz" \
      --output "$ps_archive"; \
    printf '%s  %s\n' "$ps_sha256" "$ps_archive" | sha256sum --check --status; \
    rm -rf /opt/microsoft/powershell/7; \
    mkdir -p /opt/microsoft/powershell/7; \
    tar -xzf "$ps_archive" -C /opt/microsoft/powershell/7; \
    chmod 0755 /opt/microsoft/powershell/7/pwsh; \
    ln -sfn /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh; \
    ln -sfn /opt/microsoft/powershell/7/pwsh /usr/bin/pwsh; \
    rm -f "$ps_archive"; \
    pwsh --version; \
    pwsh -NoProfile -Command '& pwsh -NoProfile -Command "exit 0"'

# Global npm tools install to a user-writable prefix: "npm install -g" never
# needs sudo. Image ENV (not containerEnv) so every process inherits it.
ENV NPM_CONFIG_PREFIX="/home/vscode/.npm-global" \
    NPM_CONFIG_UPDATE_NOTIFIER="false" \
    PATH="/home/vscode/.npm-global/bin:${PATH}"

# The lifecycle scripts serialize npm installs and config writes with flock;
# a missing util-linux binary would silently disable that protection.
RUN command -v flock >/dev/null && flock --version

# Pre-create every devcontainer.json volume mountpoint, owned by vscode.
# Two ways this matters (both observed 2026-09-19: the ~/.cache/uv volume
# mount made the runtime create ~/.cache root-owned, so opencode's
# `mkdir ~/.cache/opencode` failed with EACCES):
# 1. The container runtime creates missing mountpoint ANCESTORS as root:root,
#    which locks the vscode user out of the whole subtree (e.g. all of
#    ~/.cache). Existing image dirs are never touched.
# 2. An empty named volume is initialized from the mount target's image
#    content/ownership; a target missing from the image yields a root:root
#    volume. Existing dirs give vscode-owned volumes.
# Keep this list in sync with the "mounts" in devcontainer.json. Parent dirs
# must precede their children: `install -d -o` applies ownership only to the
# final component of each argument (caught by the assertions below).
RUN install -d -o vscode -g vscode \
      /home/vscode/.cache \
      /home/vscode/.cache/uv \
      /home/vscode/.local \
      /home/vscode/.local/share \
      /home/vscode/.local/share/opencode \
      /home/vscode/.config \
      /home/vscode/.config/gh \
      /home/vscode/.npm-global \
      /home/vscode/.npm \
      /home/vscode/.claude \
      /home/vscode/.claude-zai \
      /home/vscode/.claude-openrouter \
      /home/vscode/.codex \
      /home/vscode/.copilot \
      /home/vscode/.nuget/packages \
      /home/vscode/.dotnet \
    && [ "$(stat -c %U /home/vscode/.cache)" = vscode ] \
    && [ "$(stat -c %U /home/vscode/.cache/uv)" = vscode ] \
    && [ "$(stat -c %U /home/vscode/.local/share/opencode)" = vscode ] \
    && [ "$(stat -c %U /home/vscode/.claude-zai)" = vscode ]
