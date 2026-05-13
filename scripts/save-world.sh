#!/usr/bin/env bash
# =============================================================================
#  save-world.sh — flush l'état mémoire du World vers la DB puis dump SQL.
#
#  Usage :
#    ./scripts/save-world.sh                # flush + dump (recommandé)
#    ./scripts/save-world.sh --no-flush     # dump uniquement (urgence)
#
#  Sans flush, le dump perd l'activité depuis le dernier auto-save
#  (SaveIntervalMinutes = 5 par défaut). À lancer SYSTÉMATIQUEMENT avant un
#  docker compose down / restart.
#
#  Tout passe par docker exec giny-mysql : pas besoin de credentials admin,
#  pas d'appel HTTP. Le World poll la table `actions` toutes les 1.5s et y
#  consomme l'action `save_now` (qui appelle WorldSaveManager.PerformSave).
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a

BACKUP_DIR="$ROOT_DIR/server/backups"
mkdir -p "$BACKUP_DIR"

if [ -t 1 ]; then
    C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_DIM=$'\033[2m'; C_RESET=$'\033[0m'
else
    C_GREEN=""; C_RED=""; C_DIM=""; C_RESET=""
fi

ok()   { printf "%s✓%s %s\n" "$C_GREEN" "$C_RESET" "$*"; }
err()  { printf "%s✗%s %s\n" "$C_RED" "$C_RESET" "$*" >&2; }
step() { printf "%s→%s %s\n" "$C_DIM" "$C_RESET" "$*"; }

SKIP_FLUSH=0
if [ "${1:-}" = "--no-flush" ]; then
    SKIP_FLUSH=1
fi

# Helper : exécute du SQL via docker exec giny-mysql en utilisant le password
# du .env (jamais en argv pour ne pas le leaker dans ps).
mysql_exec() {
    docker exec -i giny-mysql sh -c \
        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot -N -B giny_world' \
        "$@"
}

if [ "$SKIP_FLUSH" = "0" ]; then
    step "Insertion action save_now dans la table actions..."
    # On lit LAST_INSERT_ID en deux requêtes séparées : la table actions
    # est auto-increment, plus simple que MAX(Id) pour éviter une race
    # avec d'autres inserts.
    ACTION_ID=$(mysql_exec <<'SQL' | tail -n1
INSERT INTO actions (Type, Payload) VALUES ('save_now', '');
SELECT LAST_INSERT_ID();
SQL
)
    if [ -z "$ACTION_ID" ] || ! [[ "$ACTION_ID" =~ ^[0-9]+$ ]]; then
        err "Impossible d'insérer l'action (réponse: $ACTION_ID)"
        exit 1
    fi
    step "Action #$ACTION_ID enfilée. Attente du traitement par le World (timeout 15s)..."

    # Poll ProcessedAt. Le ActionPoller du World poll toutes les 1.5s.
    DEADLINE=$(( $(date +%s) + 15 ))
    PROCESSED=""
    while [ "$(date +%s)" -lt "$DEADLINE" ]; do
        PROCESSED=$(echo "SELECT IF(ProcessedAt IS NULL, '', 'done') FROM actions WHERE Id = $ACTION_ID;" | mysql_exec | tr -d '[:space:]')
        if [ "$PROCESSED" = "done" ]; then
            break
        fi
        sleep 0.3
    done

    if [ "$PROCESSED" != "done" ]; then
        err "Le World n'a pas traité l'action en 15s (giny-world est-il up ?)"
        err "Continue quand même avec le dump (état potentiellement obsolète)."
    else
        ok "World flushé en DB."
    fi
fi

# mysqldump → server/backups/manual-YYYYMMDD-HHMMSS.sql.gz
TS=$(date +%Y%m%d-%H%M%S)
OUT="$BACKUP_DIR/manual-${TS}.sql.gz"
step "Dump SQL vers $OUT..."

docker exec giny-mysql sh -c \
    'mysqldump --single-transaction --quick --routines --triggers \
       --databases giny_auth giny_world \
       -uroot -p"$MYSQL_ROOT_PASSWORD" | gzip' \
    > "$OUT" 2> /dev/null

SIZE=$(du -h "$OUT" | awk '{print $1}')
ok "Backup créé : $OUT ($SIZE)"
