// i18n.go — parser des fichiers i18n_<lang>.d2i (lookup id → texte traduit).
//
// Format vérifié sur le client Dofus 2.x :
//
//   [0..3]   : int32 BE = offset de la section index (pas de magic)
//   [4..]    : section texte (chaînes utf-8 préfixées par leur longueur int16 BE)
//   @offset  : int32 BE indexLen
//              boucle tant que (curseur < offset + 4 + indexLen) :
//                  byte    diacritical (flag)
//                  int32   keyId
//                  int32   textPointer  → string utf-8 (int16 BE prefix) à cet offset
//                  si diacritical != 0 : int32 textPointerDiacritical (ignoré ici)
//
// Sections suivantes (named keys, ordered keys) — non utilisées ici.
package main

import (
	"fmt"
	"os"
)

type I18N map[int32]string

func ParseI18N(path string) (I18N, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if len(data) < 8 {
		return nil, fmt.Errorf("%s: trop court", path)
	}
	cur := int32(0)
	indexOffset := readInt32(data, &cur)
	if int(indexOffset) >= len(data) || indexOffset < 0 {
		return nil, fmt.Errorf("%s: indexOffset %d hors bornes", path, indexOffset)
	}
	cur = indexOffset
	indexLen := readInt32(data, &cur)
	if indexLen < 0 || int(indexOffset+4+indexLen) > len(data) {
		return nil, fmt.Errorf("%s: indexLen %d invalide", path, indexLen)
	}
	end := indexOffset + 4 + indexLen
	out := I18N{}
	for cur < end && int(cur) < len(data)-9 {
		diac := data[cur]
		cur++
		id := readInt32(data, &cur)
		off := readInt32(data, &cur)
		if diac != 0 {
			cur += 4 // skip text pointer pour la version diacritique
		}
		if off < 0 || int(off) >= len(data)-2 {
			continue
		}
		c2 := off
		// Lit la chaîne (int16 BE longueur, puis utf-8) en sécurisant les bornes
		if int(c2)+2 > len(data) {
			continue
		}
		l := int32(uint16(data[c2])<<8 | uint16(data[c2+1]))
		c2 += 2
		if l < 0 || int(c2)+int(l) > len(data) {
			continue
		}
		out[id] = string(data[c2 : c2+l])
	}
	return out, nil
}
