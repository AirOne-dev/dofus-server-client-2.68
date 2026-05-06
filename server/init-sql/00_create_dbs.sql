-- Crée les deux bases attendues par Giny + grants utilisateur
CREATE DATABASE IF NOT EXISTS `giny_auth`  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS `giny_world` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

GRANT ALL PRIVILEGES ON `giny_auth`.*  TO 'giny'@'%';
GRANT ALL PRIVILEGES ON `giny_world`.* TO 'giny'@'%';
FLUSH PRIVILEGES;
