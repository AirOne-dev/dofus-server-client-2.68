#!/usr/bin/env bash
# Giny entrypoint: rend la config depuis env vars puis lance auth ou world
set -euo pipefail
ROLE="${1:-auth}"

: "${MYSQL_HOST:=mysql}"
: "${MYSQL_USER:=giny}"
: "${MYSQL_PASSWORD:=giny}"
: "${MYSQL_AUTH_DB:=giny_auth}"
: "${MYSQL_WORLD_DB:=giny_world}"
: "${AUTH_PORT:=5555}"
: "${WORLD_PORT:=5556}"
: "${IPC_HOST:=auth}"
: "${IPC_PORT:=800}"
: "${AUTH_API_PORT:=9001}"
: "${WORLD_API_PORT:=9000}"
: "${WORLD_ID:=291}"
: "${WORLD_NAME:=Imagiro}"
: "${JOB_RATE:=1}"
: "${DROP_RATE:=1}"
: "${XP_RATE:=1}"

wait_tcp() {
    local host="$1" port="$2" name="$3" max="${4:-60}"
    echo "[entry] wait $name @ $host:$port"
    local i=0
    until nc -z "$host" "$port" 2>/dev/null; do
        i=$((i+1))
        [ $i -gt $max ] && { echo "[entry] $name unreachable" >&2; exit 1; }
        sleep 1
    done
    echo "[entry] $name OK"
}

case "$ROLE" in
    auth)
        wait_tcp "$MYSQL_HOST" 3306 mysql
        # Wait that init scripts have populated DB (large world dump can take 1-2 min)
        until mysql -h"$MYSQL_HOST" -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" "$MYSQL_AUTH_DB" \
                -e "SELECT 1 FROM accounts LIMIT 1" >/dev/null 2>&1; do
            echo "[entry] waiting accounts table…"
            sleep 2
        done
        cd /opt/giny/auth
        envsubst < /etc/giny/config/auth_config.json.tmpl > config.json
        echo "[entry] auth config:"; cat config.json
        exec dotnet Giny.Auth.dll
        ;;
    world)
        wait_tcp "$MYSQL_HOST" 3306 mysql
        wait_tcp "$IPC_HOST" "$IPC_PORT" auth-ipc 120
        until mysql -h"$MYSQL_HOST" -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" "$MYSQL_WORLD_DB" \
                -e "SELECT 1 FROM maps LIMIT 1" >/dev/null 2>&1; do
            echo "[entry] waiting world tables…"
            sleep 5
        done
        # Giny TcpClient utilise IPAddress.Parse() qui n'accepte pas un nom DNS.
        # On résout IPC_HOST en IP pour la config.
        IPC_HOST_IP=$(getent ahostsv4 "$IPC_HOST" | awk 'NR==1{print $1}')
        export IPC_HOST="${IPC_HOST_IP:-$IPC_HOST}"
        echo "[entry] IPC host resolved to $IPC_HOST"
        cd /opt/giny/world
        envsubst < /etc/giny/config/world_config.json.tmpl > config.json
        echo "[entry] world config:"; cat config.json
        exec dotnet Giny.World.dll
        ;;
    shell|bash|sh)
        exec /bin/bash
        ;;
    *)
        echo "Usage: entrypoint.sh [auth|world|shell]" >&2; exit 1 ;;
esac
