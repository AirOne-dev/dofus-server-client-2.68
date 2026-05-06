// mappings.go — règles de mapping d2o → table SQL Giny.
//
// Pour ajouter un mapping :
//   - clé = nom du fichier d2o (ex: "Areas.d2o")
//   - Table = nom de la table SQL cible
//   - Columns = ordre des colonnes du INSERT
//   - Field = transformation : nom du champ dans le record d2o, ou expression
//
// Les types complexes (vecteurs, sous-objets) sont sérialisés en JSON dans
// la colonne SQL (utile si la colonne est BLOB / mediumtext côté Giny).
//
// Si une expression renvoie nil ou si le record est mal-formé, on émet NULL.
package main

import "strings"

type ColMap struct {
	Col   string
	Field string // soit nom de champ d2o, soit "$id" pour l'id de l'index, soit "$json:<field>"
}

type TableMap struct {
	Table   string
	Columns []ColMap
	// Si I18NField != "", on utilise i18n[record[I18NField]] pour résoudre Name.
	I18NField string
	// Si TruncateBefore = true, on émet TRUNCATE TABLE avant les INSERTs.
	TruncateBefore bool
}

// MAPPINGS : par défaut on ne couvre que les tables simples / sûres.
// Les tables complexes (Items, Monsters, Spells…) ont des colonnes BLOB
// proto-encodées par Giny côté C# — on ne peut pas les régénérer fidèlement
// sans lancer un import via le framework Giny lui-même.
var MAPPINGS = map[string]TableMap{
	"Areas.d2o": {
		Table: "areas",
		Columns: []ColMap{
			{"Id", "$id"},
			{"Name", "$i18n:nameId"},
			{"SuperAreaId", "superAreaId"},
			{"WorldMapId", "worldmapId"},
		},
	},

	"SuperAreas.d2o": {
		Table: "superareas",
		Columns: []ColMap{
			{"Id", "$id"},
			{"Name", "$i18n:nameId"},
		},
	},

	"SubAreas.d2o": {
		Table: "subareas",
		Columns: []ColMap{
			{"Id", "$id"},
			{"Name", "$i18n:nameId"},
			{"AreaId", "areaId"},
			{"Level", "$str:level"},
			{"MonsterIds", "$jsonblob:monsters"},
			{"QuestIds", "$jsonblob:questsIds"},
			{"NpcIds", "$jsonblob:npcIds"},
			{"AssociatedZaapMapId", "associatedZaapMapId"},
			{"AchievementId", "achievementID"},
		},
	},

	"MapPositions.d2o": {
		Table: "map_positions",
		Columns: []ColMap{
			{"Id", "$id"},
			{"X", "posX"},
			{"Y", "posY"},
			{"Outdoor", "$str:outdoor"},
			{"Capabilities", "capabilities"},
			{"Name", "$i18n:nameId"},
		},
	},

	"WorldMaps.d2o": {
		Table: "worldmaps",
		Columns: []ColMap{
			{"Id", "$id"},
			{"Name", "$i18n:nameId"},
			{"OrigineX", "origineX"},
			{"OrigineY", "origineY"},
			{"MapWidth", "mapWidth"},
			{"MapHeight", "mapHeight"},
			{"StartX", "startX"},
			{"StartY", "startY"},
			{"EndX", "endX"},
			{"EndY", "endY"},
		},
	},

	"Waypoints.d2o": {
		Table: "waypoints",
		Columns: []ColMap{
			{"Id", "$id"},
			{"MapId", "mapId"},
			{"SubAreaId", "subAreaId"},
		},
	},

	"Titles.d2o": {
		Table: "titles",
		Columns: []ColMap{
			{"Id", "$id"},
			{"NameMaleId", "nameMaleId"},
			{"NameFemaleId", "nameFemaleId"},
			{"NameMale", "$i18n:nameMaleId"},
			{"NameFemale", "$i18n:nameFemaleId"},
			{"CategoryId", "categoryId"},
			{"Visible", "$str:visible"},
		},
	},

	"Ornaments.d2o": {
		Table: "ornaments",
		Columns: []ColMap{
			{"Id", "$id"},
			{"NameId", "nameId"},
			{"Name", "$i18n:nameId"},
			{"AssetId", "assetId"},
			{"Visible", "$str:visible"},
		},
	},

	"AlignmentRanks.d2o": {
		Table: "alignment_ranks",
		Columns: []ColMap{
			{"Id", "$id"},
			{"NameMaleId", "nameMaleId"},
			{"NameFemaleId", "nameFemaleId"},
			{"NameMale", "$i18n:nameMaleId"},
			{"NameFemale", "$i18n:nameFemaleId"},
			{"OrderId", "orderId"},
			{"Honor", "honor"},
		},
	},

	"Emoticons.d2o": {
		Table: "emotes",
		Columns: []ColMap{
			{"Id", "$id"},
			{"NameId", "nameId"},
			{"Name", "$i18n:nameId"},
			{"Persistancy", "$str:persistancy"},
			{"Aura", "$str:aura"},
			{"Order", "$str:order"},
		},
	},

	"Hints.d2o": {
		Table: "hints",
		Columns: []ColMap{
			{"Id", "$id"},
			{"MapId", "mapId"},
			{"NameId", "nameId"},
			{"Name", "$i18n:nameId"},
			{"GfxId", "gfxId"},
			{"CategoryId", "categoryId"},
		},
	},
}

// Renvoie la liste des paires (filename, mapping) dans un ordre stable
// (par nom de fichier) pour rendre les sorties reproductibles.
func sortedMappings() []struct {
	File    string
	Mapping TableMap
} {
	files := make([]string, 0, len(MAPPINGS))
	for k := range MAPPINGS {
		files = append(files, k)
	}
	// tri simple sans importer "sort" ici (déjà dispo, mais on garde simple)
	for i := 0; i < len(files); i++ {
		for j := i + 1; j < len(files); j++ {
			if strings.Compare(files[j], files[i]) < 0 {
				files[i], files[j] = files[j], files[i]
			}
		}
	}
	out := make([]struct {
		File    string
		Mapping TableMap
	}, 0, len(files))
	for _, f := range files {
		out = append(out, struct {
			File    string
			Mapping TableMap
		}{f, MAPPINGS[f]})
	}
	return out
}
