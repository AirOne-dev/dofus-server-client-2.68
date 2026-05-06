// OneAir web — interface unifiée du serveur OneAir : landing publique,
// articles, login joueur, panel admin, reverse-proxy dbgate.
// (Anciennement nommé "admin" — renommé après l'ajout de la landing publique.)
//
// Authentification HMAC cookie + mots de passe via env. Lit/écrit
// directement les bases giny_auth et giny_world. Mutations qui ont besoin
// d'un effet "live" (broadcast, kick) écrivent dans une table
// oneair_actions polled par le world (ajoutée à part).
package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"database/sql"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	_ "github.com/go-sql-driver/mysql"
)

//go:embed templates/*.html static/*
var assets embed.FS

var (
	cfg     Config
	db      *sql.DB
	tpl     *template.Template
	startAt = time.Now()
)

type Config struct {
	Listen        string
	SessionKey    []byte
	DSN           string // root user, can hit both DBs
	AuthDB        string
	WorldDB       string
	AuthHost      string
	WorldHost     string
	AuthPort      string
	WorldPort     string
	BackupDir     string
	BackupTrigg   string // chemin d'un fichier-flag à toucher pour réveiller le backup
	ItemsD2PDir   string
	ItemsCacheDir string
}

func main() {
	cfg = Config{
		Listen:      envOr("WEB_LISTEN", ":3000"),
		AuthDB:      envOr("MYSQL_AUTH_DB", "giny_auth"),
		WorldDB:     envOr("MYSQL_WORLD_DB", "giny_world"),
		AuthHost:    envOr("AUTH_HOST", "auth"),
		WorldHost:   envOr("WORLD_HOST", "world"),
		AuthPort:    envOr("AUTH_INTERNAL_PORT", "5555"),
		WorldPort:   envOr("WORLD_INTERNAL_PORT", "5556"),
		BackupDir:     envOr("BACKUP_DIR", "/backups"),
		BackupTrigg:   envOr("BACKUP_TRIGGER_PATH", "/backups/.trigger"),
		ItemsD2PDir:   envOr("ITEMS_D2P_DIR", "/app/OneAir.app/Contents/Resources/content/gfx/items"),
		ItemsCacheDir: envOr("ITEMS_CACHE_DIR", "/items-cache"),
	}

	// Background : extrait les icônes d'items depuis les .d2p.
	go extractItemAssets(cfg.ItemsD2PDir, cfg.ItemsCacheDir)

	skHex := mustEnv("WEB_SESSION_KEY")
	sk, err := hex.DecodeString(skHex)
	if err != nil || len(sk) < 16 {
		log.Fatalf("WEB_SESSION_KEY doit être hex (>=32 chars)")
	}
	cfg.SessionKey = sk

	cfg.DSN = fmt.Sprintf("%s:%s@tcp(%s:3306)/?parseTime=true&multiStatements=true&interpolateParams=true",
		envOr("DB_USER", "root"),
		mustEnv("MYSQL_ROOT_PASSWORD"),
		envOr("MYSQL_HOST", "mysql"))

	db, err = sql.Open("mysql", cfg.DSN)
	if err != nil {
		log.Fatal(err)
	}
	db.SetMaxOpenConns(8)

	tpl = template.Must(template.New("").Funcs(template.FuncMap{
		"initials": func(s string) string {
			if len(s) == 0 {
				return "?"
			}
			return strings.ToUpper(string(s[0]))
		},
		"inc": func(i int) int { return i + 1 },
	}).ParseFS(assets, "templates/*.html"))

	// Création initiale du schéma articles (au moins la table) pour que la
	// landing publique fonctionne dès le premier accès.
	_ = ensureArticlesSchema()

	mux := http.NewServeMux()

	// ---- Public : landing + articles + login joueur + download ------------
	mux.HandleFunc("/", handleLanding)
	mux.HandleFunc("/article/", handleArticle)
	mux.HandleFunc("/download/macos", handleDownloadMac)
	mux.HandleFunc("/download/windows", handleDownloadWindows)
	mux.HandleFunc("/api/public/status", apiPublicStatus)
	mux.HandleFunc("/api/public/login", apiPublicLogin)
	mux.HandleFunc("/api/public/logout", apiPublicLogout)
	mux.HandleFunc("/api/public/me", apiPublicMe)
	mux.HandleFunc("/api/public/character", apiPublicCharacter)
	mux.HandleFunc("/api/public/articles", handleArticleAPI)
	mux.HandleFunc("/api/public/articles/", handleArticleAPI)
	mux.HandleFunc("/api/public/community", apiPublicCommunity)

	// ---- Admin : dashboard / API (login se fait via /api/public/login) -----
	// Les anciens /login et /logout admin sont supprimés ; on redirige les
	// éventuels bookmarks pour ne pas casser un onglet ouvert.
	mux.HandleFunc("/login", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/#compte", http.StatusSeeOther)
	})
	mux.HandleFunc("/logout", func(w http.ResponseWriter, r *http.Request) {
		clearPlayerCookie(w)
		http.Redirect(w, r, "/", http.StatusSeeOther)
	})
	mux.Handle("/static/", http.FileServer(http.FS(assets)))

	auth := authMiddleware
	mux.Handle("/items/", auth(http.StripPrefix("/items/", itemAssetHandler())))
	mux.Handle("/admin", auth(http.HandlerFunc(handleDashboard)))
	mux.Handle("/admin/", auth(http.HandlerFunc(handleDashboard)))
	mux.Handle("/api/articles", auth(http.HandlerFunc(apiAdminArticles)))
	mux.Handle("/api/status", auth(http.HandlerFunc(apiStatus)))
	mux.Handle("/api/accounts", auth(http.HandlerFunc(apiAccounts)))
	mux.Handle("/api/characters", auth(http.HandlerFunc(apiCharacters)))
	mux.Handle("/api/inventory", auth(http.HandlerFunc(apiInventory)))
	mux.Handle("/api/backups", auth(http.HandlerFunc(apiBackups)))
	mux.Handle("/api/broadcast", auth(http.HandlerFunc(apiBroadcast)))
	mux.Handle("/api/kick", auth(http.HandlerFunc(apiKick)))
	mux.Handle("/api/action", auth(http.HandlerFunc(apiAction)))
	mux.Handle("/api/actions", auth(http.HandlerFunc(apiActionsList)))
	mux.Handle("/api/inventory/parsed", auth(http.HandlerFunc(apiInventoryParsed)))
	mux.Handle("/api/items/catalog", auth(http.HandlerFunc(apiItemsCatalog)))
	mux.Handle("/api/spells/catalog", auth(http.HandlerFunc(apiSpellsCatalog)))
	mux.Handle("/api/spells/parsed", auth(http.HandlerFunc(apiSpellsParsed)))
	mux.Handle("/api/players/online", auth(http.HandlerFunc(apiPlayersOnline)))
	mux.Handle("/api/events", auth(http.HandlerFunc(apiEvents)))
	mux.Handle("/api/unhandled", auth(http.HandlerFunc(apiUnhandled)))
	// Assets visuels publics : la landing référence /classes/bg_X.jpg,
	// /breeds/X.png et /heads/X.png. Pas de données sensibles, on les
	// expose sans auth (sinon les images cassent en non-loggé).
	mux.Handle("/classes/", http.StripPrefix("/classes/", classAssetHandler()))
	mux.Handle("/spells/", http.StripPrefix("/spells/", spellAssetHandler()))
	mux.Handle("/heads/", http.StripPrefix("/heads/", headAssetHandler()))
	mux.Handle("/breeds/", http.StripPrefix("/breeds/", breedAssetHandler()))
	mux.Handle("/worldmap/", auth(http.StripPrefix("/worldmap/", worldmapAssetHandler())))

	// dbgate reverse-proxy (gated by admin auth)
	if dbgateURL := envOr("DBGATE_URL", ""); dbgateURL != "" {
		u, err := url.Parse(dbgateURL)
		if err == nil {
			proxy := httputil.NewSingleHostReverseProxy(u)
			origDirector := proxy.Director
			proxy.Director = func(r *http.Request) {
				origDirector(r)
				r.Host = u.Host
			}
			mux.Handle("/dbgate/", auth(proxy))
			mux.Handle("/dbgate", auth(http.RedirectHandler("/dbgate/", http.StatusMovedPermanently)))
			log.Printf("dbgate proxied at /dbgate/ → %s", dbgateURL)
		} else {
			log.Printf("DBGATE_URL invalid: %v", err)
		}
	}

	log.Printf("web listening on %s", cfg.Listen)
	log.Fatal(http.ListenAndServe(cfg.Listen, mux))
}

