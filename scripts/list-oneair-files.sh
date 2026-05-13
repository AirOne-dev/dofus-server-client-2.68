#!/usr/bin/env bash
# =============================================================================
#  Liste les fichiers `OneAir*` du serveur Giny, regroupés par dossier.
#  Donne un aperçu rapide de tout le code custom OneAir greffé sur l'upstream.
#  Usage : ./scripts/list-oneair-files.sh
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BASE="$ROOT/server/giny/Sources"

cd "$ROOT"

# Récupère les chemins relatifs à $BASE, triés, et les groupe par dossier
# parent. Pour chaque groupe : 1 ligne d'en-tête (dossier + count) puis les
# fichiers indentés.
mapfile -t files < <(find "$BASE" -type f -name 'OneAir*.cs' | sed "s|$BASE/||" | sort)

if [ ${#files[@]} -eq 0 ]; then
    echo "Aucun fichier OneAir* trouvé sous $BASE."
    exit 0
fi

current_dir=""
total=0
for f in "${files[@]}"; do
    dir="$(dirname "$f")"
    if [ "$dir" != "$current_dir" ]; then
        # Count files in this dir
        count=$(printf '%s\n' "${files[@]}" | grep -c "^$dir/" || true)
        printf '\n\033[1;36m%s\033[0m (%d)\n' "$dir" "$count"
        current_dir="$dir"
    fi
    printf '  %s\n' "$(basename "$f")"
    total=$((total + 1))
done

printf '\n\033[1;32mTotal : %d fichiers OneAir*\033[0m\n' "$total"
