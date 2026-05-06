// Page d'accueil publique : présentation OneAir + articles + login joueur +
// téléchargement client. Sert aussi /article/{slug} (SSR), /download/macos
// et /download/windows.
package main

import (
	"archive/zip"
	"database/sql"
	"fmt"
	"html/template"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

type landingData struct {
	Welcome  string
	Online   int
	Accounts int
	Chars    int
	AuthUp   bool
	WorldUp  bool
	Articles []Article
	Latest   *Article
	DLMacURL string
	DLWinURL string
	XPRate   string
	DropRate string
	JobRate  string
}

// effectiveRate retourne le multiplicateur effectif d'un type d'événement :
// la valeur de oneair_events si une row non-expirée existe, sinon le
// fallback (env). Format string formaté joliment ("1", "10", "1.5").
func effectiveRate(eventType, fallback string) string {
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_events (
		Type VARCHAR(32) NOT NULL PRIMARY KEY,
		Multiplier DOUBLE NOT NULL DEFAULT 1.0,
		ExpiresAt DATETIME NULL,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		UpdatedBy VARCHAR(64) NULL
	) ENGINE=InnoDB`)
	var mul float64
	var exp sql.NullTime
	err := db.QueryRow(`SELECT Multiplier, ExpiresAt FROM `+cfg.WorldDB+`.oneair_events WHERE Type = ?`, eventType).
		Scan(&mul, &exp)
	if err != nil {
		return fallback
	}
	if exp.Valid && time.Now().After(exp.Time) {
		return fallback
	}
	if mul == float64(int64(mul)) {
		return fmt.Sprintf("%d", int64(mul))
	}
	return strings.TrimRight(strings.TrimRight(fmt.Sprintf("%.2f", mul), "0"), ".")
}

// GET / — landing publique. Toute path autre que "/" → 404.
func handleLanding(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" {
		http.NotFound(w, r)
		return
	}
	_ = ensureArticlesSchema()
	d := landingData{
		Welcome:  os.Getenv("WELCOME_MESSAGE"),
		AuthUp:   tcpUp(cfg.AuthHost, cfg.AuthPort),
		WorldUp:  tcpUp(cfg.WorldHost, cfg.WorldPort),
		// Toujours rempli quand le download est dispo : URL externe (s'il y
		// en a une), sinon "/download/{macos,windows}" qui zippe le bundle local.
		DLMacURL: macDownloadURL(),
		DLWinURL: winDownloadURL(),
		// Lit oneair_events (multiplicateurs live), retombe sur l'env si pas
		// d'événement actif. C'est ce qu'affiche déjà l'admin → cohérence.
		XPRate:   effectiveRate("xp", envOr("XP_RATE", "1")),
		DropRate: effectiveRate("drop", envOr("DROP_RATE", "1")),
		JobRate:  effectiveRate("job", envOr("JOB_RATE", "1")),
	}
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.AuthDB + ".accounts").Scan(&d.Accounts)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".characters").Scan(&d.Chars)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".oneair_online_clients").Scan(&d.Online)

	rows, err := db.Query(`SELECT Id, Slug, Title, Excerpt, Author, CoverImage, Tag, CreatedAt
		FROM ` + cfg.WorldDB + `.oneair_articles WHERE Published = 1
		ORDER BY CreatedAt DESC LIMIT 9`)
	if err == nil {
		defer rows.Close()
		for rows.Next() {
			var a Article
			if err := rows.Scan(&a.ID, &a.Slug, &a.Title, &a.Excerpt, &a.Author,
				&a.CoverImage, &a.Tag, &a.CreatedAt); err == nil {
				d.Articles = append(d.Articles, a)
			}
		}
	}
	if len(d.Articles) > 0 {
		d.Latest = &d.Articles[0]
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := tpl.ExecuteTemplate(w, "landing.html", d); err != nil {
		http.Error(w, err.Error(), 500)
	}
}

// GET /article/{slug} — page article SSR.
func handleArticle(w http.ResponseWriter, r *http.Request) {
	slug := strings.TrimPrefix(r.URL.Path, "/article/")
	slug = strings.TrimSuffix(slug, "/")
	if slug == "" || !slugRe.MatchString(slug) {
		http.NotFound(w, r)
		return
	}
	_ = ensureArticlesSchema()
	a, err := loadArticleBySlug(slug)
	if err == sql.ErrNoRows {
		http.NotFound(w, r)
		return
	}
	if err != nil {
		http.Error(w, err.Error(), 500)
		return
	}
	if !a.Published {
		http.NotFound(w, r)
		return
	}
	body := renderMarkdown(a.Content)
	related := []Article{}
	rows, err := db.Query(`SELECT Slug, Title, Excerpt, CoverImage, Tag, CreatedAt
		FROM `+cfg.WorldDB+`.oneair_articles WHERE Published = 1 AND Id <> ?
		ORDER BY CreatedAt DESC LIMIT 3`, a.ID)
	if err == nil {
		defer rows.Close()
		for rows.Next() {
			var r2 Article
			if err := rows.Scan(&r2.Slug, &r2.Title, &r2.Excerpt, &r2.CoverImage, &r2.Tag, &r2.CreatedAt); err == nil {
				related = append(related, r2)
			}
		}
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_ = tpl.ExecuteTemplate(w, "article.html", map[string]any{
		"Article": a,
		"Body":    body,
		"Related": related,
	})
}

// macDownloadURL retourne l'URL à utiliser pour le bouton "télécharger" de la
// landing. Vide si rien n'est dispo (ni mirror externe, ni bind local).
func macDownloadURL() string {
	if u := os.Getenv("DOWNLOAD_MACOS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		return u
	}
	appDir := envOr("ONEAIR_APP_DIR", "/app/OneAir.app")
	if st, err := os.Stat(appDir); err == nil && st.IsDir() {
		return "/download/macos"
	}
	return ""
}

// winDownloadURL : pendant Windows. Identique à macDownloadURL mais pointe
// sur le dossier OneAir-Windows/ généré par client/build-app-windows.sh.
func winDownloadURL() string {
	if u := os.Getenv("DOWNLOAD_WINDOWS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		return u
	}
	appDir := envOr("ONEAIR_WIN_DIR", "/app/OneAir-Windows")
	if st, err := os.Stat(appDir); err == nil && st.IsDir() {
		return "/download/windows"
	}
	return ""
}

// GET /download/macos — sert le client macOS.
//
// Quatre modes (par ordre de priorité) :
//   1) DOWNLOAD_MACOS_URL pointe vers une URL externe (https://...) → 302.
//   2) ONEAIR_APP_ZIP (par défaut /app/dist/OneAir.app.zip) existe → on sert
//      le fichier directement via http.ServeFile (Range supporté, instant).
//      C'est ce que produisent client/build-docker-darwin.sh à chaque build.
//   3) ONEAIR_APP_DIR (par défaut /app/OneAir.app) existe → on streame un .zip
//      à la volée (legacy fallback, lent : walk de 4.7 GB à chaque download).
//   4) Sinon → page "bientôt".
func handleDownloadMac(w http.ResponseWriter, r *http.Request) {
	if u := os.Getenv("DOWNLOAD_MACOS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		http.Redirect(w, r, u, http.StatusFound)
		return
	}

	zipPath := envOr("ONEAIR_APP_ZIP", "/app/dist/OneAir.app.zip")
	if st, err := os.Stat(zipPath); err == nil && !st.IsDir() {
		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Content-Disposition", `attachment; filename="OneAir.app.zip"`)
		w.Header().Set("X-Content-Type-Options", "nosniff")
		http.ServeFile(w, r, zipPath)
		return
	}

	appDir := envOr("ONEAIR_APP_DIR", "/app/OneAir.app")
	if st, err := os.Stat(appDir); err == nil && st.IsDir() {
		streamOneAirAppZip(w, r, appDir, "OneAir.zip")
		return
	}

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusServiceUnavailable)
	_, _ = fmt.Fprint(w, `<!doctype html><meta charset="utf-8"><title>Téléchargement bientôt disponible</title><body style="font-family:system-ui;background:#1f1208;color:#e9d8a6;padding:60px;text-align:center;">
<h1 style="font-family:Cinzel,serif;color:#f4c34c">Le client arrive bientôt</h1>
<p>Le binaire OneAir pour macOS n'est pas encore publié sur le miroir.</p>
<p style="opacity:.6">Bind <code>OneAir.app</code> dans le container <code>web</code>, ou configure <code>DOWNLOAD_MACOS_URL</code>.</p>
<p><a href="/" style="color:#f4c34c;border:1px solid #6b4a16;padding:8px 18px;border-radius:6px;text-decoration:none">Retour</a></p>`)
}

// GET /download/windows — sert le client Windows.
//
// Mêmes 4 modes que handleDownloadMac, mais sur OneAir-Windows/ (le dossier
// généré par client/build-docker-windows.sh) et son zip statique.
func handleDownloadWindows(w http.ResponseWriter, r *http.Request) {
	if u := os.Getenv("DOWNLOAD_WINDOWS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		http.Redirect(w, r, u, http.StatusFound)
		return
	}

	zipPath := envOr("ONEAIR_WIN_ZIP", "/app/dist/OneAir-Windows.zip")
	if st, err := os.Stat(zipPath); err == nil && !st.IsDir() {
		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Content-Disposition", `attachment; filename="OneAir-Windows.zip"`)
		w.Header().Set("X-Content-Type-Options", "nosniff")
		http.ServeFile(w, r, zipPath)
		return
	}

	appDir := envOr("ONEAIR_WIN_DIR", "/app/OneAir-Windows")
	if st, err := os.Stat(appDir); err == nil && st.IsDir() {
		streamOneAirAppZip(w, r, appDir, "OneAir-Windows.zip")
		return
	}

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusServiceUnavailable)
	_, _ = fmt.Fprint(w, `<!doctype html><meta charset="utf-8"><title>Téléchargement bientôt disponible</title><body style="font-family:system-ui;background:#1f1208;color:#e9d8a6;padding:60px;text-align:center;">
<h1 style="font-family:Cinzel,serif;color:#f4c34c">Le client arrive bientôt</h1>
<p>Le binaire OneAir pour Windows n'est pas encore publié sur le miroir.</p>
<p style="opacity:.6">Bind <code>OneAir-Windows/</code> dans le container <code>web</code>, ou configure <code>DOWNLOAD_WINDOWS_URL</code>.</p>
<p><a href="/" style="color:#f4c34c;border:1px solid #6b4a16;padding:8px 18px;border-radius:6px;text-decoration:none">Retour</a></p>`)
}

// Streame un .zip d'un dossier (OneAir.app ou OneAir-Windows). Pas de
// buffering disque, pas de compression (Store) : le bundle fait ~5 GB et
// contient surtout du binaire/d2p déjà compressé. Le client reçoit donc
// un download progressif.
func streamOneAirAppZip(w http.ResponseWriter, r *http.Request, appDir, fileName string) {
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s"`, fileName))
	w.Header().Set("X-Content-Type-Options", "nosniff")

	zw := zip.NewWriter(w)
	defer zw.Close()

	rootName := filepath.Base(appDir) // "OneAir.app" / "OneAir-Windows" — entrée racine dans le .zip
	walkErr := filepath.Walk(appDir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			log.Printf("download zip: walk error on %s: %v", path, err)
			return nil // skip
		}
		// chemin relatif sous "OneAir.app/..."
		rel, err := filepath.Rel(appDir, path)
		if err != nil {
			return nil
		}
		entryName := rootName
		if rel != "." {
			entryName = filepath.Join(rootName, rel)
		}

		if info.IsDir() {
			// On crée explicitement les entrées de dossier pour préserver
			// les permissions/exec du bundle.
			if rel == "." {
				return nil
			}
			h, _ := zip.FileInfoHeader(info)
			h.Name = entryName + "/"
			h.Method = zip.Store
			_, err := zw.CreateHeader(h)
			return err
		}

		// Symlinks : on les écrit comme tels (le .app en contient quelques-uns
		// dans Frameworks/. ZIP les supporte via le mode et le contenu = cible).
		if info.Mode()&os.ModeSymlink != 0 {
			target, err := os.Readlink(path)
			if err != nil {
				return nil
			}
			h, _ := zip.FileInfoHeader(info)
			h.Name = entryName
			h.SetMode(info.Mode())
			fw, err := zw.CreateHeader(h)
			if err != nil {
				return err
			}
			_, _ = fw.Write([]byte(target))
			return nil
		}

		h, _ := zip.FileInfoHeader(info)
		h.Name = entryName
		h.Method = zip.Store // pas de compression : .app est déjà des binaires/d2p
		fw, err := zw.CreateHeader(h)
		if err != nil {
			return err
		}
		f, err := os.Open(path)
		if err != nil {
			return nil // skip ce fichier
		}
		defer f.Close()
		_, err = io.Copy(fw, f)
		return err
	})
	if walkErr != nil {
		log.Printf("download zip: walk failed: %v", walkErr)
	}
}

