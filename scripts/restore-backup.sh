#!/usr/bin/env bash
# OneAir — Restauration interactive d'un dump SQL.
#
# Liste les .sql.gz dans server/backups/, propose un menu, demande confirmation,
# arrête le world (pour éviter d'écrire pendant l'import), restaure, redémarre.
#
# Usage :
#   ./scripts/restore-backup.sh                 # menu interactif
#   ./scripts/restore-backup.sh -y <fichier>    # non-interactif (full path ou nom)
#   ./scripts/restore-backup.sh --latest        # restaure le plus récent
#   ./scripts/restore-backup.sh --keep-world    # ne stoppe pas le world
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_DIR="$ROOT_DIR/server/backups"

# Couleurs (désactivées si non-tty)
if [ -t 1 ]; then
    C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'; C_RED=$'\033[31m'
    C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'; C_RESET=$'\033[0m'
else
    C_BOLD=""; C_DIM=""; C_RED=""; C_GREEN=""; C_YELLOW=""; C_CYAN=""; C_RESET=""
fi

err()  { printf "%s[!]%s %s\n" "$C_RED" "$C_RESET" "$*" >&2; }
info() { printf "%s[*]%s %s\n" "$C_CYAN" "$C_RESET" "$*"; }
ok()   { printf "%s[ok]%s %s\n" "$C_GREEN" "$C_RESET" "$*"; }
ask()  { printf "%s[?]%s %s" "$C_YELLOW" "$C_RESET" "$*"; }

# --- args -------------------------------------------------------------------
ASSUME_YES=0
KEEP_WORLD=0
LATEST=0
TARGET=""
while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes)         ASSUME_YES=1; shift ;;
        --keep-world)     KEEP_WORLD=1; shift ;;
        --latest)         LATEST=1; shift ;;
        -h|--help)
            sed -n '2,11p' "$0"; exit 0 ;;
        --) shift; TARGET="${1:-}"; shift || true ;;
        -*) err "option inconnue: $1"; exit 2 ;;
        *)  TARGET="$1"; shift ;;
    esac
done

# --- env --------------------------------------------------------------------
ENV_FILE="$ROOT_DIR/.env"
if [ -f "$ENV_FILE" ]; then
    set -a; . "$ENV_FILE"; set +a
fi
: "${MYSQL_ROOT_PASSWORD:=rootpw}"
: "${MYSQL_USER:=giny}"
: "${MYSQL_PASSWORD:=giny}"
: "${MYSQL_AUTH_DB:=giny_auth}"
: "${MYSQL_WORLD_DB:=giny_world}"

# --- vérifs containers ------------------------------------------------------
if ! docker ps --format '{{.Names}}' | grep -q '^giny-mysql$'; then
    err "container 'giny-mysql' n'est pas en cours, lance la stack d'abord."
    exit 1
fi

# --- listing ----------------------------------------------------------------
if [ ! -d "$BACKUP_DIR" ]; then
    err "dossier $BACKUP_DIR introuvable."
    exit 1
fi

mapfile -t BACKUPS < <(find "$BACKUP_DIR" -maxdepth 1 -type f -name 'giny_*.sql.gz' \
                          -printf '%T@ %p\0' 2>/dev/null \
                       | sort -zrn \
                       | cut -z -d' ' -f2- \
                       | tr '\0' '\n')

if [ "${#BACKUPS[@]}" -eq 0 ]; then
    err "aucun backup dans $BACKUP_DIR"
    exit 1
fi

# --- choix ------------------------------------------------------------------
choose_backup() {
    echo
    printf "%sBackups disponibles%s (%s) — du plus récent au plus ancien :\n" \
        "$C_BOLD" "$C_RESET" "${#BACKUPS[@]}"
    echo
    local i name size mtime
    for i in "${!BACKUPS[@]}"; do
        name=$(basename "${BACKUPS[$i]}")
        size=$(du -h "${BACKUPS[$i]}" 2>/dev/null | awk '{print $1}')
        mtime=$(date -r "${BACKUPS[$i]}" '+%Y-%m-%d %H:%M:%S')
        printf "  %s%2d)%s %-32s %s%6s%s  %s\n" \
            "$C_CYAN" "$((i+1))" "$C_RESET" "$name" "$C_DIM" "$size" "$C_RESET" "$mtime"
    done
    echo
    while :; do
        ask "Choisis un numéro (1-${#BACKUPS[@]}) ou 'q' pour annuler : "
        read -r reply
        case "$reply" in
            q|Q) info "annulé."; exit 0 ;;
            ''|*[!0-9]*) err "réponse invalide" ;;
            *)
                if [ "$reply" -ge 1 ] && [ "$reply" -le "${#BACKUPS[@]}" ]; then
                    SELECTED="${BACKUPS[$((reply-1))]}"
                    return
                fi
                err "hors limites" ;;
        esac
    done
}

if [ -n "$TARGET" ]; then
    if [ -f "$TARGET" ]; then
        SELECTED="$TARGET"
    elif [ -f "$BACKUP_DIR/$TARGET" ]; then
        SELECTED="$BACKUP_DIR/$TARGET"
    else
        err "fichier introuvable: $TARGET"; exit 1
    fi
elif [ "$LATEST" -eq 1 ]; then
    SELECTED="${BACKUPS[0]}"
else
    choose_backup
fi

info "sélection : $(basename "$SELECTED")"
info "  taille  : $(du -h "$SELECTED" | awk '{print $1}')"
info "  date    : $(date -r "$SELECTED" '+%Y-%m-%d %H:%M:%S')"

# --- confirmation -----------------------------------------------------------
echo
printf "%s⚠  Cette opération va ÉCRASER les bases %s et %s.%s\n" \
    "$C_YELLOW" "$MYSQL_AUTH_DB" "$MYSQL_WORLD_DB" "$C_RESET"
if [ "$ASSUME_YES" -ne 1 ]; then
    ask "Continuer ? [y/N] : "
    read -r confirm
    case "$confirm" in
        y|Y|yes|YES) ;;
        *) info "annulé."; exit 0 ;;
    esac
fi

# --- arrêt world ------------------------------------------------------------
WORLD_WAS_RUNNING=0
if [ "$KEEP_WORLD" -eq 0 ] && docker ps --format '{{.Names}}' | grep -q '^giny-world$'; then
    WORLD_WAS_RUNNING=1
    info "arrêt de giny-world (pour éviter les écritures pendant restore)…"
    docker stop giny-world >/dev/null
fi

# --- restore ----------------------------------------------------------------
info "import en cours…"
if gunzip -c "$SELECTED" \
   | docker exec -i -e MYSQL_PWD="$MYSQL_ROOT_PASSWORD" giny-mysql mysql \
        -u root \
        --default-character-set=utf8mb4; then
    ok "restauration OK"
else
    err "restauration échouée — la DB est peut-être dans un état partiel"
    [ "$WORLD_WAS_RUNNING" -eq 1 ] && docker start giny-world >/dev/null
    exit 1
fi

# --- redémarrage world ------------------------------------------------------
if [ "$WORLD_WAS_RUNNING" -eq 1 ]; then
    info "redémarrage de giny-world…"
    docker start giny-world >/dev/null
    ok "giny-world relancé"
fi

ok "terminé. Backup restauré : $(basename "$SELECTED")"