// ---------- helpers env -----------------------------------------------------

func envOr(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}

func mustEnv(k string) string {
	v := os.Getenv(k)
	if v == "" {
		log.Fatalf("env %s manquante", k)
	}
	return v
}

// ---------- auth (unifié — un seul cookie player) ---------------------------
//
// Plus de double login. Un seul cookie `oneair_player` (cf. player.go) est
// utilisé partout :
//   - landing (/, /api/public/*) : pas d'auth requise
//   - admin (/admin, /api/<admin>) : auth requise + Role >= 5
//
// La fonction sign() reste exportée pour player.go (HMAC partagé).

func sign(s string) string {
	h := hmac.New(sha256.New, cfg.SessionKey)
	h.Write([]byte(s))
	return hex.EncodeToString(h.Sum(nil))
}

// authMiddleware : autorise uniquement les sessions player liées à un
// compte de rôle >= 5. Sinon redirige vers la landing avec ancre #compte
// (où se trouve le formulaire de login unique).
func authMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		s := currentPlayer(r)
		if s == nil {
			redirectLogin(w, r)
			return
		}
		var role int
		_ = db.QueryRow("SELECT COALESCE(Role,1) FROM "+cfg.AuthDB+".accounts WHERE Id = ?", s.AccountID).Scan(&role)
		if role < 5 {
			if strings.HasPrefix(r.URL.Path, "/api/") {
				http.Error(w, "forbidden", http.StatusForbidden)
				return
			}
			renderForbidden(w, s.Username)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func redirectLogin(w http.ResponseWriter, r *http.Request) {
	if strings.HasPrefix(r.URL.Path, "/api/") {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	http.Redirect(w, r, "/#compte", http.StatusSeeOther)
}

// renderForbidden : page minimaliste pour les comptes connectés mais non-admin
// qui tentent d'accéder à /admin. Renvoie 403 + lien retour.
func renderForbidden(w http.ResponseWriter, username string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusForbidden)
	_, _ = fmt.Fprintf(w, `<!doctype html><html lang="fr"><meta charset="utf-8"><title>Accès refusé · OneAir</title>
<link rel="stylesheet" href="/static/landing.css"><body style="background:#050a13;color:#eaf2f9;display:grid;place-items:center;min-height:100vh;text-align:center;padding:32px;">
<div style="max-width:480px;background:linear-gradient(180deg,rgba(11,27,45,.85),rgba(5,12,22,.95));border:1px solid rgba(95,182,233,.32);border-radius:14px;padding:48px;box-shadow:0 30px 80px rgba(0,0,0,.85)">
<p style="font-family:Cinzel,serif;letter-spacing:.32em;text-transform:uppercase;color:#82d4ee;font-size:11px;margin:0 0 14px">— Accès refusé —</p>
<h1 style="font-family:Cinzel,serif;color:#e9f0f6;font-size:32px;margin:0 0 12px">Le portail t'est fermé</h1>
<p style="color:#b5c7d6;margin:0 0 28px">Bonjour <strong style="color:#e9f0f6">%s</strong> — ce panel est réservé aux administrateurs.</p>
<a href="/" style="display:inline-block;font-family:Cinzel,serif;letter-spacing:.16em;text-transform:uppercase;font-size:13px;padding:12px 22px;border-radius:4px;background:linear-gradient(135deg,#82d4ee,#5fb6e9 35%%,#2e8ec4 75%%,#1c5d88);color:#021018;border:1px solid rgba(28,93,136,.6);box-shadow:inset 0 1px 0 rgba(255,255,255,.55),0 8px 22px rgba(46,142,196,.45);text-decoration:none">Retour au site</a>
</div></body></html>`, template.HTMLEscapeString(username))
}

// ---------- dashboard -------------------------------------------------------

func handleDashboard(w http.ResponseWriter, r *http.Request) {
	user := ""
	if s := currentPlayer(r); s != nil {
		user = s.Username
	}
	_ = tpl.ExecuteTemplate(w, "app.html", map[string]any{
		"User": user,
	})
}

// ---------- API : status ----------------------------------------------------

func tcpUp(host, port string) bool {
	c, err := net.DialTimeout("tcp", host+":"+port, 800*time.Millisecond)
	if err != nil {
		return false
	}
	_ = c.Close()
	return true
}

func apiStatus(w http.ResponseWriter, r *http.Request) {
	type S struct {
		Name string `json:"name"`
		Up   bool   `json:"up"`
	}
	out := struct {
		Servers   []S    `json:"servers"`
		AccountsN int    `json:"accountsCount"`
		CharsN    int    `json:"charactersCount"`
		Online    int    `json:"playersOnline"`
		StartedAt string `json:"startedAt"`
		Uptime    string `json:"uptime"`
		Welcome   string `json:"welcomeMessage"`
	}{
		Servers: []S{
			{"auth", tcpUp(cfg.AuthHost, cfg.AuthPort)},
			{"world", tcpUp(cfg.WorldHost, cfg.WorldPort)},
		},
		StartedAt: startAt.Format(time.RFC3339),
		Uptime:    time.Since(startAt).Round(time.Second).String(),
		Welcome:   os.Getenv("WELCOME_MESSAGE"),
	}
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.AuthDB + ".accounts").Scan(&out.AccountsN)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".characters").Scan(&out.CharsN)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".oneair_online_clients").Scan(&out.Online)
	writeJSON(w, out)
}

