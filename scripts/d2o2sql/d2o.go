// d2o.go — parser binaire des archives Ankama Dofus 2.x .d2o.
//
// Format (vérifié sur le client Dofus 2.68 local) :
//
//   [0..2]    : magic ASCII "D2O"
//   [3..6]    : int32 BE = offset de la section index (depuis le début du fichier)
//   [7..ix-1] : suite de records (taille variable, sérialisation par classe)
//   [ix..]    : section index puis section classes
//
//   Section index :
//     int32 BE indexLength (= nbRecords * 8)
//     pour chaque record : (id int32 BE, offset int32 BE)
//
//   Section classes (juste après l'index) :
//     int32 BE classCount
//     pour chaque classe :
//       int32 BE  classId
//       utf-8     className   (préfixe int16 BE longueur)
//       utf-8     package
//       int32 BE  fieldCount
//       pour chaque champ :
//         utf-8     fieldName
//         int32 BE  type
//         si type == -99 (Vector) : nom et type interne (récursif)
//         si type == 0..N : pas d'info supplémentaire (référence à une autre classe)
//
//   Chaque record commence par int32 BE classId, suivi des champs de la classe
//   sérialisés dans l'ordre déclaré.
//
// Types primitifs :
//   -1 = int32 BE
//   -2 = bool (1 byte)
//   -3 = string (utf-8 préfixé int16 BE)
//   -4 = number (float64 BE)
//   -5 = i18n key (int32 BE — à résoudre via i18n_*.d2i)
//   -6 = uint32 BE
//   -99 = vector (int32 BE size puis N éléments du type interne)
//   >= 0 = sous-objet (int32 BE classId réel — peut différer en polymorphisme,
//          puis récursivement les champs de la classe résolue)
package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"os"
)

const (
	TypeInt    = -1
	TypeBool   = -2
	TypeString = -3
	TypeNumber = -4
	TypeI18N   = -5
	TypeUInt   = -6
	TypeVector = -99
)

type FieldDef struct {
	Name  string
	Type  int32
	Inner *FieldDef // pour Vector / sous-vecteur récursif
}

type ClassDef struct {
	ID      int32
	Name    string
	Package string
	Fields  []FieldDef
}

type Record map[string]any

type D2O struct {
	path    string
	classes map[int32]*ClassDef
	indexes []indexEntry
	data    []byte
}

type indexEntry struct{ ID, Offset int32 }

func ParseD2O(path string) (*D2O, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", path, err)
	}
	if len(data) < 7 || string(data[:3]) != "D2O" {
		return nil, fmt.Errorf("%s: pas un fichier D2O (magic invalide)", path)
	}
	indexOffset := int32(binary.BigEndian.Uint32(data[3:7]))
	if int(indexOffset) >= len(data) {
		return nil, fmt.Errorf("%s: indexOffset hors fichier", path)
	}

	d := &D2O{path: path, data: data, classes: map[int32]*ClassDef{}}

	cur := indexOffset
	indexLen := readInt32(data, &cur)
	end := indexOffset + 4 + indexLen
	if int(end) > len(data) {
		return nil, fmt.Errorf("%s: indexLen incohérent (%d)", path, indexLen)
	}
	for cur < end {
		id := readInt32(data, &cur)
		off := readInt32(data, &cur)
		d.indexes = append(d.indexes, indexEntry{id, off})
	}

	classCount := readInt32(data, &cur)
	for i := int32(0); i < classCount; i++ {
		cls := readClassDef(data, &cur)
		d.classes[cls.ID] = cls
	}

	return d, nil
}

func readClassDef(data []byte, cur *int32) *ClassDef {
	cls := &ClassDef{}
	cls.ID = readInt32(data, cur)
	cls.Name = readUTF(data, cur)
	cls.Package = readUTF(data, cur)
	fc := readInt32(data, cur)
	for i := int32(0); i < fc; i++ {
		cls.Fields = append(cls.Fields, readFieldDef(data, cur))
	}
	return cls
}

