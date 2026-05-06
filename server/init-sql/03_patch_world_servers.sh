#!/bin/bash
# Aligne world_servers.Host sur $SERVER_HOST (lu depuis .env, propagé via
# docker-compose). Fallback 127.0.0.1 si la var est absente. Le port 5556
# correspond au conteneur world (le dump initial pointait vers 5555 = auth).
#
# Ce script remplace l'ancien 03_patch_world_servers.sql, qui hardcodait
# 127.0.0.1 et obligeait à corriger la DB après chaque `up -v`.
set -e

HOST="${SERVER_HOST:-127.0.0.1}"

mysql --protocol=socket -uroot -p"$MYSQL_ROOT_PASSWORD" giny_auth <<SQL
UPDATE world_servers
SET Host = '${HOST}',
    Port = 5556
WHERE Id = 291;

DELETE FROM world_servers WHERE Id <> 291;

INSERT IGNORE INTO accounts
    (Id, Username, Password, LastSelectedServerId, IPs,
     CharactersSlots, Banned, Role, Nickname, Ogrines)
VALUES
    (1, 'admin', 'test', 291, '${HOST}', 15, 0, 5, 'Street', 600);
SQL
