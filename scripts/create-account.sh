#!/usr/bin/env bash
# =============================================================================
#  Crée un compte joueur OneAir / Giny en SQL direct.
#  Usage : ./scripts/create-account.sh <login> <password> [role]
#  Role  : 1 (Player), 2 (Moderator), 3 (GamemasterPadawan),
#          4 (Gamemaster), 5 (Administrator) — default 1.
#
#  Si le compte existe déjà : met à jour son password et son role.
# =============================================================================
set -euo pipefail

if [ "$#" -lt 2 ]; then
    echo "Usage: $0 <login> <password> [role]"
    echo "       role : 1=Player, 5=Administrator (défaut 1)"
    exit 1
fi

LOGIN="$1"
PASSWORD="$2"
ROLE="${3:-1}"

# Charge .env pour le mot de passe MySQL root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a
: "${MYSQL_ROOT_PASSWORD:=changeme-rootpw}"
: "${MYSQL_AUTH_DB:=giny_auth}"

# Note : Giny stocke les mots de passe en clair dans `accounts.Password`
# (cf. AccountController.cs : pas de hash). On reproduit ce comportement.
docker exec -i giny-mysql mysql -u root -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_AUTH_DB" <<SQL
INSERT INTO accounts (Username, Password, Role, CharactersSlots, Banned)
VALUES ('${LOGIN}', '${PASSWORD}', ${ROLE}, 5, 0)
ON DUPLICATE KEY UPDATE Password='${PASSWORD}', Role=${ROLE};
SQL

echo "Compte « $LOGIN » créé/mis à jour (role=$ROLE)."
