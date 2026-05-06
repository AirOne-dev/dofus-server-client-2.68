#!/bin/sh
# OneAir DB backup loop.
# Tourne dans un conteneur Alpine à côté de la stack Giny.
# Toutes les BACKUP_INTERVAL_SECONDS, dump giny_auth + giny_world en .sql.gz
# dans /backups, puis garde les BACKUP_KEEP_COUNT plus récents.
set -eu

: "${MYSQL_HOST:=mysql}"
: "${MYSQL_USER:=giny}"
: "${MYSQL_PASSWORD:=giny}"
: "${MYSQL_AUTH_DB:=giny_auth}"
: "${MYSQL_WORLD_DB:=giny_world}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_INTERVAL_SECONDS:=300}"
: "${BACKUP_KEEP_COUNT:=20}"

mkdir -p "$BACKUP_DIR"

echo "[backup] starting (interval=${BACKUP_INTERVAL_SECONDS}s, keep=${BACKUP_KEEP_COUNT})"

# Patiente jusqu'à ce que MySQL réponde au tout premier tour.
i=0
until mysqladmin -h"$MYSQL_HOST" -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" ping >/dev/null 2>&1; do
    i=$((i+1))
    [ $i -gt 60 ] && { echo "[backup] mysql unreachable, abort"; exit 1; }
    sleep 2
done

while true; do
    ts=$(date -u +%Y%m%dT%H%M%SZ)
    out="$BACKUP_DIR/giny_${ts}.sql.gz"
    tmp="${out}.part"

    echo "[backup] dumping → $out"
    if mysqldump \
            -h"$MYSQL_HOST" -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" \
            --single-transaction --quick --routines --triggers \
            --databases "$MYSQL_AUTH_DB" "$MYSQL_WORLD_DB" \
            2>"$BACKUP_DIR/.last-error" \
        | gzip -c > "$tmp" \
        && mv "$tmp" "$out"; then
        echo "[backup] OK $(du -h "$out" | awk '{print $1}')"
    else
        echo "[backup] FAILED, see $BACKUP_DIR/.last-error" >&2
        rm -f "$tmp"
    fi

    # Rotation : on garde les N plus récents (par mtime).
    count=$(ls -1t "$BACKUP_DIR"/giny_*.sql.gz 2>/dev/null | wc -l)
    if [ "$count" -gt "$BACKUP_KEEP_COUNT" ]; then
        ls -1t "$BACKUP_DIR"/giny_*.sql.gz \
            | tail -n +$((BACKUP_KEEP_COUNT + 1)) \
            | xargs -r rm -f
        echo "[backup] rotated, keeping $BACKUP_KEEP_COUNT most recent"
    fi

    sleep "$BACKUP_INTERVAL_SECONDS"
done
