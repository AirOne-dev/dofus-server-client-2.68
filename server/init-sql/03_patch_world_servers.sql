-- Aligne la table world_servers avec le port effectif du conteneur world (5556)
-- Le dump initial pointait vers 5555 (= port d'auth dans notre stack).
USE giny_auth;

UPDATE world_servers
SET Host = '127.0.0.1',
    Port = 5556
WHERE Id = 291;

-- Supprime le serveur "Ombre" qui n'est pas lancé chez nous
DELETE FROM world_servers WHERE Id <> 291;

-- Compte de test minimal (admin / test) — déjà dans le dump mais on s'assure
INSERT IGNORE INTO accounts
    (Id, Username, Password, LastSelectedServerId, IPs,
     CharactersSlots, Banned, Role, Nickname, Ogrines)
VALUES
    (1, 'admin', 'test', 291, '127.0.0.1', 15, 0, 5, 'Street', 600);
