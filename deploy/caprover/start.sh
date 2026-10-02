#!/bin/sh
# Run the headless engine and the gateway together; if either dies, exit so CapRover restarts the container.
set -e
dotnet engine/pez-headless.dll --port 7777 --open --controllers "${PEZZ_SEATS}" --map-size "${PEZZ_MAP_SIZE}" \
  --max-players "${PEZZ_MAX_PLAYERS}" --house-ais "${PEZZ_HOUSE_AIS}" --house-resign-above "${PEZZ_HOUSE_RESIGN_ABOVE}" \
  --seed "${PEZZ_SEED:-$(date +%s)}" &
ENGINE=$!
for i in $(seq 1 60); do
  node -e "fetch('http://127.0.0.1:7777/api/status').then(()=>process.exit(0),()=>process.exit(1))" && break
  sleep 0.5
done
node mcp/gateway.js &
GATEWAY=$!
while kill -0 $ENGINE 2>/dev/null && kill -0 $GATEWAY 2>/dev/null; do sleep 2; done
echo "engine or gateway exited; stopping container" >&2
kill $ENGINE $GATEWAY 2>/dev/null || true
exit 1
