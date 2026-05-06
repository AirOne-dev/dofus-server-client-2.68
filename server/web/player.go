// Player-side authentication and APIs for the public landing page.
// Réutilise la table accounts (mots de passe en clair, comme côté admin).
// Cookie distinct de l'admin (oneair_player) et signature préfixée pour
// éviter qu'un cookie de l'un soit accepté par l'autre.
package main

import (
	"crypto/subtle"
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

const (
	playerCookieName    = "oneair_player"
	playerSessionMaxAge = 7 * 24 * time.Hour
)

type playerSession struct {
	AccountID int
	Username  string
}

func makePlayerSession(accountID int, username string) string {
	exp := time.Now().Add(playerSessionMaxAge).Unix()
	body := fmt.Sprintf("p|%d|%s|%d", accountID, username, exp)
	return body + "|" + sign(body)
}

func verifyPlayerSession(c string) (*playerSession, bool) {
	parts := strings.SplitN(c, "|", 5)
	if len(parts) != 5 || parts[0] != "p" {
		return nil, false
	}
	body := strings.Join(parts[:4], "|")
	if subtle.ConstantTimeCompare([]byte(parts[4]), []byte(sign(body))) != 1 {
		return nil, false
	}
	exp, err := strconv.ParseInt(parts[3], 10, 64)
	if err != nil || time.Now().Unix() > exp {
		return nil, false
	}
	id, err := strconv.Atoi(parts[1])
	if err != nil {
		return nil, false
	}
	return &playerSession{AccountID: id, Username: parts[2]}, true
}

func currentPlayer(r *http.Request) *playerSession {
	c, err := r.Cookie(playerCookieName)
	if err != nil {
		return nil
	}
	s, ok := verifyPlayerSession(c.Value)
	if !ok {
		return nil
	}
	return s
}

func setPlayerCookie(w http.ResponseWriter, accountID int, username string) {
	http.SetCookie(w, &http.Cookie{
		Name:     playerCookieName,
		Value:    makePlayerSession(accountID, username),
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		MaxAge:   int(playerSessionMaxAge.Seconds()),
	})
}

func clearPlayerCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:   playerCookieName,
		Value:  "",
		Path:   "/",
		MaxAge: -1,
	})
}

// POST /api/public/login — body { username, password }
func apiPublicLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "method", 405)
		return
	}
	var req struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "bad json", 400)
		return
	}
	req.Username = strings.TrimSpace(req.Username)
	if req.Username == "" || req.Password == "" {
		http.Error(w, "identifiants requis", 400)
		return
	}
	var id int
	var pw, nick string
	var banned int
	err := db.QueryRow(`SELECT Id, COALESCE(Password,''), COALESCE(Nickname,''), COALESCE(Banned,0)
		FROM `+cfg.AuthDB+`.accounts WHERE Username = ?`, req.Username).
		Scan(&id, &pw, &nick, &banned)
	if err == sql.ErrNoRows {
		time.Sleep(700 * time.Millisecond)
		http.Error(w, "identifiants invalides", 401)
		return
	}
	if err != nil {
		httpErr(w, err)
		return
	}
	if banned != 0 {
		http.Error(w, "compte banni", 403)
		return
	}
	if subtle.ConstantTimeCompare([]byte(pw), []byte(req.Password)) != 1 {
		time.Sleep(700 * time.Millisecond)
		http.Error(w, "identifiants invalides", 401)
		return
	}
	setPlayerCookie(w, id, req.Username)
	writeJSON(w, map[string]any{"ok": true, "username": req.Username, "nickname": nick})
}

// POST /api/public/logout
func apiPublicLogout(w http.ResponseWriter, r *http.Request) {
	clearPlayerCookie(w)
	writeJSON(w, map[string]string{"ok": "1"})
}

// GET /api/public/me — { loggedIn, username, nickname, characters[] }
func apiPublicMe(w http.ResponseWriter, r *http.Request) {
	s := currentPlayer(r)
	if s == nil {
		writeJSON(w, map[string]any{"loggedIn": false})
		return
	}
	var nick string
	var role, banned int
	_ = db.QueryRow(`SELECT COALESCE(Nickname,''), COALESCE(Role,1), COALESCE(Banned,0)
		FROM `+cfg.AuthDB+`.accounts WHERE Id = ?`, s.AccountID).Scan(&nick, &role, &banned)
	if banned != 0 {
		clearPlayerCookie(w)
		writeJSON(w, map[string]any{"loggedIn": false, "banned": true})
		return
	}
	chars := loadPlayerCharacters(s.AccountID)
	writeJSON(w, map[string]any{
		"loggedIn":   true,
		"username":   s.Username,
		"nickname":   nick,
		"isAdmin":    role >= 5,
		"characters": chars,
	})
}

