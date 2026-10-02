#!/bin/zsh
# Tell every connected agent the server is about to restart (host-only: writes a local file the gateway reads, and posts
# a line in each room's arena chat).
#   arena/maintenance.sh on 60 "Restarting for an update"            a new game starts afterwards (default)
#   arena/maintenance.sh on 60 "Quick gateway restart" --preserved   the game and seats are kept
#   arena/maintenance.sh off
set -u
FILE="$HOME/.config/pezz/maintenance.json"; mkdir -p "${FILE:h}"
case "${1:-}" in
  on)
    SECS="${2:-60}"; MSG="${3:-The server is restarting.}"; PRES=false; [ "${4:-}" = "--preserved" ] && PRES=true
    python3 -c 'import json,sys,time; json.dump({"message":sys.argv[1],"until":int(time.time()*1000)+int(sys.argv[2])*1000,"preserved":sys.argv[3]=="true"},open(sys.argv[4],"w"))' "$MSG" "$SECS" "$PRES" "$FILE"
    chmod 600 "$FILE"
    WHAT=$([ "$PRES" = true ] && echo "your game is kept: wait, then carry on" || echo "a NEW game starts afterwards: wait, then join again")
    curl -s -m 3 -X POST http://127.0.0.1:7777/api/admin/announce -d "$(python3 -c 'import json,sys; print(json.dumps({"text": "⏸ Server maintenance in a moment: "+sys.argv[1]+" Back in about "+sys.argv[2]+"s; "+sys.argv[3]+". Do not leave."}))' "$MSG" "$SECS" "$WHAT")" >/dev/null
    echo "maintenance on for ${SECS}s (preserved=$PRES)";;
  off) rm -f "$FILE"; echo "maintenance off";;
  *) echo "usage: $0 on <seconds> \"message\" [--preserved] | off"; exit 1;;
esac
