# Always-on Pezz arena: headless engine + arena gateway in one container. Only the gateway (port 80) is exposed;
# the game's admin API listens on 127.0.0.1:7777 inside the container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS engine
WORKDIR /src
COPY headless/ headless/
COPY unity/Assets/Pez/Sim/ unity/Assets/Pez/Sim/
COPY unity/Assets/Pez/Api/ unity/Assets/Pez/Api/
RUN dotnet publish headless/Pez.Headless.csproj -c Release -o /out

FROM node:22-bookworm-slim AS gateway
WORKDIR /app/mcp
COPY mcp/package.json mcp/package-lock.json ./
RUN npm ci --omit=dev
COPY mcp/*.js mcp/viewer.html ./

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends tini && rm -rf /var/lib/apt/lists/*
COPY --from=gateway /usr/local/bin/node /usr/local/bin/node
WORKDIR /app
COPY --from=engine /out engine/
COPY --from=gateway /app/mcp mcp/
COPY unity/Assets/Pez/Resources/PezIcons/*.png unity/Assets/Pez/Resources/PezIcons/
COPY deploy/caprover/start.sh start.sh
RUN chmod +x start.sh && useradd -r -u 10001 pezz && chown -R pezz /app
USER pezz
ENV PEZZ_GATEWAY_HOST=0.0.0.0 PEZZ_GATEWAY_PORT=8080 PEZZ_GAME=http://127.0.0.1:7777 \
    PEZZ_SEATS=ai PEZZ_MAP_SIZE=80 PEZZ_MAX_PLAYERS=8 PEZZ_HOUSE_AIS=1 PEZZ_HOUSE_RESIGN_ABOVE=5
EXPOSE 8080
ENTRYPOINT ["/usr/bin/tini", "--", "./start.sh"]
