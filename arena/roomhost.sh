#!/bin/zsh
# Room hosts: machines that run Pezz rooms for the gateway (mcp/roomhost.js plus a self-contained engine).
#
#   arena/roomhost.sh install <ssh-host> <region> [rooms]   build the engine for that machine, copy it over, and keep the
#                                                           room host running there (launchd on macOS, systemd on Linux)
#   arena/roomhost.sh update <ssh-host>                     ship a new engine and room host; running rooms save and resume
#   arena/roomhost.sh roll-all                              update every installed host (CI runs this after a deploy)
#   arena/roomhost.sh status [<ssh-host>]                   ask a host (or every host the gateway knows) how it is
#   arena/roomhost.sh add-url <name> <region> <url>         register a host you run some other way (cloud container)
#   arena/roomhost.sh remove <ssh-host>                     stop it and take it off the machine (rooms' saves stay)
#
# The gateway learns hosts from ~/.config/pezz/roomhosts.json (written here); restart the gateway to pick changes up.
# Hosts listen on their tailnet address only, port 7700; every request carries the shared secret in its path.
set -eu
REPO=${0:A:h:h}
CONF=$HOME/.config/pezz; HOSTS=$CONF/roomhosts.json; SECRET_FILE=$CONF/roomhost-secret
mkdir -p "$CONF"; chmod 700 "$CONF"
[ -s "$SECRET_FILE" ] || { openssl rand -hex 24 > "$SECRET_FILE"; chmod 600 "$SECRET_FILE"; }
SECRET=$(cat "$SECRET_FILE")

rid() { # the .NET runtime id for a remote machine
  local os=$(ssh "$1" uname -s) arch=$(ssh "$1" uname -m)
  case "$os-$arch" in
    Darwin-arm64) echo osx-arm64 ;; Darwin-x86_64) echo osx-x64 ;;
    Linux-x86_64) echo linux-x64 ;; Linux-aarch64|Linux-arm64) echo linux-arm64 ;;
    *) echo "unsupported: $os-$arch" >&2; exit 1 ;;
  esac
}

build() { # build <rid> -> prints the engine path (built once per commit and platform)
  local out=$HOME/pezz-engines/$1 commit=$(git -C "$REPO" rev-parse HEAD)
  if [ "$(cat "$out/.commit" 2>/dev/null)" != "$commit" ]; then
    (cd "$REPO/headless" && dotnet publish -c Release -r "$1" --self-contained true -p:PublishSingleFile=true -o "$out" >/dev/null)
    echo "$commit" > "$out/.commit"
  fi
  echo "$out/pez-headless"
}

register() { # register <name> <url> <region> <ssh-host>
  python3 - "$HOSTS" "$1" "$2" "$3" "$4" <<'EOF'
import json, sys
p, name, url, region, ssh = sys.argv[1:]
try: hosts = json.load(open(p))
except Exception: hosts = []
hosts = [h for h in hosts if h["name"] != name] + [{"name": name, "url": url, "region": region, "ssh": ssh}]
json.dump(hosts, open(p, "w"), indent=1)
EOF
  chmod 600 "$HOSTS"
}