// ---------- API : accounts --------------------------------------------------

type Account struct {
	ID                  int    `json:"id"`
	Username            string `json:"username"`
	Password            string `json:"password,omitempty"`
	Role                int    `json:"role"`
	Banned              int    `json:"banned"`
	Nickname            string `json:"nickname"`
	LastSelectedServer  int    `json:"lastSelectedServer"`
	CharactersSlots     int    `json:"characterSlots"`
}

var safeUserRe = regexp.MustCompile(`^[A-Za-z0-9_-]{2,32}$`)

func apiAccounts(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case "GET":
		rows, err := db.Query(`SELECT Id, Username, COALESCE(Password,''), COALESCE(Role,1),
		           COALESCE(Banned,0), COALESCE(Nickname,''), COALESCE(LastSelectedServerId,0),
		           COALESCE(CharactersSlots,5)
		   FROM ` + cfg.AuthDB + `.accounts ORDER BY Id DESC LIMIT 500`)
		if err != nil {
			httpErr(w, err)
			return
		}
		defer rows.Close()
		var out []Account
		for rows.Next() {
			var a Account
			if err := rows.Scan(&a.ID, &a.Username, &a.Password, &a.Role, &a.Banned,
				&a.Nickname, &a.LastSelectedServer, &a.CharactersSlots); err != nil {
				httpErr(w, err)
				return
			}
			out = append(out, a)
		}
		writeJSON(w, out)
	case "POST":
		var req struct {
			Action   string `json:"action"`
			Username string `json:"username"`
			Password string `json:"password"`
			Role     int    `json:"role"`
			ID       int    `json:"id"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			httpErr(w, err)
			return
		}
		switch req.Action {
		case "create":
			if !safeUserRe.MatchString(req.Username) {
				http.Error(w, "username invalide (2-32, [A-Za-z0-9_-])", 400)
				return
			}
			if len(req.Password) < 4 {
				http.Error(w, "password trop court (>=4)", 400)
				return
			}
			if req.Role < 1 || req.Role > 5 {
				req.Role = 1
			}
			_, err := db.Exec(`INSERT INTO `+cfg.AuthDB+`.accounts (Username, Password, Role, CharactersSlots, Banned)
				VALUES (?, ?, ?, 5, 0)
				ON DUPLICATE KEY UPDATE Password=VALUES(Password), Role=VALUES(Role)`,
				req.Username, req.Password, req.Role)
			if err != nil {
				httpErr(w, err)
				return
			}
		case "role":
			_, err := db.Exec(`UPDATE `+cfg.AuthDB+`.accounts SET Role=? WHERE Id=?`, req.Role, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
		case "ban":
			_, err := db.Exec(`UPDATE `+cfg.AuthDB+`.accounts SET Banned=1 WHERE Id=?`, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
		case "unban":
			_, err := db.Exec(`UPDATE `+cfg.AuthDB+`.accounts SET Banned=0 WHERE Id=?`, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
		case "delete":
			_, err := db.Exec(`DELETE FROM `+cfg.AuthDB+`.accounts WHERE Id=?`, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
		case "password":
			if len(req.Password) < 4 {
				http.Error(w, "password trop court", 400)
				return
			}
			_, err := db.Exec(`UPDATE `+cfg.AuthDB+`.accounts SET Password=? WHERE Id=?`, req.Password, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
		default:
			http.Error(w, "action inconnue", 400)
			return
		}
		writeJSON(w, map[string]string{"ok": "1"})
	default:
		http.Error(w, "method", 405)
	}
}

// ---------- API : characters ------------------------------------------------

type Character struct {
	ID         int64   `json:"id"`
	Name       string  `json:"name"`
	AccountID  int     `json:"accountId"`
	Username   string  `json:"username"`
	BreedID    int     `json:"breedId"`
	Sex        string  `json:"sex"`
	Experience int64   `json:"experience"`
	Kamas      int64   `json:"kamas"`
	MapID      int64   `json:"mapId"`
	CellID     int     `json:"cellId"`
	Online     bool    `json:"online"`
}

func apiCharacters(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, "method", 405)
		return
	}
	q := `SELECT c.Id, COALESCE(c.Name,''), COALESCE(c.AccountId,0), COALESCE(a.Username,''),
	             COALESCE(c.BreedId,0), COALESCE(c.Sex,''), COALESCE(c.Experience,0),
	             COALESCE(c.Kamas,0), COALESCE(c.MapId,0), COALESCE(c.CellId,0)
	      FROM ` + cfg.WorldDB + `.characters c
	      LEFT JOIN ` + cfg.AuthDB + `.accounts a ON a.Id = c.AccountId
	      ORDER BY c.Id DESC LIMIT 500`
	rows, err := db.Query(q)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	var out []Character
	for rows.Next() {
		var c Character
		if err := rows.Scan(&c.ID, &c.Name, &c.AccountID, &c.Username, &c.BreedID, &c.Sex,
			&c.Experience, &c.Kamas, &c.MapID, &c.CellID); err != nil {
			httpErr(w, err)
			return
		}
		out = append(out, c)
	}
	// online flag (best effort)
	online := map[int64]bool{}
	if rows2, err := db.Query("SELECT CharacterId FROM " + cfg.WorldDB + ".oneair_online_clients"); err == nil {
		defer rows2.Close()
		for rows2.Next() {
			var id int64
			if err := rows2.Scan(&id); err == nil {
				online[id] = true
			}
		}
	}
	for i := range out {
		if online[out[i].ID] {
			out[i].Online = true
		}
	}
	writeJSON(w, out)
}

// ---------- API : inventory -------------------------------------------------

type Item struct {
	UID          int    `json:"uid"`
	GID          int    `json:"gid"`
	Position     int    `json:"position"`
	Quantity     int    `json:"quantity"`
	AppearanceID int    `json:"appearanceId"`
	EffectsHex   string `json:"effectsHex"`
	Name         string `json:"name"`
	Look         string `json:"look"`
	Type         int    `json:"type"`
	Level        int    `json:"level"`
	Usable       int    `json:"usable"`
}

func apiInventory(w http.ResponseWriter, r *http.Request) {
	cidStr := r.URL.Query().Get("characterId")
	cid, err := strconv.ParseInt(cidStr, 10, 64)
	if err != nil {
		http.Error(w, "characterId requis", 400)
		return
	}
	switch r.Method {
	case "GET":
		rows, err := db.Query(`SELECT ci.UId, COALESCE(ci.GId,0), COALESCE(ci.Position,63),
		         COALESCE(ci.Quantity,1), COALESCE(ci.AppearanceId,0),
		         COALESCE(HEX(ci.Effects),''), COALESCE(i.Name,'?'), COALESCE(ci.Look,''),
		         COALESCE(i.TypeId,0), COALESCE(i.Level,0),
		         CASE WHEN LOWER(COALESCE(i.Usable,'')) = 'true' THEN 1 ELSE 0 END
		    FROM `+cfg.WorldDB+`.character_items ci
		    LEFT JOIN `+cfg.WorldDB+`.items i ON i.Id = ci.GId
		    WHERE ci.CharacterId = ?
		    ORDER BY (ci.Position != 63) DESC, ci.Position, ci.UId`, cid)
		if err != nil {
			httpErr(w, err)
			return
		}
		defer rows.Close()
		var out []Item
		for rows.Next() {
			var it Item
			if err := rows.Scan(&it.UID, &it.GID, &it.Position, &it.Quantity,
				&it.AppearanceID, &it.EffectsHex, &it.Name, &it.Look, &it.Type, &it.Level, &it.Usable); err != nil {
				httpErr(w, err)
				return
			}
			out = append(out, it)
		}
		writeJSON(w, out)
	case "POST":
		var req struct {
			Action   string `json:"action"`
			GID      int    `json:"gid"`
			Quantity int    `json:"quantity"`
			UID      int    `json:"uid"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			httpErr(w, err)
			return
		}
		switch req.Action {
		case "add":
			if req.Quantity <= 0 {
				req.Quantity = 1
			}
			// UID auto-incrémenté manuellement (pas d'auto_increment)
			var maxUID int
			_ = db.QueryRow("SELECT COALESCE(MAX(UId),0) FROM " + cfg.WorldDB + ".character_items").Scan(&maxUID)
			newUID := maxUID + 1
			_, err := db.Exec(`INSERT INTO `+cfg.WorldDB+`.character_items
				(UId, GId, Position, Quantity, Effects, AppearanceId, Look, CharacterId)
				VALUES (?, ?, 63, ?, '', 0, '', ?)`,
				newUID, req.GID, req.Quantity, cid)
			if err != nil {
				httpErr(w, err)
				return
			}
			queueAction("reload_inventory", strconv.FormatInt(cid, 10))
			writeJSON(w, map[string]int{"uid": newUID})
		case "delete":
			_, err := db.Exec(`DELETE FROM `+cfg.WorldDB+`.character_items WHERE UId=? AND CharacterId=?`,
				req.UID, cid)
			if err != nil {
				httpErr(w, err)
				return
			}
			queueAction("reload_inventory", strconv.FormatInt(cid, 10))
			writeJSON(w, map[string]string{"ok": "1"})
		case "setQty":
			if req.Quantity <= 0 {
				req.Quantity = 1
			}
			_, err := db.Exec(`UPDATE `+cfg.WorldDB+`.character_items SET Quantity=? WHERE UId=? AND CharacterId=?`,
				req.Quantity, req.UID, cid)
			if err != nil {
				httpErr(w, err)
				return
			}
			queueAction("reload_inventory", strconv.FormatInt(cid, 10))
			writeJSON(w, map[string]string{"ok": "1"})
		default:
			http.Error(w, "action inconnue", 400)
		}
	default:
		http.Error(w, "method", 405)
	}
}