type publicCharacter struct {
	ID         int64  `json:"id"`
	Name       string `json:"name"`
	BreedID    int    `json:"breedId"`
	BreedName  string `json:"breedName"`
	Sex        string `json:"sex"`
	Level      int    `json:"level"`
	Experience int64  `json:"experience"`
	Kamas      int64  `json:"kamas"`
	MapID      int64  `json:"mapId"`
	Zone       string `json:"zone"`
	Online     bool   `json:"online"`
}

func loadPlayerCharacters(accountID int) []publicCharacter {
	rows, err := db.Query(`SELECT c.Id, COALESCE(c.Name,''), COALESCE(c.BreedId,0), COALESCE(c.Sex,''),
			COALESCE(c.Experience,0), COALESCE(c.Kamas,0), COALESCE(c.MapId,0), COALESCE(s.Name,'')
		FROM `+cfg.WorldDB+`.characters c
		LEFT JOIN `+cfg.WorldDB+`.maps m ON m.Id = c.MapId
		LEFT JOIN `+cfg.WorldDB+`.subareas s ON s.Id = m.SubareaId
		WHERE c.AccountId = ?
		ORDER BY c.Id ASC`, accountID)
	if err != nil {
		return nil
	}
	defer rows.Close()

	online := map[int64]int{}
	if r2, err := db.Query("SELECT CharacterId, COALESCE(Level,0) FROM " + cfg.WorldDB + ".oneair_online_clients"); err == nil {
		defer r2.Close()
		for r2.Next() {
			var id int64
			var lvl int
			if err := r2.Scan(&id, &lvl); err == nil {
				online[id] = lvl
			}
		}
	}
	var out []publicCharacter
	for rows.Next() {
		var c publicCharacter
		if err := rows.Scan(&c.ID, &c.Name, &c.BreedID, &c.Sex, &c.Experience, &c.Kamas,
			&c.MapID, &c.Zone); err != nil {
			continue
		}
		c.BreedName = breedNameByID(c.BreedID)
		if lvl, ok := online[c.ID]; ok {
			c.Level = lvl
			c.Online = true
		} else {
			c.Level = levelFromXP(c.Experience)
		}
		out = append(out, c)
	}
	return out
}

func breedNameByID(id int) string {
	names := map[int]string{
		1: "Feca", 2: "Osamodas", 3: "Enutrof", 4: "Sram", 5: "Xélor", 6: "Écaflip",
		7: "Eniripsa", 8: "Iop", 9: "Crâ", 10: "Sadida", 11: "Sacrieur", 12: "Pandawa",
		13: "Roublard", 14: "Zobal", 15: "Steamer", 16: "Eliotrope", 17: "Huppermage", 18: "Ouginak",
	}
	if n, ok := names[id]; ok {
		return n
	}
	return "Inconnu"
}

// Table cumulative XP des niveaux 1..200 (Dofus 2.x). Approximative au-delà
// du 100 mais cohérente avec ce qui est utilisé en jeu pour afficher un niveau.
var xpByLevel = [...]int64{
	0,
	0, 110, 330, 660, 1100, 1683, 2447, 3431, 4683, 6256,
	8211, 10615, 13546, 17087, 21330, 26375, 32330, 39315, 47457, 56895,
	67779, 80272, 94548, 110795, 129214, 150018, 173436, 199712, 229105, 261889,
	298355, 338807, 383568, 432978, 487393, 547185, 612745, 684479, 762812, 848188,
	941068, 1041930, 1151271, 1269604, 1397458, 1535380, 1683933, 1843698, 2015270, 2199263,
	2396305, 2607044, 2832145, 3072289, 3328174, 3600518, 3890054, 4197531, 4523715, 4869387,
	5235341, 5622386, 6031344, 6463050, 6918348, 7398091, 7903140, 8434360, 8992623, 9578797,
	10193753, 10838355, 11513459, 12219909, 12958528, 13730116, 14535443, 15375240, 16250190, 17160913,
	18107952, 19091749, 20112605, 21170629, 22265655, 23397137, 24564038, 25764610, 26996182, 28254919,
	29536602, 30835394, 32144624, 33455588, 34758378, 36041703, 37292746, 38497075, 39639609, 40703616,
	41671749, 43217733, 44855137, 46591369, 48434201, 50391794, 52472722, 54686001, 57041117, 59548058,
	62217340, 65060035, 68087811, 71312967, 74748456, 78407931, 82305773, 86457125, 90877941, 95585013,
	100596013, 105929518, 111605059, 117643148, 124065322, 130894181, 138153425, 145867887, 154063566, 162767668,
	172008632, 181816175, 192221332, 203256490, 214955434, 227353385, 240487038, 254394610, 269115881, 284692236,
	301166713, 318584054, 336990747, 356435079, 376967185, 398639090, 421504769, 445620192, 471043373, 497834425,
	526055613, 555771409, 587048547, 619956085, 654565461, 690950555, 729187752, 769356009, 811536926, 855814819,
	902276792, 951012815, 1002115802, 1055681695, 1111809553, 1170601638, 1232163510, 1296604119, 1364035905, 1434574897,
	1508340814, 1585457167, 1666051366, 1750254832, 1838203111, 1930035994, 2025897640, 2125936705, 2230306474, 2339164997,
	2452675229, 2571005172, 2694328029, 2822822351, 2956672193, 3096067273, 3241203132, 3392281307, 3549509496, 3713101732,
	3883278564, 4060267243, 4244301913, 4435623804, 4634481430, 4841130796, 5055835612, 5278867512, 5510506270, 5751040030,
}

