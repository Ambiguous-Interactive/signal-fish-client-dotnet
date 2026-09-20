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

# Global npm tools install to a user-writable prefix: "npm install -g" never
# needs sudo. Image ENV (not containerEnv) so every process inherits it.
ENV NPM_CONFIG_PREFIX="/home/vscode/.npm-global" \
    NPM_CONFIG_UPDATE_NOTIFIER="false" \
    PATH="/home/vscode/.npm-global/bin:${PATH}"

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
