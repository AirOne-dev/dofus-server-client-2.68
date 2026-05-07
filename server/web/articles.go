package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"
)

type Article struct {
	ID         int       `json:"id"`
	Slug       string    `json:"slug"`
	Title      string    `json:"title"`
	Excerpt    string    `json:"excerpt"`
	Content    string    `json:"content,omitempty"`
	Author     string    `json:"author"`
	CoverImage string    `json:"coverImage"`
	Tag        string    `json:"tag"`
	Published  bool      `json:"published"`
	CreatedAt  time.Time `json:"createdAt"`
	UpdatedAt  time.Time `json:"updatedAt"`
}

var slugRe = regexp.MustCompile(`^[a-z0-9-]+$`)

func ensureArticlesSchema() error {
	_, err := db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.articles (
		Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
		Slug VARCHAR(160) NOT NULL UNIQUE,
		Title VARCHAR(255) NOT NULL,
		Excerpt VARCHAR(512) NOT NULL DEFAULT '',
		Content MEDIUMTEXT NOT NULL,
		Author VARCHAR(64) NOT NULL DEFAULT 'OneAir',
		CoverImage VARCHAR(512) NOT NULL DEFAULT '',
		Tag VARCHAR(64) NOT NULL DEFAULT '',
		Published TINYINT NOT NULL DEFAULT 1,
		CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
		UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		KEY ix_published_created (Published, CreatedAt)
	) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4`)
	if err != nil {
		return err
	}
	var n int
	_ = db.QueryRow("SELECT COUNT(*) FROM " + cfg.WorldDB + ".articles").Scan(&n)
	if n == 0 {
		_, _ = db.Exec(`INSERT INTO `+cfg.WorldDB+`.articles
			(Slug, Title, Excerpt, Content, Author, Tag) VALUES (?, ?, ?, ?, ?, ?)`,
			"bienvenue-sur-oneair",
			"Bienvenue dans les Douze",
			"OneAir, c'est Dofus 2.68 sans pay-to-win, sans serveur saturé, et avec un Havre-Sac qui marche vraiment. Ouverture des portes.",
			welcomeArticle,
			"OneAir",
			"annonce")
	}
	return nil
}

const welcomeArticle = `# Bienvenue dans les Douze

Salutations, aventurier. **OneAir** est un serveur Dofus privé, gratuit, ouvert et tenu par des passionnés. Notre charte est simple :

- Pas de pay-to-win, jamais
- Une économie cohérente, des taux modérés
- Le contenu 2.68 entièrement jouable : Frigost, Pandala, Otomaï, Île des Wabbits, Île des Dragoeufs

## Ce qui marche déjà

- Création de compte (jusqu'à **5 personnages** par compte)
- Toutes les classes principales (Iop, Crâ, Eniripsa, Sadida, Sacrieur, Xélor, Écaflip…)
- Donjons et zones de farming en groupe
- **Havre-Sac** complet : entrée/sortie, coffre lié à la banque, zaaps personnels, personnalisation des meubles, loterie quotidienne
- Système de zaaps avec découverte automatique en cliquant sur les zaaps du monde

## Comment commencer

1. Téléchargez le client macOS via le bouton en haut de cette page
2. Lancez l'application — elle s'ouvre directement sur le launcher OneAir
3. Créez votre compte depuis l'écran de connexion
4. Choisissez le serveur **OneAir** et lancez l'aventure

Le forum, le Discord et les tournois inter-guildes arrivent. En attendant, croisez le fer dans les Douze !

— L'équipe OneAir`

func handleArticleAPI(w http.ResponseWriter, r *http.Request) {
	_ = ensureArticlesSchema()
	slug := strings.TrimPrefix(r.URL.Path, "/api/public/articles")
	slug = strings.TrimPrefix(slug, "/")
	slug = strings.TrimSuffix(slug, "/")
	if slug == "" {
		listPublicArticles(w, r)
		return
	}
	if !slugRe.MatchString(slug) {
		http.Error(w, "slug invalide", 400)
		return
	}
	a, err := loadArticleBySlug(slug)
	if err == sql.ErrNoRows {
		http.Error(w, "introuvable", 404)
		return
	}
	if err != nil {
		httpErr(w, err)
		return
	}
	if !a.Published {
		http.Error(w, "non publié", 404)
		return
	}
	writeJSON(w, a)
}

func listPublicArticles(w http.ResponseWriter, r *http.Request) {
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	if limit <= 0 || limit > 50 {
		limit = 12
	}
	offset, _ := strconv.Atoi(r.URL.Query().Get("offset"))
	rows, err := db.Query(`SELECT Id, Slug, Title, Excerpt, Author, CoverImage, Tag, CreatedAt, UpdatedAt
		FROM `+cfg.WorldDB+`.articles WHERE Published = 1
		ORDER BY CreatedAt DESC LIMIT ? OFFSET ?`, limit, offset)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()
	out := []Article{}
	for rows.Next() {
		var a Article
		if err := rows.Scan(&a.ID, &a.Slug, &a.Title, &a.Excerpt, &a.Author,
			&a.CoverImage, &a.Tag, &a.CreatedAt, &a.UpdatedAt); err != nil {
			continue
		}
		a.Published = true
		out = append(out, a)
	}
	writeJSON(w, out)
}

func loadArticleBySlug(slug string) (*Article, error) {
	var a Article
	var pub int
	err := db.QueryRow(`SELECT Id, Slug, Title, Excerpt, Content, Author, CoverImage, Tag, Published, CreatedAt, UpdatedAt
		FROM `+cfg.WorldDB+`.articles WHERE Slug = ?`, slug).
		Scan(&a.ID, &a.Slug, &a.Title, &a.Excerpt, &a.Content, &a.Author,
			&a.CoverImage, &a.Tag, &pub, &a.CreatedAt, &a.UpdatedAt)
	if err != nil {
		return nil, err
	}
	a.Published = pub != 0
	return &a, nil
}

func apiAdminArticles(w http.ResponseWriter, r *http.Request) {
	_ = ensureArticlesSchema()
	switch r.Method {
	case "GET":
		rows, err := db.Query(`SELECT Id, Slug, Title, Excerpt, Content, Author, CoverImage, Tag, Published, CreatedAt, UpdatedAt
			FROM ` + cfg.WorldDB + `.articles ORDER BY CreatedAt DESC LIMIT 200`)
		if err != nil {
			httpErr(w, err)
			return
		}
		defer rows.Close()
		out := []Article{}
		for rows.Next() {
			var a Article
			var pub int
			if err := rows.Scan(&a.ID, &a.Slug, &a.Title, &a.Excerpt, &a.Content, &a.Author,
				&a.CoverImage, &a.Tag, &pub, &a.CreatedAt, &a.UpdatedAt); err != nil {
				continue
			}
			a.Published = pub != 0
			out = append(out, a)
		}
		writeJSON(w, out)
	case "POST":
		var req struct {
			Action     string `json:"action"`
			ID         int    `json:"id"`
			Slug       string `json:"slug"`
			Title      string `json:"title"`
			Excerpt    string `json:"excerpt"`
			Content    string `json:"content"`
			Author     string `json:"author"`
			CoverImage string `json:"coverImage"`
			Tag        string `json:"tag"`
			Published  bool   `json:"published"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			http.Error(w, "bad json", 400)
			return
		}
		switch req.Action {
		case "create":
			req.Slug = strings.TrimSpace(strings.ToLower(req.Slug))
			if req.Slug == "" {
				req.Slug = slugify(req.Title)
			}
			if !slugRe.MatchString(req.Slug) {
				http.Error(w, "slug invalide (a-z, 0-9, tiret)", 400)
				return
			}
			if strings.TrimSpace(req.Title) == "" || strings.TrimSpace(req.Content) == "" {
				http.Error(w, "titre et contenu requis", 400)
				return
			}
			if req.Author == "" {
				if s := currentPlayer(r); s != nil {
					req.Author = s.Username
				} else {
					req.Author = "admin"
				}
			}
			pub := 1
			if !req.Published {
				pub = 0
			}
			_, err := db.Exec(`INSERT INTO `+cfg.WorldDB+`.articles
				(Slug, Title, Excerpt, Content, Author, CoverImage, Tag, Published)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
				req.Slug, req.Title, req.Excerpt, req.Content, req.Author,
				req.CoverImage, req.Tag, pub)
			if err != nil {
				httpErr(w, err)
				return
			}
			writeJSON(w, map[string]any{"ok": true, "slug": req.Slug})
		case "update":
			if req.ID == 0 {
				http.Error(w, "id requis", 400)
				return
			}
			pub := 1
			if !req.Published {
				pub = 0
			}
			_, err := db.Exec(`UPDATE `+cfg.WorldDB+`.articles SET
				Slug = COALESCE(NULLIF(?,''), Slug),
				Title = COALESCE(NULLIF(?,''), Title),
				Excerpt = ?, Content = COALESCE(NULLIF(?,''), Content),
				Author = COALESCE(NULLIF(?,''), Author),
				CoverImage = ?, Tag = ?, Published = ?
				WHERE Id = ?`,
				req.Slug, req.Title, req.Excerpt, req.Content, req.Author,
				req.CoverImage, req.Tag, pub, req.ID)
			if err != nil {
				httpErr(w, err)
				return
			}
			writeJSON(w, map[string]string{"ok": "1"})
		case "delete":
			if req.ID == 0 {
				http.Error(w, "id requis", 400)
				return
			}
			_, err := db.Exec(`DELETE FROM `+cfg.WorldDB+`.articles WHERE Id = ?`, req.ID)
			if err != nil {
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

var (
	nonSlugRe      = regexp.MustCompile(`[^a-z0-9-]+`)
	dashCollapseRe = regexp.MustCompile(`-+`)
)

func slugify(s string) string {
	s = strings.ToLower(strings.TrimSpace(s))
	repl := strings.NewReplacer(
		"à", "a", "â", "a", "ä", "a",
		"é", "e", "è", "e", "ê", "e", "ë", "e",
		"î", "i", "ï", "i",
		"ô", "o", "ö", "o",
		"ù", "u", "û", "u", "ü", "u",
		"ç", "c", "ñ", "n", "œ", "oe",
		" ", "-", "_", "-", "'", "-", "&", "et",
	)
	s = repl.Replace(s)
	s = nonSlugRe.ReplaceAllString(s, "")
	s = dashCollapseRe.ReplaceAllString(s, "-")
	s = strings.Trim(s, "-")
	if len(s) > 100 {
		s = s[:100]
	}
	if s == "" {
		s = fmt.Sprintf("article-%d", time.Now().Unix())
	}
	return s
}