ship() { # ship <ssh-host> <region> <rooms>
  local h=$1 region=$2 rooms=$3 r=$(rid "$1")
  echo "building the engine for $r"
  local engine=$(build "$r")
  local dir='$HOME/pezz-roomhost'
  ssh "$h" "mkdir -p ~/pezz-roomhost && chmod 700 ~/pezz-roomhost"
  # Ship the engine under a new name and swap it in, so a running room keeps its binary until it restarts.
  scp -q "$engine" "$h:pezz-roomhost/pez-headless.new"
  scp -q "$REPO/mcp/roomhost.js" "$h:pezz-roomhost/roomhost.js"
  ssh "$h" "mv ~/pezz-roomhost/pez-headless.new ~/pezz-roomhost/pez-headless && chmod +x ~/pezz-roomhost/pez-headless"
  local node=$(ssh "$h" 'command -v node || ls ~/.asdf/installs/nodejs/*/bin/node 2>/dev/null | tail -1')
  local ip=$(ssh "$h" 'tailscale ip -4 2>/dev/null || /Applications/Tailscale.app/Contents/MacOS/Tailscale ip -4' | head -1)
  local name=$(ssh "$h" hostname -s)
  local envs="PEZZ_HOST_SECRET=$SECRET PEZZ_HOST_BIND=$ip PEZZ_HOST_NAME=$name PEZZ_HOST_REGION=$region PEZZ_HOST_ROOMS=$rooms PEZZ_ENGINE=\$HOME/pezz-roomhost/pez-headless"
  if [ "$(ssh "$h" uname -s)" = Darwin ]; then
    ssh "$h" "cat > ~/pezz-roomhost/run.sh <<EOF
#!/bin/zsh
export $envs
exec $node \$HOME/pezz-roomhost/roomhost.js >> \$HOME/pezz-roomhost/roomhost.log 2>&1
EOF
chmod 700 ~/pezz-roomhost/run.sh
cat > ~/Library/LaunchAgents/com.pezz.roomhost.plist <<EOF
<?xml version=\"1.0\" encoding=\"UTF-8\"?>
<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">
<plist version=\"1.0\"><dict>
<key>Label</key><string>com.pezz.roomhost</string>
<key>ProgramArguments</key><array><string>/bin/zsh</string><string>\$HOME/pezz-roomhost/run.sh</string></array>
<key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
</dict></plist>
EOF
# bootout is asynchronous: wait until the old one is gone, then load (retrying), or the load fails with an I/O error.
launchctl bootout gui/\$(id -u)/com.pezz.roomhost 2>/dev/null || true
for i in 1 2 3 4 5 6 7 8 9 10; do launchctl print gui/\$(id -u)/com.pezz.roomhost >/dev/null 2>&1 || break; sleep 1; done
for i in 1 2 3; do launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/com.pezz.roomhost.plist 2>/dev/null && break; sleep 2; done
launchctl print gui/\$(id -u)/com.pezz.roomhost >/dev/null 2>&1 || { echo 'the room host did not start'; exit 1; }"
  else
    ssh "$h" "mkdir -p ~/.config/systemd/user && cat > ~/.config/systemd/user/pezz-roomhost.service <<EOF
[Unit]
Description=Pezz room host
[Service]
Environment=$(echo $envs | sed 's/ / Environment=/g')
ExecStart=$node %h/pezz-roomhost/roomhost.js
Restart=always
[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload && systemctl --user enable --now pezz-roomhost && systemctl --user restart pezz-roomhost"
  fi
  register "$name" "http://$ip:7700/k/$SECRET" "$region" "$h"
  for i in {1..20}; do curl -s -m 3 "http://$ip:7700/k/$SECRET/host" >/dev/null && break; sleep 1; done
  curl -s -m 3 "http://$ip:7700/k/$SECRET/host"; echo
}

case "${1:-status}" in
  install) ship "$2" "${3:-unknown}" "${4:-0}" ;;
  update)
    # New engine and room host on the machine, then each running room saves and resumes on the new engine.
    name=$(ssh "$2" hostname -s)
    region=$(python3 -c "import json,sys; print(next((h['region'] for h in json.load(open('$HOSTS')) if h['name']=='$name'), 'unknown'))")
    ship "$2" "$region" 0
    url=$(python3 -c "import json; print(next(h['url'] for h in json.load(open('$HOSTS')) if h['name']=='$name'))")
    curl -s -m 600 -X POST "$url/roll"; echo ;;
  roll-all)
    # Every host installed with this script (it has an ssh target): update it and roll its rooms. CI runs this after
    # each live deploy, so room hosts run the same engine as room 1.
    for h in $(python3 -c "import json; print(' '.join(h['ssh'] for h in json.load(open('$HOSTS')) if h.get('ssh')))" 2>/dev/null); do
      echo "== $h"; "$0" update "$h" || echo "update of $h FAILED"
    done ;;
  status)
    python3 - "$HOSTS" <<'EOF'
import json, sys, urllib.request
for h in json.load(open(sys.argv[1])):
    try: d = json.load(urllib.request.urlopen(h["url"] + "/host", timeout=4)); print(f'{h["name"]:16} {h["region"]:10} up    {d["platform"]:14} rooms {len(d["rooms"])}/{d["capacity"]}  load {d["load_pct"]}%')
    except Exception as e: print(f'{h["name"]:16} {h["region"]:10} DOWN  {e}')
EOF
    ;;
  add-url)
    # A host that wasn't installed over ssh (a cloud container, deploy/roomhost/Dockerfile): name, region, its tailnet URL.
    register "$2" "${4%/}/k/$SECRET" "$3" ""
    curl -s -m 5 "${4%/}/k/$SECRET/host" || echo "(not answering yet)"; echo ;;
  remove)
    ssh "$2" 'launchctl bootout gui/$(id -u)/com.pezz.roomhost 2>/dev/null; rm -f ~/Library/LaunchAgents/com.pezz.roomhost.plist; systemctl --user disable --now pezz-roomhost 2>/dev/null; rm -rf ~/pezz-roomhost' || true
    name=$(ssh "$2" hostname -s)
    python3 -c "import json; p='$HOSTS'; h=[x for x in json.load(open(p)) if x['name']!='$name']; json.dump(h, open(p,'w'), indent=1)"
    echo "removed $name" ;;
  *) echo "usage: $0 install <ssh-host> <region> [rooms] | update <ssh-host> | status | remove <ssh-host>"; exit 2 ;;
esac
