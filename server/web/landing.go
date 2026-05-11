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

// effectiveRate retourne le multiplicateur d'un événement actif (row events
// non expirée), sinon le fallback. Format compact ("1", "10", "1.5").
func effectiveRate(eventType, fallback string) string {
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.events (
		Type VARCHAR(32) NOT NULL PRIMARY KEY,
		Multiplier DOUBLE NOT NULL DEFAULT 1.0,
		ExpiresAt DATETIME NULL,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		UpdatedBy VARCHAR(64) NULL
	) ENGINE=InnoDB`)
	var mul float64
	var exp sql.NullTime
	err := db.QueryRow(`SELECT Multiplier, ExpiresAt FROM `+cfg.WorldDB+`.events WHERE Type = ?`, eventType).
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
		DLMacURL: macDownloadURL(),
		DLWinURL: winDownloadURL(),
		XPRate:   effectiveRate("xp", envOr("XP_RATE", "1")),
		DropRate: effectiveRate("drop", envOr("DROP_RATE", "1")),
		JobRate:  effectiveRate("job", envOr("JOB_RATE", "1")),
	}
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.AuthDB + ".accounts").Scan(&d.Accounts)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".characters").Scan(&d.Chars)
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".online_clients").Scan(&d.Online)

	rows, err := db.Query(`SELECT Id, Slug, Title, Excerpt, Author, CoverImage, Tag, CreatedAt
		FROM ` + cfg.WorldDB + `.articles WHERE Published = 1
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
		FROM `+cfg.WorldDB+`.articles WHERE Published = 1 AND Id <> ?
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

// handleDownloadMac sert le client macOS, dans l'ordre de préférence :
// (1) DOWNLOAD_MACOS_URL externe, (2) zip statique ONEAIR_APP_ZIP avec Range,
// (3) zip on-the-fly du dossier ONEAIR_APP_DIR (~5 GB walk, lent), (4) page d'erreur.
func handleDownloadMac(w http.ResponseWriter, r *http.Request) {
	if u := os.Getenv("DOWNLOAD_MACOS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		http.Redirect(w, r, u, http.StatusFound)
		return
	}

	zipPath := envOr("ONEAIR_APP_ZIP", "/app/build/OneAir-MacOS.zip")
	if st, err := os.Stat(zipPath); err == nil && !st.IsDir() {
		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Content-Disposition", `attachment; filename="OneAir-MacOS.zip"`)
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

// handleDownloadWindows : pendant de handleDownloadMac sur OneAir-Windows/.
func handleDownloadWindows(w http.ResponseWriter, r *http.Request) {
	if u := os.Getenv("DOWNLOAD_WINDOWS_URL"); u != "" && (strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://")) {
		http.Redirect(w, r, u, http.StatusFound)
		return
	}

	zipPath := envOr("ONEAIR_WIN_ZIP", "/app/build/OneAir-Windows.zip")
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

// streamOneAirAppZip streame un .zip Store (pas de compression : le bundle
// fait ~5 GB d'assets déjà compressés) sans buffer disque.
func streamOneAirAppZip(w http.ResponseWriter, r *http.Request, appDir, fileName string) {
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s"`, fileName))
	w.Header().Set("X-Content-Type-Options", "nosniff")

	zw := zip.NewWriter(w)
	defer zw.Close()

	rootName := filepath.Base(appDir)
	walkErr := filepath.Walk(appDir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			log.Printf("download zip: walk error on %s: %v", path, err)
			return nil
		}
		rel, err := filepath.Rel(appDir, path)
		if err != nil {
			return nil
		}
		entryName := rootName
		if rel != "." {
			entryName = filepath.Join(rootName, rel)
		}

		if info.IsDir() {
			// Entrées de dossier explicites pour préserver les permissions/exec.
			if rel == "." {
				return nil
			}
			h, _ := zip.FileInfoHeader(info)
			h.Name = entryName + "/"
			h.Method = zip.Store
			_, err := zw.CreateHeader(h)
			return err
		}

		// Symlinks : on stocke la cible comme contenu et on conserve le mode
		// (le .app en contient dans Frameworks/).
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
		h.Method = zip.Store
		fw, err := zw.CreateHeader(h)
		if err != nil {
			return err
		}
		f, err := os.Open(path)
		if err != nil {
			return nil
		}
		defer f.Close()
		_, err = io.Copy(fw, f)
		return err
	})
	if walkErr != nil {
		log.Printf("download zip: walk failed: %v", walkErr)
	}
}

func apiPublicStatus(w http.ResponseWriter, r *http.Request) {
	_, _ = db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.online_clients (
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
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".online_clients").Scan(&out.Online)
	writeJSON(w, out)
}

// renderMarkdown : Markdown minimal sécurisé (HTML-escape avant
// remplacement, liens http(s) ou /-relatifs uniquement). Supporte titres
// h1-h3, paragraphes, listes -, gras, italique, code inline, liens, images.
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