func readFieldDef(data []byte, cur *int32) FieldDef {
	f := FieldDef{}
	f.Name = readUTF(data, cur)
	f.Type = readInt32(data, cur)
	if f.Type == TypeVector {
		// nom de l'élément interne (souvent vide)
		readUTF(data, cur)
		// type de l'élément interne
		inner := FieldDef{Type: readInt32(data, cur)}
		if inner.Type == TypeVector {
			// récursif
			readUTF(data, cur)
			innerInner := FieldDef{Type: readInt32(data, cur)}
			inner.Inner = &innerInner
		}
		f.Inner = &inner
	}
	return f
}

// Records : itère sur tous les enregistrements et appelle cb(id, record).
func (d *D2O) Records(cb func(id int32, rec Record)) error {
	for _, ix := range d.indexes {
		cur := ix.Offset
		classID := readInt32(d.data, &cur)
		cls := d.classes[classID]
		if cls == nil {
			return fmt.Errorf("%s: classe %d inconnue (record %d)", d.path, classID, ix.ID)
		}
		rec, err := d.readRecord(cls, &cur)
		if err != nil {
			return fmt.Errorf("%s: record %d: %w", d.path, ix.ID, err)
		}
		cb(ix.ID, rec)
	}
	return nil
}

func (d *D2O) readRecord(cls *ClassDef, cur *int32) (Record, error) {
	rec := Record{"__class": cls.Name}
	for _, f := range cls.Fields {
		v, err := d.readValue(f, cur)
		if err != nil {
			return nil, fmt.Errorf("field %s: %w", f.Name, err)
		}
		rec[f.Name] = v
	}
	return rec, nil
}

func (d *D2O) readValue(f FieldDef, cur *int32) (any, error) {
	switch f.Type {
	case TypeInt:
		return readInt32(d.data, cur), nil
	case TypeBool:
		b := d.data[*cur] != 0
		*cur++
		return b, nil
	case TypeString:
		return readUTF(d.data, cur), nil
	case TypeNumber:
		v := binary.BigEndian.Uint64(d.data[*cur:])
		*cur += 8
		return math.Float64frombits(v), nil
	case TypeI18N:
		return readInt32(d.data, cur), nil
	case TypeUInt:
		v := readUint32(d.data, cur)
		return v, nil
	case TypeVector:
		size := readInt32(d.data, cur)
		out := make([]any, 0, size)
		inner := *f.Inner
		for i := int32(0); i < size; i++ {
			v, err := d.readValue(inner, cur)
			if err != nil {
				return nil, err
			}
			out = append(out, v)
		}
		return out, nil
	default:
		// Sous-objet : type >= 0 = classId déclaré dans cette même section
		if f.Type < 0 {
			return nil, fmt.Errorf("type non géré %d", f.Type)
		}
		// Le classId réel peut différer (polymorphisme) → on lit un int32 BE
		actualID := readInt32(d.data, cur)
		if actualID == -1 {
			// référence nulle
			return nil, nil
		}
		sub := d.classes[actualID]
		if sub == nil {
			return nil, fmt.Errorf("sous-classe %d inconnue", actualID)
		}
		return d.readRecord(sub, cur)
	}
}

// ---- helpers binaires ------------------------------------------------------

func readInt32(b []byte, cur *int32) int32 {
	v := int32(binary.BigEndian.Uint32(b[*cur:]))
	*cur += 4
	return v
}
func readUint32(b []byte, cur *int32) uint32 {
	v := binary.BigEndian.Uint32(b[*cur:])
	*cur += 4
	return v
}
func readUTF(b []byte, cur *int32) string {
	l := int32(binary.BigEndian.Uint16(b[*cur:]))
	*cur += 2
	if l == 0 {
		return ""
	}
	s := string(b[*cur : *cur+l])
	*cur += l
	return s
}

// ---- pretty-print pour debug -----------------------------------------------

func (d *D2O) DumpFirst(n int, w io.Writer) {
	count := 0
	d.Records(func(id int32, rec Record) {
		if count >= n {
			return
		}
		count++
		fmt.Fprintf(w, "[%d] %v\n", id, rec)
	})
}
