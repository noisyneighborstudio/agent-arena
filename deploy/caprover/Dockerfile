# The Pezz server on CapRover: the headless engine (room 1 and any extra rooms), the gateway, and the browser build, in one
# container. deploy/caprover/deploy.sh assembles the upload: the engine is published on the Mac (a framework-dependent
# .dll runs on any OS with the .NET runtime) and the browser build comes from Unity there, so the server only installs the
# gateway's packages and the Claude CLI (for the team-name check). Only the gateway (8080) is exposed; the engines' APIs
# stay inside the container. Everything that must survive a deploy lives under /data (the app's persistent volume).
FROM node:24-bookworm-slim AS node
WORKDIR /app/mcp
COPY mcp/package.json mcp/package-lock.json ./
RUN npm ci --omit=dev
# The team-name check runs `claude -p` (mcp/names.js); it signs in with CLAUDE_CODE_OAUTH_TOKEN from the app's env.
RUN npm install -g --prefix /opt/claude @anthropic-ai/claude-code && /opt/claude/bin/claude --version

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends tini ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /opt/claude /opt/claude
WORKDIR /app
COPY engine/ engine/
COPY mcp/ mcp/
COPY --from=node /app/mcp/node_modules mcp/node_modules
COPY arena/web3d/ arena/web3d/
COPY unity/Assets/Pez/Resources/PezIcons/ unity/Assets/Pez/Resources/PezIcons/
COPY unity/Assets/Pez/Resources/PezModels/ unity/Assets/Pez/Resources/PezModels/
COPY build/web/ build/web/
COPY deploy/caprover/serve.mjs serve.mjs
COPY BUILD_INFO BUILD_INFO
RUN useradd -r -u 10001 -d /data pezz && mkdir -p /data/logs /app/arena && ln -s /data/logs /app/arena/logs \
 && chown -R pezz /data && chown pezz /app/arena
USER pezz
ENV HOME=/data PATH=/opt/claude/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PEZZ_GATEWAY_HOST=0.0.0.0 PEZZ_GATEWAY_PORT=8080
VOLUME /data
EXPOSE 8080
STOPSIGNAL SIGTERM
ENTRYPOINT ["/usr/bin/tini", "--", "node", "/app/serve.mjs"]
