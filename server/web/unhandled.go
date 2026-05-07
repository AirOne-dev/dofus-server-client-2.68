package main

import (
	"database/sql"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// ensureUnhandledSchema : la table est normalement créée par le world
// (OneAirUnhandledLogger.EnsureSchema). Ce CREATE IF NOT EXISTS est un
// filet pour le cas où le web boote avant le world.
func ensureUnhandledSchema() error {
	_, err := db.Exec(`CREATE TABLE IF NOT EXISTS ` + cfg.WorldDB + `.unhandled_log (
		Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
		AtUtc DATETIME NOT NULL,
		CharacterId BIGINT NULL,
		CharacterName VARCHAR(64) NULL,
		Category VARCHAR(48) NOT NULL,
		Detail VARCHAR(255) NOT NULL,
		PayloadJson MEDIUMTEXT NULL,
		KEY ix_at (AtUtc),
		KEY ix_cat (Category, AtUtc),
		KEY ix_char (CharacterId, AtUtc)
	) ENGINE=InnoDB`)
	return err
}

type unhandledEntry struct {
	ID            int64     `json:"id"`
	AtUtc         time.Time `json:"atUtc"`
	CharacterID   *int64    `json:"characterId,omitempty"`
	CharacterName string    `json:"characterName,omitempty"`
	Category      string    `json:"category"`
	Detail        string    `json:"detail"`
	Payload       string    `json:"payload,omitempty"`
}

func apiUnhandled(w http.ResponseWriter, r *http.Request) {
	if err := ensureUnhandledSchema(); err != nil {
		httpErr(w, err)
		return
	}

	switch r.Method {
	case "GET":
		apiUnhandledList(w, r)
	case "DELETE":
		apiUnhandledDelete(w, r)
	default:
		http.Error(w, "method", 405)
	}
}

func apiUnhandledList(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	limit, _ := strconv.Atoi(q.Get("limit"))
	if limit <= 0 || limit > 1000 {
		limit = 200
	}
	where := []string{"1=1"}
	args := []any{}
	if c := q.Get("category"); c != "" {
		where = append(where, "Category = ?")
		args = append(args, c)
	}
	if cid := q.Get("characterId"); cid != "" {
		if v, err := strconv.ParseInt(cid, 10, 64); err == nil {
			where = append(where, "CharacterId = ?")
			args = append(args, v)
		}
	}
	if since := q.Get("since"); since != "" {
		if v, err := strconv.ParseInt(since, 10, 64); err == nil {
			where = append(where, "AtUtc >= FROM_UNIXTIME(?)")
			args = append(args, v)
		}
	}
	if id := q.Get("id"); id != "" {
		if v, err := strconv.ParseInt(id, 10, 64); err == nil {
			where = append(where, "Id = ?")
			args = append(args, v)
		}
	}

	sqlQ := `SELECT Id, AtUtc, CharacterId, COALESCE(CharacterName,''), Category, Detail, COALESCE(PayloadJson,'')
		FROM ` + cfg.WorldDB + `.unhandled_log
		WHERE ` + strings.Join(where, " AND ") + `
		ORDER BY Id DESC LIMIT ?`
	args = append(args, limit)
	rows, err := db.Query(sqlQ, args...)
	if err != nil {
		httpErr(w, err)
		return
	}
	defer rows.Close()

	var out []unhandledEntry
	for rows.Next() {
		var e unhandledEntry
		var cid sql.NullInt64
		if err := rows.Scan(&e.ID, &e.AtUtc, &cid, &e.CharacterName, &e.Category, &e.Detail, &e.Payload); err != nil {
			httpErr(w, err)
			return
		}
		if cid.Valid {
			v := cid.Int64
			e.CharacterID = &v
		}
		out = append(out, e)
	}

	if r.URL.Query().Get("format") == "md" {
		w.Header().Set("Content-Type", "text/markdown; charset=utf-8")
		w.Write([]byte(formatUnhandledMarkdown(out, q.Get("category"), q.Get("characterId"))))
		return
	}

	cats := map[string]int{}
	totalRow := db.QueryRow(`SELECT COUNT(*) FROM ` + cfg.WorldDB + `.unhandled_log`)
	var total int
	_ = totalRow.Scan(&total)

	if catRows, err := db.Query(`SELECT Category, COUNT(*) FROM ` + cfg.WorldDB + `.unhandled_log GROUP BY Category`); err == nil {
		defer catRows.Close()
		for catRows.Next() {
			var c string
			var n int
			if err := catRows.Scan(&c, &n); err == nil {
				cats[c] = n
			}
		}
	}

	writeJSON(w, map[string]any{
		"events":     out,
		"total":      total,
		"byCategory": cats,
	})
}

func apiUnhandledDelete(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	where := []string{"1=1"}
	args := []any{}
	if c := q.Get("category"); c != "" {
		where = append(where, "Category = ?")
		args = append(args, c)
	}
	if cid := q.Get("characterId"); cid != "" {
		if v, err := strconv.ParseInt(cid, 10, 64); err == nil {
			where = append(where, "CharacterId = ?")
			args = append(args, v)
		}
	}
	if id := q.Get("id"); id != "" {
		if v, err := strconv.ParseInt(id, 10, 64); err == nil {
			where = append(where, "Id = ?")
			args = append(args, v)
		}
	}
	if q.Get("all") == "1" {
		where = []string{"1=1"}
		args = nil
	}
	res, err := db.Exec(`DELETE FROM `+cfg.WorldDB+`.unhandled_log WHERE `+strings.Join(where, " AND "), args...)
	if err != nil {
		httpErr(w, err)
		return
	}
	n, _ := res.RowsAffected()
	writeJSON(w, map[string]any{"deleted": n})
}

// formatUnhandledMarkdown produit un bloc Markdown self-contained pour
// copier-coller à Claude (chaque event inclut son payload complet).
func formatUnhandledMarkdown(events []unhandledEntry, filterCategory, filterCharacterId string) string {
	var sb strings.Builder
	sb.WriteString("# OneAir — actions non gérées capturées\n\n")
	if filterCategory != "" {
		sb.WriteString("Catégorie filtrée : `" + filterCategory + "`\n")
	}
	if filterCharacterId != "" {
		sb.WriteString("Personnage filtré : `" + filterCharacterId + "`\n")
	}
	sb.WriteString(fmt.Sprintf("Total : %d events\n\n", len(events)))
	sb.WriteString("Tu trouveras ci-dessous les events captés par `OneAirUnhandledLogger.cs` (table `unhandled_log`). " +
		"Chaque event correspond à une action que le client a tenté d'exécuter mais que le serveur Giny.NETCore (branche 2.68) " +
		"ne sait pas gérer aujourd'hui — soit faute de handler, soit faute d'effet implémenté.\n\n")
	sb.WriteString("**Ce que je voudrais que tu fasses :** identifie les patterns dans cette liste et propose les fixes côté serveur " +
		"(ajout d'un `[MessageHandler]`, `[GenericActionHandler]`, `[ItemEffect]`, `[ItemUsageHandler]`, `[SpellEffectHandler]`, ou un " +
		"hook custom OneAir). Pour chaque fix, donne le path du fichier à créer/modifier, le code complet, et le sed Dockerfile " +
		"associé si nécessaire (cf. CLAUDE.md `Workflow patch serveur`).\n\n")

	grouped := map[string][]unhandledEntry{}
	order := []string{}
	for _, e := range events {
		if _, ok := grouped[e.Category]; !ok {
			order = append(order, e.Category)
		}
		grouped[e.Category] = append(grouped[e.Category], e)
	}
	for _, cat := range order {
		evs := grouped[cat]
		sb.WriteString(fmt.Sprintf("## Catégorie `%s` (%d events)\n\n", cat, len(evs)))
		seenDetails := map[string]int{}
		var first []unhandledEntry
		for _, e := range evs {
			if seenDetails[e.Detail] == 0 {
				first = append(first, e)
			}
			seenDetails[e.Detail]++
		}
		for _, e := range first {
			cnt := seenDetails[e.Detail]
			sb.WriteString(fmt.Sprintf("### `%s` (× %d)\n\n", e.Detail, cnt))
			sb.WriteString(fmt.Sprintf("- Captured at: `%s`\n", e.AtUtc.Format(time.RFC3339)))
			if e.CharacterID != nil {
				sb.WriteString(fmt.Sprintf("- Character: `%s` (id=`%d`)\n", e.CharacterName, *e.CharacterID))
			}
			if strings.TrimSpace(e.Payload) != "" {
				sb.WriteString("\n```\n")
				sb.WriteString(e.Payload)
				sb.WriteString("\n```\n\n")
			} else {
				sb.WriteString("\n")
			}
		}
	}

	sb.WriteString("---\n\n")
	sb.WriteString("Pour rappel, les hooks responsables de la capture (et donc les points d'attache Giny) :\n\n")
	sb.WriteString("| Catégorie | Source Giny | Type d'action |\n")
	sb.WriteString("|---|---|---|\n")
	sb.WriteString("| `item_use` | `ItemsManager.UseItem` (no handler) | item consommable sans `[ItemUsageHandler]` |\n")
	sb.WriteString("| `item_use_error` | `ItemsManager.UseItem` (catch) | exception pendant l'invocation |\n")
	sb.WriteString("| `item_effect` | `ItemEffectsManager.AddEffects` | effet d'item équipé sans `[ItemEffect]` |\n")
	sb.WriteString("| `spell_effect` | `DefaultSpellCastHandler.Initialize` | effet de sort sans `[SpellEffectHandler]` |\n")
	sb.WriteString("| `generic_action` | `GenericActionsManager.Handle` | enum `GenericActionEnum.*` sans handler (ex: `Paddock`) |\n")
	sb.WriteString("| `interactive` | `GenericActions.HandleUnhandled` | élément interactif marqué Unhandled |\n")
	sb.WriteString("| `interactive_err` | `MapInstance.UseInteractive` (else) | clic sur élément non dispatchable |\n")
	sb.WriteString("| `npc_action` | `Npc.InteractWith` (else) | NPC cliqué sans NpcActionRecord pour le type |\n")
	sb.WriteString("| `exchange_request` | `ExchangesHandler.HandleExchangePlayerRequestMessage` (default case) | type d'échange non implémenté |\n")
	sb.WriteString("| `net_message` | `WorldClient.OnMessageUnhandled` | message protocole sans `[MessageHandler]` |\n")
	sb.WriteString("| `net_error` | `WorldClient.OnHandlingError` | exception dans un handler protocole |\n")

	return sb.String()
}
