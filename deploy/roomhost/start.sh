#!/bin/sh
# Join the tailnet (userspace networking: inbound tailnet connections arrive on this container's loopback), then run
# the room host. Rooms and their saved games live on /data, so a restarted container resumes them.
set -e
# Local testing only: PEZZ_NO_TAILSCALE=1 listens on the container's own interface instead (publish the port to loopback).
if [ "${PEZZ_NO_TAILSCALE:-}" = 1 ]; then PEZZ_HOST_BIND=0.0.0.0 exec node /app/roomhost.js; fi
[ -n "$TS_AUTHKEY" ] || { echo "TS_AUTHKEY is required (a tailnet auth key, ideally tagged and pre-approved)"; exit 2; }
[ -n "$PEZZ_HOST_SECRET" ] || { echo "PEZZ_HOST_SECRET is required (the gateway machine's ~/.config/pezz/roomhost-secret)"; exit 2; }
mkdir -p /data/tailscale
tailscaled --tun=userspace-networking --state=/data/tailscale/tailscaled.state --socket=/data/tailscale/tailscaled.sock &
for i in $(seq 1 30); do tailscale --socket=/data/tailscale/tailscaled.sock status >/dev/null 2>&1 && break; sleep 1; done
tailscale --socket=/data/tailscale/tailscaled.sock up --authkey="$TS_AUTHKEY" --hostname="${PEZZ_HOST_NAME:-pezz-roomhost}" --accept-dns=false
exec node /app/roomhost.js
