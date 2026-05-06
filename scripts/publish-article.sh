#!/usr/bin/env bash
# =============================================================================
#  publish-article.sh — publie un article OneAir directement en DB.
#
#  Usage : ./scripts/publish-article.sh <fichier.md>
#
#  Format du .md attendu (frontmatter YAML simple en tête) :
#  ----
#  title: Titre de l'article
#  slug: slug-optionnel-sinon-derive-du-titre
#  tag: patch|annonce|event|devblog|fix
#  excerpt: Résumé 1-2 phrases qui apparaît sur la home.
#  ----
#  ## Markdown du contenu
#
#  Tout l'article en Markdown léger (titres ##, listes -, **gras**, *italique*,
#  `code`, [liens](url)). Pas de HTML brut — le renderer côté serveur l'échappe.
#
#  ----------------------------------------------------------------------------
#  UPSERT via INSERT ... ON DUPLICATE KEY UPDATE (Slug est UNIQUE).
#  L'auteur = 1er compte avec Role>=5 trouvé en DB. Published=1 par défaut.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a

if [ $# -lt 1 ] || [ ! -f "$1" ]; then
    echo "Usage: $0 <fichier.md>" >&2
    echo "Voir le commentaire en tête du script pour le format attendu." >&2
    exit 1
fi
SRC="$(realpath "$1")"
WORLD_DB="${MYSQL_WORLD_DB:-giny_world}"

# Récupère l'auteur en DB (1er admin trouvé). Fallback "admin" si rien.
AUTHOR=$(docker exec giny-mysql sh -c \
    'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" giny_auth -Bse \
        "SELECT Username FROM accounts WHERE Role >= 5 ORDER BY Id LIMIT 1"' 2>/dev/null \
    || true)
AUTHOR="${AUTHOR:-admin}"

# Python génère le SQL complet (parsing frontmatter + escape + UPSERT).
SQL=$(/usr/bin/python3 - "$SRC" "$AUTHOR" <<'PY'
import sys, re, pathlib
src_path, author = sys.argv[1], sys.argv[2]
text = pathlib.Path(src_path).read_text(encoding="utf-8")

m = re.match(r"^---\s*\n(.*?)\n---\s*\n(.*)$", text, re.DOTALL)
if not m:
    sys.stderr.write("ERREUR : frontmatter '---\\n...\\n---' introuvable\n")
    sys.exit(2)
front, body = m.group(1), m.group(2).lstrip()

fields = {}
for line in front.splitlines():
    if ":" in line:
        k, v = line.split(":", 1)
        fields[k.strip()] = v.strip()

title = fields.get("title", "").strip()
slug  = fields.get("slug", "").strip()
tag   = fields.get("tag", "devblog").strip()
exc   = fields.get("excerpt", "").strip()

if not title:
    sys.stderr.write("ERREUR : 'title:' requis dans le frontmatter\n")
    sys.exit(2)

if not slug:
    s = re.sub(r"[^a-z0-9]+", "-", title.lower())
    slug = re.sub(r"-+", "-", s).strip("-")[:80] or "article"
if not re.match(r"^[a-z0-9-]+$", slug):
    sys.stderr.write(f"ERREUR : slug '{slug}' invalide (a-z, 0-9, tirets)\n")
    sys.exit(2)

# Echappe les single quotes pour SQL (doublage standard MySQL).
def esc(s): return s.replace("'", "''")

print(f"""SET NAMES utf8mb4;
INSERT INTO oneair_articles (Slug, Title, Excerpt, Content, Author, Tag, Published)
VALUES ('{esc(slug)}', '{esc(title)}', '{esc(exc)}', '{esc(body)}', '{esc(author)}', '{esc(tag)}', 1)
ON DUPLICATE KEY UPDATE
    Title = VALUES(Title), Excerpt = VALUES(Excerpt), Content = VALUES(Content),
    Author = VALUES(Author), Tag = VALUES(Tag), Published = 1, UpdatedAt = NOW();
SELECT CONCAT('  → article id=', Id, ' slug=', Slug, ' published=', Published) AS result
  FROM oneair_articles WHERE Slug = '{esc(slug)}';""")

# Sortie ANSI bonus pour le wrapper bash (stderr donc séparé du SQL).
sys.stderr.write(f"==> Article : {title}\n")
sys.stderr.write(f"    slug   : {slug}\n")
sys.stderr.write(f"    tag    : {tag}\n")
sys.stderr.write(f"    body   : {len(body)} chars\n")
sys.stderr.write(f"    author : {author}\n")
PY
)

echo "$SQL" | docker exec -i giny-mysql sh -c \
    "mysql -uroot -p\"\$MYSQL_ROOT_PASSWORD\" $WORLD_DB"

# On relit le slug depuis le frontmatter pour l'affichage final.
SLUG=$(/usr/bin/python3 -c "
import re, pathlib
text = pathlib.Path('$SRC').read_text(encoding='utf-8')
m = re.match(r'^---\s*\n(.*?)\n---', text, re.DOTALL)
front = m.group(1)
fields = {l.split(':',1)[0].strip(): l.split(':',1)[1].strip() for l in front.splitlines() if ':' in l}
slug = fields.get('slug','').strip()
if not slug:
    title = fields.get('title','').strip()
    s = re.sub(r'[^a-z0-9]+', '-', title.lower())
    slug = re.sub(r'-+', '-', s).strip('-')[:80] or 'article'
print(slug)
")
echo "==> Publié : http://localhost/article/$SLUG"