// ---------- API : backups ---------------------------------------------------

type Backup struct {
	Name string    `json:"name"`
	Size int64     `json:"size"`
	Time time.Time `json:"time"`
}

func apiBackups(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case "GET":
		entries, err := os.ReadDir(cfg.BackupDir)
		if err != nil {
			httpErr(w, err)
			return
		}
		var out []Backup
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".sql.gz") {
				continue
			}
			info, err := e.Info()
			if err != nil {
				continue
			}
			out = append(out, Backup{Name: e.Name(), Size: info.Size(), Time: info.ModTime()})
		}
		sort.Slice(out, func(i, j int) bool { return out[i].Time.After(out[j].Time) })
		writeJSON(w, out)
	case "POST":
		var req struct {
			Action string `json:"action"`
			Name   string `json:"name"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			httpErr(w, err)
			return
		}
		switch req.Action {
		case "trigger":
			if err := triggerBackupNow(); err != nil {
				httpErr(w, err)
				return
			}
			writeJSON(w, map[string]string{"ok": "1"})
		case "delete":
			if !strings.HasSuffix(req.Name, ".sql.gz") || strings.Contains(req.Name, "/") {
				http.Error(w, "nom invalide", 400)
				return
			}
			if err := os.Remove(filepath.Join(cfg.BackupDir, req.Name)); err != nil {
				httpErr(w, err)
				return
			}
			writeJSON(w, map[string]string{"ok": "1"})
		case "restore":
			if !strings.HasSuffix(req.Name, ".sql.gz") || strings.Contains(req.Name, "/") {
				http.Error(w, "nom invalide", 400)
				return
			}
			if err := restoreBackup(filepath.Join(cfg.BackupDir, req.Name)); err != nil {
				httpErr(w, err)
				return
			}
			writeJSON(w, map[string]string{"ok": "1"})
		default:
			http.Error(w, "action inconnue", 400)
		}
	default:
		http.Error(w, "method", 405)
	}
}

func triggerBackupNow() error {
	// Le service backup boucle un sleep; on dump nous-mêmes en parallèle.
	ts := time.Now().UTC().Format("20060102T150405Z")
	out := filepath.Join(cfg.BackupDir, "giny_"+ts+"_manual.sql.gz")
	return runBackupTo(out)
}

func runBackupTo(out string) error {
	pw := os.Getenv("MYSQL_ROOT_PASSWORD")
	host := envOr("MYSQL_HOST", "mysql")
	cmd := exec.Command("sh", "-c",
		fmt.Sprintf(`mysqldump -h%s -uroot -p'%s' --single-transaction --quick --routines --triggers --databases %s %s | gzip -c > %s`,
			host, escapeShell(pw), cfg.AuthDB, cfg.WorldDB, escapeShell(out)))
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func restoreBackup(path string) error {
	pw := os.Getenv("MYSQL_ROOT_PASSWORD")
	host := envOr("MYSQL_HOST", "mysql")
	cmd := exec.Command("sh", "-c",
		fmt.Sprintf(`gunzip -c '%s' | mysql -h%s -uroot -p'%s' --default-character-set=utf8mb4`,
			escapeShell(path), host, escapeShell(pw)))
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func escapeShell(s string) string { return strings.ReplaceAll(s, "'", `'"'"'`) }

// ---------- API : broadcast / kick (via oneair_actions table) ---------------

func ensureActionsTable() error {
	_, err := db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_actions (
		Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
		Type VARCHAR(48) NOT NULL,
		Payload TEXT,
		ProcessedAt DATETIME NULL,
		Result TEXT NULL,
		CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
		KEY ix_unprocessed (ProcessedAt, Id)
	) ENGINE=InnoDB`)
	return err
}

func queueAction(typ, payload string) {
	_ = ensureActionsTable()
	_, _ = db.Exec(`INSERT INTO `+cfg.WorldDB+`.oneair_actions (Type, Payload) VALUES (?, ?)`, typ, payload)
}

func apiBroadcast(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "method", 405)
		return
	}
	var req struct {
		Message string `json:"message"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		httpErr(w, err)
		return
	}
	if strings.TrimSpace(req.Message) == "" {
		http.Error(w, "message vide", 400)
		return
	}
	queueAction("broadcast", req.Message)
	writeJSON(w, map[string]string{"ok": "1"})
}

