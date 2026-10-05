# A Pezz room host for any Linux cloud machine: the headless engine plus mcp/roomhost.js, joined to the tailnet with
# Tailscale in userspace mode (no privileges needed). The gateway reaches it at <tailnet name>:7700 like any other host.
#
#   docker build --platform linux/amd64 -f deploy/roomhost/Dockerfile -t pezz-roomhost .   (most clouds; linux/arm64 for Graviton/Ampere)
#   docker run -d --restart unless-stopped --name pezz-roomhost \
#     -e TS_AUTHKEY=tskey-... -e PEZZ_HOST_SECRET=<~/.config/pezz/roomhost-secret> -e PEZZ_HOST_REGION=eu-west \
#     -e PEZZ_HOST_NAME=pezz-eu-1 -v pezz-roomhost:/data pezz-roomhost
# then on the gateway machine: arena/roomhost.sh add-url pezz-eu-1 eu-west http://pezz-eu-1:7700
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS engine
ARG TARGETARCH
WORKDIR /src
COPY headless/ headless/
COPY unity/Assets/Pez/Sim/ unity/Assets/Pez/Sim/
COPY unity/Assets/Pez/Api/ unity/Assets/Pez/Api/
RUN dotnet publish headless/Pez.Headless.csproj -c Release -r linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) --self-contained true -p:PublishSingleFile=true -o /out

FROM node:22-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl tini libicu72 \
 && curl -fsSL https://tailscale.com/install.sh | sh && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=engine /out/pez-headless /app/pez-headless
COPY mcp/roomhost.js /app/roomhost.js
COPY deploy/roomhost/start.sh /app/start.sh
RUN chmod +x /app/start.sh /app/pez-headless && useradd -r -u 10001 -d /data pezz && mkdir -p /data && chown pezz /data
ENV HOME=/data PEZZ_ENGINE=/app/pez-headless PEZZ_HOST_BIND=127.0.0.1 PEZZ_HOST_PORT=7700
VOLUME /data
ENTRYPOINT ["/usr/bin/tini", "--", "/app/start.sh"]
