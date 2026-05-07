package main

import (
	"database/sql"
	"net/http"
	"strconv"
	"sync"
	"time"
)

type communityEvent struct {
	ID           int64     `json:"id"`
	Kind         string    `json:"kind"`
	Title        string    `json:"title"`
	Detail       string    `json:"detail,omitempty"`
	CharacterIDs string    `json:"characterIds,omitempty"`
	Names        string    `json:"names,omitempty"`
	PayloadJSON  string    `json:"payload,omitempty"`
	AtUtc        time.Time `json:"atUtc"`
}

type leaderboardRow struct {
	Rank       int    `json:"rank"`
	ID         int64  `json:"id"`
	Name       string `json:"name"`
	Level      int    `json:"level"`
	Experience int64  `json:"experience"`
	BreedID    int    `json:"breedId"`
	Role       int    `json:"role"`
}

func ensureActivitySchema() error {
	_, err := db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.activity (
		Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
		Kind VARCHAR(32) NOT NULL,
		CharacterIds VARCHAR(255) NOT NULL,
		Names VARCHAR(255) NOT NULL,
		Title VARCHAR(255) NOT NULL,
		Detail VARCHAR(512) NULL,
		PayloadJson TEXT NULL,
		AtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
		KEY ix_at (AtUtc),
		KEY ix_kind (Kind, AtUtc)
	) ENGINE=InnoDB`)
	return err
}

func apiPublicCommunity(w http.ResponseWriter, r *http.Request) {
	_ = ensureActivitySchema()

	q := r.URL.Query()
	limit, _ := strconv.Atoi(q.Get("limit"))
	if limit <= 0 || limit > 100 {
		limit = 25
	}
	lbN, _ := strconv.Atoi(q.Get("leaderboardN"))
	if lbN <= 0 || lbN > 50 {
		lbN = 10
	}

	feed, err := loadCommunityFeed(limit)
	if err != nil {
		httpErr(w, err)
		return
	}
	leaders, err := loadLeaderboard(lbN)
	if err != nil {
		httpErr(w, err)
		return
	}
	stats, _ := loadCommunityStats()

	writeJSON(w, map[string]any{
		"events":      feed,
		"leaderboard": leaders,
		"stats":       stats,
	})
}

func loadCommunityFeed(limit int) ([]communityEvent, error) {
	rows, err := db.Query(`SELECT Id, Kind, Title, COALESCE(Detail,''), CharacterIds, Names, COALESCE(PayloadJson,''), AtUtc
		FROM `+cfg.WorldDB+`.activity
		ORDER BY Id DESC LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []communityEvent
	for rows.Next() {
		var e communityEvent
		if err := rows.Scan(&e.ID, &e.Kind, &e.Title, &e.Detail, &e.CharacterIDs, &e.Names, &e.PayloadJSON, &e.AtUtc); err != nil {
			return nil, err
		}
		out = append(out, e)
	}
	if out == nil {
		out = []communityEvent{}
	}
	return out, nil
}

// loadLeaderboard : top N joueurs par XP. On exclut Role>=5 parce que les
// admins s'auto-promeuvent à du XP arbitraire (et fausseraient le classement).
func loadLeaderboard(n int) ([]leaderboardRow, error) {
	rows, err := db.Query(`SELECT c.Id, c.Name, c.Experience, COALESCE(c.BreedId, 0), COALESCE(a.Role, 0)
		FROM `+cfg.WorldDB+`.characters c
		LEFT JOIN `+cfg.AuthDB+`.accounts a ON a.Id = c.AccountId
		WHERE COALESCE(a.Role, 0) < 5 AND COALESCE(a.Banned, 0) = 0
		ORDER BY c.Experience DESC LIMIT ?`, n)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []leaderboardRow
	rank := 0
	for rows.Next() {
		rank++
		var r leaderboardRow
		if err := rows.Scan(&r.ID, &r.Name, &r.Experience, &r.BreedID, &r.Role); err != nil {
			return nil, err
		}
		r.Rank = rank
		r.Level = experienceToLevel(r.Experience)
		out = append(out, r)
	}
	if out == nil {
		out = []leaderboardRow{}
	}
	return out, nil
}

func loadCommunityStats() (map[string]any, error) {
	stats := map[string]any{}

	if rows, err := db.Query(`SELECT Kind, COUNT(*) FROM ` + cfg.WorldDB + `.activity GROUP BY Kind`); err == nil {
		defer rows.Close()
		byKind := map[string]int{}
		for rows.Next() {
			var k string
			var n int
			if rows.Scan(&k, &n) == nil {
				byKind[k] = n
			}
		}
		stats["byKind"] = byKind
	}

	if rows, err := db.Query(`SELECT Detail, COUNT(*) AS n FROM (
		SELECT Detail FROM ` + cfg.WorldDB + `.activity
		WHERE Kind = 'dungeon_win' ORDER BY Id DESC LIMIT 100
	) t GROUP BY Detail ORDER BY n DESC LIMIT 1`); err == nil {
		defer rows.Close()
		if rows.Next() {
			var d sql.NullString
			var n int
			if rows.Scan(&d, &n) == nil && d.Valid {
				stats["topDungeon"] = map[string]any{"detail": d.String, "count": n}
			}
		}
	}

	return stats, nil
}

// expLevelTable[i] = XP requise pour atteindre le niveau i+2. Chargée une
// fois depuis la table experiences au premier appel.
var (
	expLevelTable []int64
	expLevelOnce  sync.Once
)

func experienceToLevel(xp int64) int {
	expLevelOnce.Do(loadExpLevels)
	if len(expLevelTable) == 0 {
		return 1
	}
	lvl := 1
	for i, threshold := range expLevelTable {
		if xp >= threshold {
			lvl = i + 2
		} else {
			break
		}
	}
	return lvl
}

func loadExpLevels() {
	rows, err := db.Query(`SELECT ExperienceCharacter FROM ` + cfg.WorldDB + `.experiences
		WHERE Level >= 2 ORDER BY Level ASC`)
	if err != nil {
		return
	}
	defer rows.Close()
	for rows.Next() {
		var x sql.NullInt64
		if rows.Scan(&x) == nil && x.Valid {
			expLevelTable = append(expLevelTable, x.Int64)
		}
	}
}