func apiKick(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "method", 405)
		return
	}
	var req struct {
		CharacterID int64  `json:"characterId"`
		Reason      string `json:"reason"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		httpErr(w, err)
		return
	}
	queueAction("kick", fmt.Sprintf("%d|%s", req.CharacterID, req.Reason))
	writeJSON(w, map[string]string{"ok": "1"})
}

// /api/action — endpoint générique pour file une action arbitrair sur le world.
// Whitelist des types acceptés pour éviter les abus.
var allowedActionTypes = map[string]bool{
	"broadcast":         true,
	"kick":              true,
	"reload_inventory":  true,
	"send_pm":           true,
	"teleport":          true,
	"set_kamas":         true,
	"give_kamas":        true,
	"set_level":         true,
	"give_xp":           true,
	"give_item":         true,
	"heal":              true,
	"save_now":          true,
	"reload_items":      true,
	"shutdown":          true,
	"dump_inventory":    true,
	"item_set_qty":      true,
	"item_set_pos":      true,
	"item_delete":       true,
	"item_eff_add":      true,
	"item_eff_set":      true,
	"item_eff_del":      true,
	"learn_spell":       true,
	"forget_spell":      true,
	"set_spell_level":   true,
	"reset_spells":      true,
	"dump_spells":       true,
	"set_look":          true,
	"set_breed":         true,
	"set_sex":           true,
	"set_head":          true,
	"delete_character":  true,
	"reset_character":   true,
	"bulk_give_kamas":   true,
	"bulk_give_xp":      true,
	"bulk_give_item":    true,
	"bulk_heal":         true,
	"event_set":         true,
	"event_clear":       true,
}

func apiAction(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "method", 405)
		return
	}
	var req struct {
		Type    string `json:"type"`
		Payload string `json:"payload"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		httpErr(w, err)
		return
	}
	if !allowedActionTypes[req.Type] {
		http.Error(w, "type non autorisé", 400)
		return
	}
	queueAction(req.Type, req.Payload)
	writeJSON(w, map[string]string{"ok": "1"})
}

// /api/inventory/parsed?characterId=N — lit l'inventaire parsé écrit par
// le poller (table oneair_inventory_dumps). Renvoie items + effets décodés.
func apiInventoryParsed(w http.ResponseWriter, r *http.Request) {
	cidStr := r.URL.Query().Get("characterId")
	cid, err := strconv.ParseInt(cidStr, 10, 64)
	if err != nil {
		http.Error(w, "characterId requis", 400)
		return
	}
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_inventory_dumps (
		CharacterId BIGINT NOT NULL PRIMARY KEY,
		Json LONGTEXT,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
	) ENGINE=InnoDB`)
	var jsonStr string
	var updatedAt time.Time
	err = db.QueryRow(`SELECT Json, UpdatedAt FROM `+cfg.WorldDB+`.oneair_inventory_dumps WHERE CharacterId = ?`, cid).
		Scan(&jsonStr, &updatedAt)
	if err == sql.ErrNoRows {
		writeJSON(w, map[string]any{"items": []any{}, "updatedAt": nil, "needsDump": true})
		return
	}
	if err != nil {
		httpErr(w, err)
		return
	}
	var parsed any
	_ = json.Unmarshal([]byte(jsonStr), &parsed)
	writeJSON(w, map[string]any{"items": parsed, "updatedAt": updatedAt})
}

// /api/items/catalog?q=&type=&offset=&limit= — items du jeu, paginé.
func apiItemsCatalog(w http.ResponseWriter, r *http.Request) {
	q := strings.TrimSpace(r.URL.Query().Get("q"))
	typeFilter := r.URL.Query().Get("type")
	offset, _ := strconv.Atoi(r.URL.Query().Get("offset"))
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	if limit <= 0 || limit > 200 {
		limit = 60
	}
	args := []any{}
	where := []string{"1=1"}
	if q != "" {
		where = append(where, "Name LIKE ?")
		args = append(args, "%"+q+"%")
	}
	if typeFilter != "" {
		ids := strings.Split(typeFilter, ",")
		ph := make([]string, 0, len(ids))
		for _, id := range ids {
			if n, err := strconv.Atoi(id); err == nil {
				ph = append(ph, "?")
				args = append(args, n)
			}
		}
		if len(ph) > 0 {
			where = append(where, "TypeId IN ("+strings.Join(ph, ",")+")")
		}
	}
	if excludeFilter := r.URL.Query().Get("excludeType"); excludeFilter != "" {
		ids := strings.Split(excludeFilter, ",")
		ph := make([]string, 0, len(ids))
		for _, id := range ids {
			if n, err := strconv.Atoi(id); err == nil {
				ph = append(ph, "?")
				args = append(args, n)
			}
		}
		if len(ph) > 0 {
			where = append(where, "TypeId NOT IN ("+strings.Join(ph, ",")+")")
		}
	}
	args = append(args, limit, offset)
	if usableFilter := r.URL.Query().Get("usable"); usableFilter != "" {
		if usableFilter == "1" || usableFilter == "true" {
			where = append(where, "LOWER(COALESCE(Usable,'')) = 'true'")
		} else if usableFilter == "0" || usableFilter == "false" {
			where = append(where, "LOWER(COALESCE(Usable,'')) <> 'true'")
		}
	}
	rows, err := db.Query(`SELECT Id, COALESCE(Name,''), COALESCE(TypeId,0), COALESCE(Level,0),
		CASE WHEN LOWER(COALESCE(Usable,'')) = 'true' THEN 1 ELSE 0 END AS Usable
		FROM `+cfg.WorldDB+`.items WHERE `+strings.Join(where, " AND ")+`
		ORDER BY Level ASC, Id ASC LIMIT ? OFFSET ?`, args...)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type Item struct {
		ID     int64 `json:"id"`
		Name   string `json:"name"`
		Type   int    `json:"type"`
		Level  int    `json:"level"`
		Usable int    `json:"usable"`
	}
	out := []Item{}
	for rows.Next() {
		var it Item
		if err := rows.Scan(&it.ID, &it.Name, &it.Type, &it.Level, &it.Usable); err != nil {
			httpErr(w, err)
			return
		}
		out = append(out, it)
	}
	writeJSON(w, out)
}

// /api/spells/catalog?q=&offset=&limit= — sorts du jeu paginés.
func apiSpellsCatalog(w http.ResponseWriter, r *http.Request) {
	q := strings.TrimSpace(r.URL.Query().Get("q"))
	offset, _ := strconv.Atoi(r.URL.Query().Get("offset"))
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	if limit <= 0 || limit > 200 {
		limit = 60
	}
	args := []any{}
	where := "1=1"
	if q != "" {
		where = "Name LIKE ?"
		args = append(args, "%"+q+"%")
	}
	args = append(args, limit, offset)
	rows, err := db.Query(`SELECT Id, COALESCE(Name,''), COALESCE(Category,'')
		FROM `+cfg.WorldDB+`.spells WHERE `+where+`
		ORDER BY Id ASC LIMIT ? OFFSET ?`, args...)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type Spell struct {
		ID       int    `json:"id"`
		Name     string `json:"name"`
		Category string `json:"category"`
	}
	out := []Spell{}
	for rows.Next() {
		var s Spell
		if err := rows.Scan(&s.ID, &s.Name, &s.Category); err != nil {
			httpErr(w, err)
			return
		}
		out = append(out, s)
	}
	writeJSON(w, out)
}

// /api/spells/parsed?characterId=N — sorts connus du joueur (depuis dump action).
func apiSpellsParsed(w http.ResponseWriter, r *http.Request) {
	cidStr := r.URL.Query().Get("characterId")
	cid, err := strconv.ParseInt(cidStr, 10, 64)
	if err != nil {
		http.Error(w, "characterId requis", 400)
		return
	}
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_spell_dumps (
		CharacterId BIGINT NOT NULL PRIMARY KEY,
		Json LONGTEXT,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
	) ENGINE=InnoDB`)
	var jsonStr string
	var updatedAt time.Time
	err = db.QueryRow(`SELECT Json, UpdatedAt FROM `+cfg.WorldDB+`.oneair_spell_dumps WHERE CharacterId = ?`, cid).
		Scan(&jsonStr, &updatedAt)
	if err == sql.ErrNoRows {
		writeJSON(w, map[string]any{"spells": []any{}, "updatedAt": nil, "needsDump": true})
		return
	}
	if err != nil {
		httpErr(w, err)
		return
	}
	var parsed any
	_ = json.Unmarshal([]byte(jsonStr), &parsed)
	writeJSON(w, map[string]any{"spells": parsed, "updatedAt": updatedAt})
}

