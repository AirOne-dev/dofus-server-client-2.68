// d2p.go — extracteur d'archives Ankama .d2p (Dofus 2.x).
//
// Format vérifié contre les bitmap*.d2p locaux (Dofus 2.68) :
//   - 2 bytes magic en début (0x02 0x01)
//   - 24 bytes en fin de fichier : 6 × uint32 BE
//        baseOffset, baseLength, indexOffset, indexLength,
//        propertiesOffset, propertiesCount
//   - À indexOffset, sur indexLength octets :
//        Pour chaque entrée :
//          int16 BE nameLen
//          name (UTF-8)
//          int32 BE fileOffset (relatif à baseOffset)
//          int32 BE fileLength
//
// On extrait tous les fichiers .png/.jpg dans ItemsCacheDir.
package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

func extractD2P(path, outDir string) (int, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, err
	}
	defer f.Close()

	// Magic
	magic := make([]byte, 2)
	if _, err := io.ReadFull(f, magic); err != nil {
		return 0, fmt.Errorf("read magic: %w", err)
	}
	if magic[0] != 0x02 {
		return 0, fmt.Errorf("d2p invalide (magic %x)", magic)
	}

	// Footer (24 derniers octets) — 6 × uint32 BE
	if _, err := f.Seek(-24, io.SeekEnd); err != nil {
		return 0, err
	}
	var baseOffset, baseLength, indexOffset, indexLength, propsOffset, propsCount uint32
	for _, ptr := range []*uint32{&baseOffset, &baseLength, &indexOffset, &indexLength, &propsOffset, &propsCount} {
		if err := binary.Read(f, binary.BigEndian, ptr); err != nil {
			return 0, err
		}
	}
	_ = propsOffset
	_ = propsCount

	// Lit la section index dans son intégralité (taille connue)
	if _, err := f.Seek(int64(indexOffset), io.SeekStart); err != nil {
		return 0, err
	}
	indexBuf := make([]byte, indexLength)
	if _, err := io.ReadFull(f, indexBuf); err != nil {
		return 0, fmt.Errorf("read index: %w", err)
	}

	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return 0, err
	}

	count := 0
	pos := 0
	for pos < len(indexBuf) {
		if pos+2 > len(indexBuf) {
			break
		}
		nameLen := int(binary.BigEndian.Uint16(indexBuf[pos:]))
		pos += 2
		if pos+nameLen+8 > len(indexBuf) {
			break
		}
		name := string(indexBuf[pos : pos+nameLen])
		pos += nameLen
		fileOffset := binary.BigEndian.Uint32(indexBuf[pos:])
		fileLength := binary.BigEndian.Uint32(indexBuf[pos+4:])
		pos += 8

		// Filtre : on ne sort que les images
		if !strings.HasSuffix(name, ".png") && !strings.HasSuffix(name, ".jpg") {
			continue
		}

		// Lit le contenu
		if _, err := f.Seek(int64(baseOffset)+int64(fileOffset), io.SeekStart); err != nil {
			return count, err
		}
		data := make([]byte, fileLength)
		if _, err := io.ReadFull(f, data); err != nil {
			return count, err
		}

		base := filepath.Base(name)
		outPath := filepath.Join(outDir, base)
		if err := os.WriteFile(outPath, data, 0o644); err != nil {
			return count, err
		}
		count++
	}
	return count, nil
}

// extractAssets : extrait des archives d2p (préfixe configurable) dans cacheDir.
func extractAssets(label, d2pDir, prefix, cacheDir string) {
	entries, err := os.ReadDir(d2pDir)
	if err != nil {
		log.Printf("%s: d2p dir not found (%s) — assets non extraits", label, d2pDir)
		return
	}
	var wg sync.WaitGroup
	var mu sync.Mutex
	total := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), prefix) || !strings.HasSuffix(e.Name(), ".d2p") {
			continue
		}
		path := filepath.Join(d2pDir, e.Name())
		wg.Add(1)
		go func(p string) {
			defer wg.Done()
			n, err := extractD2P(p, cacheDir)
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				log.Printf("%s: extract %s failed (%d ok): %v", label, filepath.Base(p), n, err)
			} else {
				log.Printf("%s: extract %s OK (%d files)", label, filepath.Base(p), n)
			}
			total += n
		}(path)
	}
	wg.Wait()
	if total > 0 {
		log.Printf("%s: extracted %d files into %s", label, total, cacheDir)
	}
}

func extractItemAssets(d2pDir, cacheDir string)     { extractAssets("items", d2pDir, "bitmap", cacheDir) }
func extractWorldmapAssets(d2pDir, cacheDir string) { extractAssets("worldmap", d2pDir, "worldmap", cacheDir) }
