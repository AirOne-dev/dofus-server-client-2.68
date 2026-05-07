-- OneAir — état havre-sac persistant
-- Cette extension ajoute aux tables giny_world :
--   * havenbag_state        : position pré-havre-sac, thème, room, dernière loterie
--   * known_zaaps           : zaaps connus du joueur (alimenté à chaque OpenZaap)
--   * havenbag_furnitures   : meubles posés (cellId, gid, orientation, thème)
--   * havenbag_interactives : binding BonesId → Type (zaap/chest/lotery/exit)
--
-- Idempotent (CREATE TABLE IF NOT EXISTS). Doit rester aligné avec
-- OneAirHavenBagPatch.EnsureSchema() — le schéma runtime est la source de vérité.
USE giny_world;

CREATE TABLE IF NOT EXISTS havenbag_state (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    PreviousMapId BIGINT NULL,
    PreviousCellId SMALLINT NULL,
    Theme TINYINT UNSIGNED NOT NULL DEFAULT 0,
    RoomId TINYINT UNSIGNED NOT NULL DEFAULT 0,
    LastLoteryAt DATETIME NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS known_zaaps (
    CharacterId BIGINT NOT NULL,
    MapId BIGINT NOT NULL,
    DiscoveredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (CharacterId, MapId),
    KEY ix_known_zaaps_char (CharacterId)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS havenbag_furnitures (
    CharacterId BIGINT NOT NULL,
    ThemeId TINYINT UNSIGNED NOT NULL DEFAULT 1,
    CellId SMALLINT NOT NULL,
    FurnitureId INT NOT NULL,
    Orientation TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, ThemeId, CellId)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS havenbag_interactives (
    BonesId INT NOT NULL PRIMARY KEY,
    Type VARCHAR(16) NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;