// /api/events — liste les multiplicateurs d'événements actifs (table oneair_events).
func apiEvents(w http.ResponseWriter, r *http.Request) {
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_events (
		Type VARCHAR(32) NOT NULL PRIMARY KEY,
		Multiplier DOUBLE NOT NULL DEFAULT 1.0,
		ExpiresAt DATETIME NULL,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		UpdatedBy VARCHAR(64) NULL
	) ENGINE=InnoDB`)
	rows, err := db.Query(`SELECT Type, Multiplier, ExpiresAt, UpdatedAt FROM ` + cfg.WorldDB + `.oneair_events`)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type Event struct {
		Type       string     `json:"type"`
		Multiplier float64    `json:"multiplier"`
		ExpiresAt  *time.Time `json:"expiresAt"`
		UpdatedAt  time.Time  `json:"updatedAt"`
	}
	out := []Event{}
	for rows.Next() {
		var e Event
		var exp sql.NullTime
		if err := rows.Scan(&e.Type, &e.Multiplier, &exp, &e.UpdatedAt); err != nil {
			httpErr(w, err)
			return
		}
		if exp.Valid {
			e.ExpiresAt = &exp.Time
		}
		out = append(out, e)
	}
	writeJSON(w, out)
}

// /api/players/online — liste détaillée des joueurs en ligne (online_clients
// joint avec characters + map_positions + subareas/areas pour la zone).
func apiPlayersOnline(w http.ResponseWriter, r *http.Request) {
	// On crée la table on-the-fly au cas où elle n'existerait pas
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_online_clients (
		CharacterId BIGINT NOT NULL PRIMARY KEY,
		AccountId INT NULL, Name VARCHAR(255) NULL, Level INT NULL, MapId BIGINT NULL,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
	) ENGINE=InnoDB`)

	rows, err := db.Query(`SELECT
		oc.CharacterId, COALESCE(oc.Name,''), COALESCE(oc.Level,0),
		COALESCE(c.AccountId,0), COALESCE(c.BreedId,0), COALESCE(c.Sex,''),
		COALESCE(c.Kamas,0), COALESCE(c.Experience,0),
		COALESCE(oc.MapId,0), COALESCE(mp.X,0), COALESCE(mp.Y,0),
		COALESCE(s.Name,''), COALESCE(a.WorldMapId,0),
		COALESCE(au.Username,''), oc.UpdatedAt
		FROM ` + cfg.WorldDB + `.oneair_online_clients oc
		LEFT JOIN ` + cfg.WorldDB + `.characters c ON c.Id = oc.CharacterId
		LEFT JOIN ` + cfg.WorldDB + `.map_positions mp ON mp.Id = oc.MapId
		LEFT JOIN ` + cfg.WorldDB + `.maps m ON m.Id = oc.MapId
		LEFT JOIN ` + cfg.WorldDB + `.subareas s ON s.Id = m.SubareaId
		LEFT JOIN ` + cfg.WorldDB + `.areas a ON a.Id = s.AreaId
		LEFT JOIN ` + cfg.AuthDB + `.accounts au ON au.Id = c.AccountId
		ORDER BY oc.Level DESC, oc.Name`)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type Player struct {
		CharacterID int64     `json:"characterId"`
		Name        string    `json:"name"`
		Level       int       `json:"level"`
		AccountID   int       `json:"accountId"`
		Username    string    `json:"username"`
		BreedID     int       `json:"breedId"`
		Sex         string    `json:"sex"`
		Kamas       int64     `json:"kamas"`
		Experience  int64     `json:"experience"`
		MapID       int64     `json:"mapId"`
		MapX        int       `json:"mapX"`
		MapY        int       `json:"mapY"`
		Zone        string    `json:"zone"`
		WorldID     int       `json:"worldId"`
		UpdatedAt   time.Time `json:"updatedAt"`
	}
	out := []Player{}
	for rows.Next() {
		var p Player
		if err := rows.Scan(&p.CharacterID, &p.Name, &p.Level,
			&p.AccountID, &p.BreedID, &p.Sex, &p.Kamas, &p.Experience,
			&p.MapID, &p.MapX, &p.MapY, &p.Zone, &p.WorldID,
			&p.Username, &p.UpdatedAt); err != nil {
			httpErr(w, err)
			return
		}
		out = append(out, p)
	}
	writeJSON(w, out)
}