func levelFromXP(xp int64) int {
	lo, hi := 1, len(xpByLevel)-1
	for lo < hi {
		mid := (lo + hi + 1) / 2
		if xpByLevel[mid] <= xp {
			lo = mid
		} else {
			hi = mid - 1
		}
	}
	return lo
}

// xpProgress retourne (xpDansNiveau, xpRequisNiveauSuivant, ratio[0..1]).
func xpProgress(xp int64, lvl int) (int64, int64, float64) {
	if lvl >= len(xpByLevel)-1 {
		return 0, 0, 1
	}
	cur := xpByLevel[lvl]
	next := xpByLevel[lvl+1]
	if next <= cur {
		return 0, 0, 1
	}
	delta := xp - cur
	span := next - cur
	if delta < 0 {
		delta = 0
	}
	return delta, span, float64(delta) / float64(span)
}

// GET /api/public/character?id=N — détails d'un personnage du joueur connecté.
func apiPublicCharacter(w http.ResponseWriter, r *http.Request) {
	s := currentPlayer(r)
	if s == nil {
		http.Error(w, "unauthorized", 401)
		return
	}
	cid, err := strconv.ParseInt(r.URL.Query().Get("id"), 10, 64)
	if err != nil {
		http.Error(w, "id requis", 400)
		return
	}
	var ownerAcct int
	err = db.QueryRow("SELECT COALESCE(AccountId,0) FROM "+cfg.WorldDB+".characters WHERE Id = ?", cid).Scan(&ownerAcct)
	if err != nil || ownerAcct != s.AccountID {
		http.Error(w, "personnage introuvable", 404)
		return
	}
	var name, sex, zone string
	var experience, kamas, mapID int64
	var breedID int
	err = db.QueryRow(`SELECT COALESCE(c.Name,''), COALESCE(c.BreedId,0), COALESCE(c.Sex,''),
			COALESCE(c.Experience,0), COALESCE(c.Kamas,0), COALESCE(c.MapId,0), COALESCE(s.Name,'')
		FROM `+cfg.WorldDB+`.characters c
		LEFT JOIN `+cfg.WorldDB+`.maps m ON m.Id = c.MapId
		LEFT JOIN `+cfg.WorldDB+`.subareas s ON s.Id = m.SubareaId
		WHERE c.Id = ?`, cid).Scan(&name, &breedID, &sex, &experience, &kamas, &mapID, &zone)
	if err != nil {
		httpErr(w, err)
		return
	}
	var lvl int
	online := false
	if err := db.QueryRow("SELECT COALESCE(Level,0) FROM "+cfg.WorldDB+".oneair_online_clients WHERE CharacterId = ?", cid).Scan(&lvl); err == nil && lvl > 0 {
		online = true
	} else {
		lvl = levelFromXP(experience)
	}
	xpInLvl, xpForNext, ratio := xpProgress(experience, lvl)
	var x, y int
	_ = db.QueryRow("SELECT COALESCE(X,0), COALESCE(Y,0) FROM "+cfg.WorldDB+".map_positions WHERE Id = ?", mapID).Scan(&x, &y)
	pos := fmt.Sprintf("[%d, %d]", x, y)
	if zone != "" {
		pos = zone + " " + pos
	}
	var items int
	_ = db.QueryRow("SELECT COUNT(*) FROM "+cfg.WorldDB+".character_items WHERE CharacterId = ?", cid).Scan(&items)
	writeJSON(w, map[string]any{
		"id":         cid,
		"name":       name,
		"breedId":    breedID,
		"breedName":  breedNameByID(breedID),
		"sex":        sex,
		"level":      lvl,
		"experience": experience,
		"xpInLevel":  xpInLvl,
		"xpForNext":  xpForNext,
		"xpRatio":    ratio,
		"kamas":      kamas,
		"mapId":      mapID,
		"position":   pos,
		"zone":       zone,
		"online":     online,
		"itemCount":  items,
	})
}