// GET /api/public/status — status léger pour le polling de la landing.
func apiPublicStatus(w http.ResponseWriter, r *http.Request) {
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.oneair_online_clients (
		CharacterId BIGINT NOT NULL PRIMARY KEY,
		AccountId INT NULL, Name VARCHAR(255) NULL, Level INT NULL, MapId BIGINT NULL,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
	) ENGINE=InnoDB`)
	out := struct {
		AuthUp   bool `json:"authUp"`
		WorldUp  bool `json:"worldUp"`
		Online   int  `json:"online"`
		Accounts int  `json:"accounts"`
		Chars    int  `json:"characters"`
	}{
		AuthUp:  tcpUp(cfg.AuthHost, cfg.AuthPort),
		WorldUp: tcpUp(cfg.WorldHost, cfg.WorldPort),
	}
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.AuthDB + ".accounts").Scan(&out.Accounts)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".characters").Scan(&out.Chars)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".oneair_online_clients").Scan(&out.Online)
	writeJSON(w, out)
}

// renderMarkdown : sous-ensemble Markdown sécurisé.
//
// Supporte : # / ## / ### titres, paragraphes, listes -, **gras**, *italique*,
// `code`, [texte](url). Tout est HTML-escapé avant remplacement, les liens
// sont http(s) ou relatifs uniquement.
func renderMarkdown(src string) template.HTML {
	var out strings.Builder
	lines := strings.Split(src, "\n")
	inList := false
	inPara := false
	flushPara := func() {
		if inPara {
			out.WriteString("</p>\n")
			inPara = false
		}
	}
	flushList := func() {
		if inList {
			out.WriteString("</ul>\n")
			inList = false
		}
	}
	for _, ln := range lines {
		l := strings.TrimRight(ln, "\r")
		t := strings.TrimSpace(l)
		if t == "" {
			flushPara()
			flushList()
			continue
		}
		switch {
		case strings.HasPrefix(t, "### "):
			flushPara()
			flushList()
			fmt.Fprintf(&out, "<h3>%s</h3>\n", inlineMD(strings.TrimPrefix(t, "### ")))
		case strings.HasPrefix(t, "## "):
			flushPara()
			flushList()
			fmt.Fprintf(&out, "<h2>%s</h2>\n", inlineMD(strings.TrimPrefix(t, "## ")))
		case strings.HasPrefix(t, "# "):
			flushPara()
			flushList()
			fmt.Fprintf(&out, "<h1>%s</h1>\n", inlineMD(strings.TrimPrefix(t, "# ")))
		case strings.HasPrefix(t, "- "):
			flushPara()
			if !inList {
				out.WriteString("<ul>\n")
				inList = true
			}
			fmt.Fprintf(&out, "<li>%s</li>\n", inlineMD(strings.TrimPrefix(t, "- ")))
		default:
			flushList()
			if !inPara {
				out.WriteString("<p>")
				inPara = true
			} else {
				out.WriteString(" ")
			}
			out.WriteString(inlineMD(t))
		}
	}
	flushPara()
	flushList()
	return template.HTML(out.String())
}

var (
	mdImgRe    = regexp.MustCompile(`!\[([^\]]*)\]\(([^)]+)\)`)
	mdBoldRe   = regexp.MustCompile(`\*\*([^*]+)\*\*`)
	mdItalicRe = regexp.MustCompile(`\*([^*\n]+)\*`)
	mdCodeRe   = regexp.MustCompile("`([^`\n]+)`")
	mdLinkRe   = regexp.MustCompile(`\[([^\]]+)\]\(([^)]+)\)`)
	safeURLRe  = regexp.MustCompile(`^(https?://|/)`)
)

func inlineMD(s string) string {
	s = template.HTMLEscapeString(s)
	// Images : ![alt](url) — uniquement http(s) ou /paths internes.
	s = mdImgRe.ReplaceAllStringFunc(s, func(m string) string {
		sub := mdImgRe.FindStringSubmatch(m)
		if len(sub) < 3 {
			return m
		}
		alt, url := sub[1], sub[2]
		if !safeURLRe.MatchString(url) {
			return template.HTMLEscapeString(alt)
		}
		return fmt.Sprintf(`<figure class="article-figure"><img src="%s" alt="%s" loading="lazy"></figure>`, url, alt)
	})
	s = mdBoldRe.ReplaceAllString(s, "<strong>$1</strong>")
	s = mdItalicRe.ReplaceAllString(s, "<em>$1</em>")
	s = mdCodeRe.ReplaceAllString(s, "<code>$1</code>")
	s = mdLinkRe.ReplaceAllStringFunc(s, func(m string) string {
		sub := mdLinkRe.FindStringSubmatch(m)
		if len(sub) < 3 {
			return m
		}
		url := sub[2]
		if !safeURLRe.MatchString(url) {
			return sub[1]
		}
		return fmt.Sprintf(`<a href="%s" rel="noopener">%s</a>`, url, sub[1])
	})
	return s
}