// /classes/<file> — sert les assets de classes depuis le client local.
//   ex: /classes/symbol_8.png  → content/gfx/classes/symbol_8.png
//   ex: /classes/bg_80.jpg     → bg_<breed><sex>.jpg
func classAssetHandler() http.Handler {
	dir := envOr("CLASSES_DIR", "/app/OneAir.app/Contents/Resources/content/gfx/classes")
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") {
			http.NotFound(w, r)
			return
		}
		if !strings.HasSuffix(name, ".png") && !strings.HasSuffix(name, ".jpg") {
			http.NotFound(w, r)
			return
		}
		local := filepath.Join(dir, name)
		if !fileExists(local) {
			// fallback : essaye dans bgSelectCharacter/
			local = filepath.Join(dir, "bgSelectCharacter", name)
		}
		if fileExists(local) {
			w.Header().Set("Cache-Control", "public, max-age=86400")
			http.ServeFile(w, r, local)
			return
		}
		http.NotFound(w, r)
	})
}

// /api/worlds (déprécié — minimap retiré)
func apiWorlds(w http.ResponseWriter, r *http.Request) {
	rows, err := db.Query(`SELECT COALESCE(a.WorldMapId,0) as wid,
		MIN(mp.X) as minX, MAX(mp.X) as maxX,
		MIN(mp.Y) as minY, MAX(mp.Y) as maxY,
		COUNT(*) as mapCount,
		(SELECT GROUP_CONCAT(DISTINCT a2.Name SEPARATOR ', ')
		 FROM ` + cfg.WorldDB + `.areas a2 WHERE a2.WorldMapId = a.WorldMapId LIMIT 5) as areaNames
		FROM ` + cfg.WorldDB + `.map_positions mp
		LEFT JOIN ` + cfg.WorldDB + `.maps m ON m.Id = mp.Id
		LEFT JOIN ` + cfg.WorldDB + `.subareas s ON s.Id = m.SubareaId
		LEFT JOIN ` + cfg.WorldDB + `.areas a ON a.Id = s.AreaId
		GROUP BY wid
		HAVING mapCount > 5
		ORDER BY mapCount DESC`)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type World struct {
		ID       int    `json:"id"`
		MinX     int    `json:"minX"`
		MaxX     int    `json:"maxX"`
		MinY     int    `json:"minY"`
		MaxY     int    `json:"maxY"`
		MapCount int    `json:"mapCount"`
		Name     string `json:"name"`
	}
	var out []World
	for rows.Next() {
		var w World
		var areaNames sql.NullString
		if err := rows.Scan(&w.ID, &w.MinX, &w.MaxX, &w.MinY, &w.MaxY, &w.MapCount, &areaNames); err != nil {
			continue
		}
		if areaNames.Valid && areaNames.String != "" {
			w.Name = areaNames.String
		} else {
			w.Name = "World " + strconv.Itoa(w.ID)
		}
		// Friendlier names for known worlds
		w.Name = friendlyWorldName(w.ID, w.Name)
		out = append(out, w)
	}
	writeJSON(w, out)
}

func friendlyWorldName(id int, fallback string) string {
	known := map[int]string{
		0:  "Monde des Douze",
		1:  "Incarnam",
		2:  "Royaume Sufokien (Île)",
		13: "Île de Frigost",
		14: "Île de Pandala",
		15: "Île d'Otomaï",
		16: "Île des Dragoeufs",
		19: "Île des Wabbits",
		21: "Bétaversse",
		28: "Royaume Wakfu / Mont Tilse",
		30: "Dimension Eliotrope",
	}
	if k, ok := known[id]; ok {
		return k
	}
	return fallback
}

// /api/maps?cx=&cy=&w=&h=&world= — retourne les maps dans une fenêtre
// rectangulaire, filtrées par worldMapId si fourni.
func apiMaps(w http.ResponseWriter, r *http.Request) {
	cx, _ := strconv.Atoi(r.URL.Query().Get("cx"))
	cy, _ := strconv.Atoi(r.URL.Query().Get("cy"))
	wW, _ := strconv.Atoi(r.URL.Query().Get("w"))
	hW, _ := strconv.Atoi(r.URL.Query().Get("h"))
	worldFilter := r.URL.Query().Get("world")
	if wW <= 0 || wW > 60 {
		wW = 15
	}
	if hW <= 0 || hW > 60 {
		hW = 11
	}
	x0 := cx - wW/2
	x1 := cx + wW/2
	y0 := cy - hW/2
	y1 := cy + hW/2

	args := []any{x0, x1, y0, y1}
	worldClause := ""
	if worldFilter != "" {
		if wid, err := strconv.Atoi(worldFilter); err == nil {
			worldClause = " AND COALESCE(a.WorldMapId,0) = ?"
			args = append(args, wid)
		}
	}

	rows, err := db.Query(`SELECT mp.Id, mp.X, mp.Y, COALESCE(mp.Outdoor,'') as outdoor,
		    COALESCE(s.Name,'') as zone,
		    COALESCE(a.WorldMapId, 0) as worldId,
		    COALESCE((SELECT COUNT(*) FROM `+cfg.WorldDB+`.oneair_online_clients oc WHERE oc.MapId = mp.Id),0) as players
		FROM `+cfg.WorldDB+`.map_positions mp
		LEFT JOIN `+cfg.WorldDB+`.maps m ON m.Id = mp.Id
		LEFT JOIN `+cfg.WorldDB+`.subareas s ON s.Id = m.SubareaId
		LEFT JOIN `+cfg.WorldDB+`.areas a ON a.Id = s.AreaId
		WHERE mp.X BETWEEN ? AND ? AND mp.Y BETWEEN ? AND ?`+worldClause+`
		ORDER BY mp.Y, mp.X LIMIT 3000`, args...)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type Cell struct {
		ID      int64  `json:"id"`
		X       int    `json:"x"`
		Y       int    `json:"y"`
		Outdoor string `json:"outdoor"`
		Zone    string `json:"zone"`
		World   int    `json:"world"`
		Players int    `json:"players"`
	}
	var cells []Cell
	for rows.Next() {
		var c Cell
		if err := rows.Scan(&c.ID, &c.X, &c.Y, &c.Outdoor, &c.Zone, &c.World, &c.Players); err != nil {
			httpErr(w, err)
			return
		}
		cells = append(cells, c)
	}

	// Joueurs détaillés par map (pour tooltips)
	playerRows, _ := db.Query(`SELECT MapId, Name, COALESCE(Level,0)
		FROM ` + cfg.WorldDB + `.oneair_online_clients`)
	type Player struct {
		MapID int64  `json:"mapId"`
		Name  string `json:"name"`
		Level int    `json:"level"`
	}
	var players []Player
	if playerRows != nil {
		defer playerRows.Close()
		for playerRows.Next() {
			var p Player
			if err := playerRows.Scan(&p.MapID, &p.Name, &p.Level); err == nil {
				players = append(players, p)
			}
		}
	}

	writeJSON(w, map[string]any{
		"window":  map[string]int{"cx": cx, "cy": cy, "w": wW, "h": hW},
		"cells":   cells,
		"players": players,
	})
}

