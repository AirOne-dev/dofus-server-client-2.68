-- OneAir — état havre-sac persistant
-- Cette extension ajoute aux tables giny_world :
--   * oneair_havenbag_state : position pré-havre-sac, thème, room
--   * oneair_known_zaaps    : zaaps connus du joueur (alimenté à chaque OpenZaap)
--   * oneair_havenbag_furnitures : meubles posés (cellId, gid, orientation)
--
-- Idempotent (CREATE TABLE IF NOT EXISTS). Les tables sont aussi auto-créées
-- au boot par OneAirHavenBagPatch.EnsureSchema() comme fallback ; ce fichier
-- garantit qu'elles existent dès le premier démarrage en init-sql.
USE giny_world;

CREATE TABLE IF NOT EXISTS oneair_havenbag_state (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    PreviousMapId BIGINT NULL,
    PreviousCellId SMALLINT NULL,
    Theme TINYINT UNSIGNED NOT NULL DEFAULT 0,
    RoomId TINYINT UNSIGNED NOT NULL DEFAULT 0,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS oneair_known_zaaps (
    CharacterId BIGINT NOT NULL,
    MapId BIGINT NOT NULL,
    DiscoveredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (CharacterId, MapId),
    KEY ix_known_zaaps_char (CharacterId)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS oneair_havenbag_furnitures (
    CharacterId BIGINT NOT NULL,
    CellId SMALLINT NOT NULL,
    FurnitureId INT NOT NULL,
    Orientation TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, CellId)
) ENGINE=InnoDB;
