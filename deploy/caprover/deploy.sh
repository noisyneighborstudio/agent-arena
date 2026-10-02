#!/bin/sh
# Deploy the always-on Pezz arena to CapRover.
#   ASDF_NODEJS_VERSION=22.22.0 caprover login -u https://captain.<root> -n <machine>   (once)
#   deploy/caprover/deploy.sh [machine] [app]
# The invite code is generated once and kept in ~/.config/pezz/<app>-invite (chmod 600).
set -e
MACHINE=${1:-captain-01}
APP=${2:-pezz}
export ASDF_NODEJS_VERSION=${ASDF_NODEJS_VERSION:-22.22.0}
cd "$(dirname "$0")/../.."
mkdir -p ~/.config/pezz
INV_FILE=~/.config/pezz/$APP-invite
[ -s "$INV_FILE" ] || { python3 -c 'import secrets; print("pezz-" + secrets.token_hex(4))' > "$INV_FILE"; chmod 600 "$INV_FILE"; }
INVITE=$(cat "$INV_FILE")
ROOT=$(caprover api -n "$MACHINE" -t /user/system/info -m GET -o true | python3 -c 'import sys,json,re; t=sys.stdin.read(); m=re.search(r"\"rootDomain\":\s*\"([^\"]+)\"", t); print(m.group(1) if m else "")')
[ -n "$ROOT" ] || { echo "Not logged in to $MACHINE (run caprover login)"; exit 1; }
PUBLIC="https://$APP.$ROOT"
echo "Deploying $APP to $MACHINE as $PUBLIC"
caprover api -n "$MACHINE" -t /user/apps/appDefinitions/register -m POST -d "{\"appName\":\"$APP\",\"hasPersistentData\":false}" -o false || true
caprover api -n "$MACHINE" -t /user/apps/appDefinitions/update -m POST -o false -d "{\"appName\":\"$APP\",\"instanceCount\":1,\"containerHttpPort\":8080,\"notExposeAsWebApp\":false,\"forceSsl\":true,\"websocketSupport\":true,\"envVars\":[{\"key\":\"PEZZ_INVITE\",\"value\":\"$INVITE\"},{\"key\":\"PEZZ_PUBLIC_URL\",\"value\":\"$PUBLIC\"}]}"
TAR=$(mktemp -t pezz).tar
git ls-files captain-definition deploy/caprover headless/Pez.Headless.csproj headless/Program.cs headless/Tests.cs \
  'unity/Assets/Pez/Sim/*.cs' 'unity/Assets/Pez/Api/*.cs' mcp/package.json mcp/package-lock.json 'mcp/*.js' mcp/viewer.html \
  'unity/Assets/Pez/Resources/PezIcons/*.png' | tar -cf "$TAR" -T -
caprover deploy -n "$MACHINE" -a "$APP" -t "$TAR"
caprover api -n "$MACHINE" -t /user/apps/appDefinitions/enablebasedomainssl -m POST -d "{\"appName\":\"$APP\"}" -o false || true
rm -f "$TAR"
echo "Live: $PUBLIC/play   (invite code in $INV_FILE)"