func apiActionsList(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, "method", 405)
		return
	}
	_ = ensureActionsTable()
	rows, err := db.Query(`SELECT Id, Type, COALESCE(Payload,''), ProcessedAt, COALESCE(Result,''), CreatedAt
		FROM ` + cfg.WorldDB + `.oneair_actions
		ORDER BY Id DESC LIMIT 50`)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	type A struct {
		ID          int64      `json:"id"`
		Type        string     `json:"type"`
		Payload     string     `json:"payload"`
		ProcessedAt *time.Time `json:"processedAt"`
		Result      string     `json:"result"`
		CreatedAt   time.Time  `json:"createdAt"`
	}
	var out []A
	for rows.Next() {
		var a A
		var p sql.NullTime
		if err := rows.Scan(&a.ID, &a.Type, &a.Payload, &p, &a.Result, &a.CreatedAt); err != nil {
			httpErr(w, err)
			return
		}
		if p.Valid {
			a.ProcessedAt = &p.Time
		}
		out = append(out, a)
	}
	writeJSON(w, out)
}

// ---------- helpers ---------------------------------------------------------

// itemAssetHandler : sert les icônes d'items.
// Stratégie :
//   1) cache local /items-cache/{gid}.png (extrait des d2p Dofus)
//   2) lookup iconId via items.AppearenceId ou DofusDB API → cache local /items-cache/{iconId}.png
//   3) redirect vers DofusDB (api.dofusdb.fr) qui sert tous les item icons par iconId
var iconIDCache sync.Map // gid (string) -> iconId (string), "0" = no icon

func itemAssetHandler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") {
			http.NotFound(w, r)
			return
		}
		if !strings.HasSuffix(name, ".png") {
			http.NotFound(w, r)
			return
		}
		gid := strings.TrimSuffix(name, ".png")

		// 1) cache local par gid
		if local := filepath.Join(cfg.ItemsCacheDir, name); fileExists(local) {
			http.ServeFile(w, r, local)
			return
		}

		// 2) lookup iconId
		iconId, _ := resolveIconID(gid)
		if iconId != "" && iconId != "0" {
			if local := filepath.Join(cfg.ItemsCacheDir, iconId+".png"); fileExists(local) {
				http.ServeFile(w, r, local)
				return
			}
			http.Redirect(w, r, "https://api.dofusdb.fr/img/items/"+iconId+".png", http.StatusFound)
			return
		}

		// 3) fallback : tente direct en gid sur la CDN
		http.Redirect(w, r, "https://api.dofusdb.fr/img/items/"+gid+".png", http.StatusFound)
	})
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

// /spells/{id}.png — redirige vers DofusDB (icône de sort officielle).
func spellAssetHandler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") || !strings.HasSuffix(name, ".png") {
			http.NotFound(w, r)
			return
		}
		id := strings.TrimSuffix(name, ".png")
		http.Redirect(w, r, "https://api.dofusdb.fr/img/spells/"+id+".png", http.StatusFound)
	})
}

// /heads/{id}.png — sert SmallHead_<id>.png depuis le client local OneAir.
func headAssetHandler() http.Handler {
	dir := envOr("HEADS_DIR", "/app/OneAir.app/Contents/Resources/content/gfx/heads")
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") || !strings.HasSuffix(name, ".png") {
			http.NotFound(w, r)
			return
		}
		id := strings.TrimSuffix(name, ".png")
		local := filepath.Join(dir, "SmallHead_"+id+".png")
		if fileExists(local) {
			http.ServeFile(w, r, local)
			return
		}
		http.NotFound(w, r)
	})
}

// /worldmap/{name} — sert les tuiles du worldmap extraites des .d2p locaux.
// Les noms de fichiers sont tels que stockés dans l'archive (ex: "0_0.jpg").
func worldmapAssetHandler() http.Handler {
	dir := envOr("WORLDMAP_CACHE_DIR", "/worldmap-cache")
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") {
			http.NotFound(w, r)
			return
		}
		local := filepath.Join(dir, name)
		if fileExists(local) {
			w.Header().Set("Cache-Control", "public, max-age=86400")
			http.ServeFile(w, r, local)
			return
		}
		http.NotFound(w, r)
	})
}

// /breeds/{id}.png — icône de classe (Iop, Crâ, …) servie depuis le client
// local (logo_transparent_X.png dans /classes-src). DofusDB ne sert plus
// ces assets sous l'ancienne URL ; on n'a pas besoin de fallback réseau.
func breedAssetHandler() http.Handler {
	dir := envOr("CLASSES_DIR", "/app/OneAir.app/Contents/Resources/content/gfx/classes")
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Path
		if strings.Contains(name, "..") || strings.Contains(name, "/") || !strings.HasSuffix(name, ".png") {
			http.NotFound(w, r)
			return
		}
		id := strings.TrimSuffix(name, ".png")
		local := filepath.Join(dir, "logo_transparent_"+id+".png")
		if fileExists(local) {
			w.Header().Set("Cache-Control", "public, max-age=86400")
			http.ServeFile(w, r, local)
			return
		}
		http.NotFound(w, r)
	})
}

// resolveIconID : trouve l'iconId d'un gid via DofusDB (cache mémoire).
func resolveIconID(gid string) (string, error) {
	if v, ok := iconIDCache.Load(gid); ok {
		return v.(string), nil
	}
	// DofusDB
	cli := &http.Client{Timeout: 4 * time.Second}
	resp, err := cli.Get("https://api.dofusdb.fr/items/" + gid)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		iconIDCache.Store(gid, "0")
		return "0", nil
	}
	var d struct {
		IconID int `json:"iconId"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&d); err != nil {
		return "", err
	}
	id := strconv.Itoa(d.IconID)
	iconIDCache.Store(gid, id)
	return id, nil
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(v)
}

func httpErr(w http.ResponseWriter, err error) {
	if errors.Is(err, sql.ErrNoRows) {
		http.Error(w, "introuvable", 404)
		return
	}
	log.Printf("err: %v", err)
	http.Error(w, err.Error(), 500)
}
