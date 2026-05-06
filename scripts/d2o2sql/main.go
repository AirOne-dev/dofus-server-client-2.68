// d2o2sql — convertit les .d2o du client Dofus local en .sql d'import pour
// le serveur Giny.
//
// Usage :
//   go run ./scripts/d2o2sql \
//      --data  ./OneAir.app/Contents/Resources/data/common \
//      --i18n  ./OneAir.app/Contents/Resources/data/i18n/i18n_fr.d2i \
//      --out   ./server/init-sql
//
// Émet un fichier .sql par mapping connu (cf. mappings.go) sous la forme
// `10-<table>.sql`, `11-<table>.sql`, … (les chiffres assurent que les
// imports passent après les dumps existants 00-03).
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"sort"
)

func main() {
	dataDir := flag.String("data", "", "chemin vers data/common (.d2o)")
	i18nPath := flag.String("i18n", "", "chemin vers i18n_<lang>.d2i (optionnel mais recommandé)")
	outDir := flag.String("out", "./server/init-sql", "dossier de sortie pour les .sql")
	startNum := flag.Int("start", 10, "numéro de départ pour les fichiers (ex: 10-areas.sql)")
	dumpJSON := flag.Bool("json", false, "émet aussi un .json à côté de chaque .sql (debug)")
	listOnly := flag.Bool("list", false, "liste les fichiers d2o trouvés et leur classe principale, ne génère rien")
	only := flag.String("only", "", "ne traite que les fichiers listés (CSV de noms d2o)")
	flag.Parse()

	if *dataDir == "" {
		fmt.Fprintln(os.Stderr, "erreur : --data requis")
		flag.Usage()
		os.Exit(2)
	}

	// Charge i18n si fourni
	var i18n I18N
	if *i18nPath != "" {
		var err error
		i18n, err = ParseI18N(*i18nPath)
		if err != nil {
			log.Printf("WARN i18n: %v — les colonnes Name resteront NULL", err)
		} else {
			log.Printf("i18n: %d clés chargées (parser partiel — colonnes Name peuvent être NULL)", len(i18n))
		}
	}

	if err := os.MkdirAll(*outDir, 0o755); err != nil {
		log.Fatalf("mkdir %s: %v", *outDir, err)
	}

	// Mode liste : inspecte tous les .d2o du dossier
	if *listOnly {
		listD2OFiles(*dataDir)
		return
	}

	onlyMap := map[string]bool{}
	if *only != "" {
		for _, f := range splitCSV(*only) {
			onlyMap[f] = true
		}
	}

	num := *startNum
	mappings := sortedMappings()
	for _, m := range mappings {
		if len(onlyMap) > 0 && !onlyMap[m.File] {
			continue
		}
		path := filepath.Join(*dataDir, m.File)
		if _, err := os.Stat(path); err != nil {
			log.Printf("SKIP %s : non trouvé", m.File)
			continue
		}
		if err := process(path, m.File, m.Mapping, i18n, *outDir, num, *dumpJSON); err != nil {
			log.Printf("ERREUR %s : %v", m.File, err)
			continue
		}
		num++
	}
	log.Printf("✓ Terminé. Fichiers SQL dans %s", *outDir)
}

func process(path, name string, mapping TableMap, i18n I18N, outDir string, num int, dumpJSON bool) error {
	d, err := ParseD2O(path)
	if err != nil {
		return err
	}
	var recs []idRec
	if err := d.Records(func(id int32, r Record) {
		recs = append(recs, idRec{id, r})
	}); err != nil {
		return err
	}
	sort.Slice(recs, func(i, j int) bool { return recs[i].ID < recs[j].ID })

	outPath := filepath.Join(outDir, fmt.Sprintf("%02d-%s.sql", num, mapping.Table))
	f, err := os.Create(outPath)
	if err != nil {
		return err
	}
	defer f.Close()
	if err := writeSQL(f, mapping, recs, i18n); err != nil {
		return err
	}
	log.Printf("✓ %s → %s (%d records)", name, outPath, len(recs))

	if dumpJSON {
		jsonPath := filepath.Join(outDir, fmt.Sprintf("%02d-%s.json", num, mapping.Table))
		if err := writeJSONDump(jsonPath, recs); err != nil {
			log.Printf("  WARN json: %v", err)
		}
	}
	return nil
}

func writeJSONDump(path string, recs []idRec) error {
	f, err := os.Create(path)
	if err != nil {
		return err
	}
	defer f.Close()
	fmt.Fprintln(f, "[")
	for i, r := range recs {
		comma := ","
		if i == len(recs)-1 {
			comma = ""
		}
		fmt.Fprintf(f, "  {\"_id\": %d, \"data\": %s}%s\n", r.ID, jsonOf(r.Rec), comma)
	}
	fmt.Fprintln(f, "]")
	return nil
}

func jsonOf(v any) string {
	b, err := json.Marshal(v)
	if err != nil {
		return "null"
	}
	return string(b)
}

func splitCSV(s string) []string {
	out := []string{}
	cur := ""
	for _, r := range s {
		if r == ',' {
			if cur != "" {
				out = append(out, cur)
			}
			cur = ""
		} else {
			cur += string(r)
		}
	}
	if cur != "" {
		out = append(out, cur)
	}
	return out
}

func listD2OFiles(dir string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		log.Fatalf("read %s: %v", dir, err)
	}
	type info struct {
		name    string
		records int
		classes []string
	}
	var rows []info
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".d2o" {
			continue
		}
		path := filepath.Join(dir, e.Name())
		d, err := ParseD2O(path)
		if err != nil {
			log.Printf("  ! %s : %v", e.Name(), err)
			continue
		}
		rec := 0
		_ = d.Records(func(id int32, r Record) { rec++ })
		var cls []string
		for _, c := range d.classes {
			cls = append(cls, c.Name)
		}
		sort.Strings(cls)
		rows = append(rows, info{e.Name(), rec, cls})
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].name < rows[j].name })
	fmt.Printf("%-40s %8s  %s\n", "FILE", "RECORDS", "CLASSES")
	for _, r := range rows {
		fmt.Printf("%-40s %8d  %s\n", r.name, r.records, joinStrings(r.classes, ", "))
	}
}

func joinStrings(s []string, sep string) string {
	out := ""
	for i, x := range s {
		if i > 0 {
			out += sep
		}
		out += x
	}
	return out
}
