// sql.go — génération des fichiers SQL à partir des records d2o + mappings.
package main

import (
	"encoding/json"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// writeSQL écrit `INSERT IGNORE INTO <table> (...) VALUES (...);` pour chaque
// record, dans le writer fourni.
func writeSQL(w io.Writer, mapping TableMap, recs []idRec, i18n I18N) error {
	fmt.Fprintf(w, "-- Auto-généré par d2o2sql. Ne pas éditer à la main.\n")
	fmt.Fprintf(w, "-- Table : %s · %d records\n\n", mapping.Table, len(recs))
	fmt.Fprintf(w, "SET NAMES utf8mb4;\n")
	fmt.Fprintf(w, "SET FOREIGN_KEY_CHECKS=0;\n")
	if mapping.TruncateBefore {
		fmt.Fprintf(w, "TRUNCATE TABLE `%s`;\n", mapping.Table)
	}
	fmt.Fprintf(w, "\n")

	cols := make([]string, len(mapping.Columns))
	for i, c := range mapping.Columns {
		cols[i] = "`" + c.Col + "`"
	}
	colList := strings.Join(cols, ", ")

	const batchSize = 500
	batch := []string{}
	flush := func() {
		if len(batch) == 0 {
			return
		}
		fmt.Fprintf(w, "INSERT IGNORE INTO `%s` (%s) VALUES\n  %s;\n",
			mapping.Table, colList, strings.Join(batch, ",\n  "))
		batch = batch[:0]
	}

	for _, r := range recs {
		vals := make([]string, len(mapping.Columns))
		for i, col := range mapping.Columns {
			vals[i] = renderValue(col.Field, r, i18n)
		}
		batch = append(batch, "("+strings.Join(vals, ", ")+")")
		if len(batch) >= batchSize {
			flush()
		}
	}
	flush()
	fmt.Fprintf(w, "\nSET FOREIGN_KEY_CHECKS=1;\n")
	return nil
}

type idRec struct {
	ID  int32
	Rec Record
}

// renderValue résout l'expression `field` selon les conventions :
//   "$id"            → r.ID
//   "$i18n:F"        → texte traduit pour r.Rec[F] (id i18n)
//   "$str:F"         → r.Rec[F] forcé en string
//   "$json:F"        → r.Rec[F] sérialisé en JSON
//   "$jsonblob:F"    → idem mais préfixé pour stockage texte
//   "F"              → r.Rec[F] direct
func renderValue(expr string, r idRec, i18n I18N) string {
	if expr == "$id" {
		return strconv.Itoa(int(r.ID))
	}
	if strings.HasPrefix(expr, "$i18n:") {
		key := expr[len("$i18n:"):]
		v, ok := r.Rec[key]
		if !ok {
			return "NULL"
		}
		id, ok := toInt(v)
		if !ok {
			return "NULL"
		}
		s, ok := i18n[int32(id)]
		if !ok {
			return "NULL"
		}
		return sqlString(s)
	}
	if strings.HasPrefix(expr, "$str:") {
		key := expr[len("$str:"):]
		v, ok := r.Rec[key]
		if !ok {
			return "NULL"
		}
		return sqlString(fmt.Sprintf("%v", v))
	}
	if strings.HasPrefix(expr, "$json:") {
		key := expr[len("$json:"):]
		v, ok := r.Rec[key]
		if !ok {
			return "NULL"
		}
		b, _ := json.Marshal(v)
		return sqlString(string(b))
	}
	if strings.HasPrefix(expr, "$jsonblob:") {
		key := expr[len("$jsonblob:"):]
		v, ok := r.Rec[key]
		if !ok {
			return "NULL"
		}
		b, _ := json.Marshal(v)
		return sqlString(string(b))
	}
	v, ok := r.Rec[expr]
	if !ok {
		return "NULL"
	}
	return sqlScalar(v)
}

func sqlScalar(v any) string {
	if v == nil {
		return "NULL"
	}
	switch x := v.(type) {
	case bool:
		if x {
			return "1"
		}
		return "0"
	case int:
		return strconv.Itoa(x)
	case int32:
		return strconv.Itoa(int(x))
	case int64:
		return strconv.FormatInt(x, 10)
	case uint32:
		return strconv.FormatUint(uint64(x), 10)
	case float64:
		return strconv.FormatFloat(x, 'g', -1, 64)
	case string:
		return sqlString(x)
	case []any:
		b, _ := json.Marshal(x)
		return sqlString(string(b))
	case map[string]any:
		b, _ := json.Marshal(x)
		return sqlString(string(b))
	default:
		return sqlString(fmt.Sprintf("%v", v))
	}
}

func sqlString(s string) string {
	// Échappe pour MySQL : \, ', \n, \r, \0, ctrl-Z
	r := strings.NewReplacer(
		`\`, `\\`,
		`'`, `\'`,
		"\x00", `\0`,
		"\n", `\n`,
		"\r", `\r`,
		"\x1a", `\Z`,
	)
	return "'" + r.Replace(s) + "'"
}

func toInt(v any) (int64, bool) {
	switch x := v.(type) {
	case int:
		return int64(x), true
	case int32:
		return int64(x), true
	case int64:
		return x, true
	case uint32:
		return int64(x), true
	case float64:
		return int64(x), true
	}
	return 0, false
}
