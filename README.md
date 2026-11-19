# Report Designer — designer web de rapports PDF pour .NET

Alternative .NET à **JasperReports / iReport** : un designer web par **bandes**, sur le modèle de Jasper,
pour concevoir des modèles de rapport en glisser-déposer, et **ReportDesigner.Pdf**, un moteur PDF qui les
génère sans dépendre de QuestPDF. Il s'appuie sur SkiaSharp et HarfBuzz, sous licence MIT : aucune
contrainte de chiffre d'affaires.

![Designer](docs/designer.png)

## Démarrage

Prérequis : [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/ReportDesigner.Web
# puis ouvrir http://localhost:5142
```

Deux modèles d'exemple sont fournis :

- **facture** : facture avec en-tête, lignes, totaux et pied de page ;
- **ventes-par-region** : liste groupée par région, avec sous-totaux et total général.

La facture montre aussi une désignation **extensible** : la ligne s'agrandit pour afficher un texte long.

Cliquez sur **▶ Aperçu PDF** ou appuyez sur <kbd>Ctrl</kbd>+<kbd>Entrée</kbd> pour générer le PDF.

## Structure

| Projet | Rôle |
|---|---|
| `src/ReportDesigner.Core` | Modèle JSON du rapport, moteur d'expressions, pagination par bandes et interface `IReportRenderer`. Aucune dépendance PDF. |
| `src/ReportDesigner.Pdf` | **Moteur PDF par défaut**, qui remplace QuestPDF : polices, mise en page du texte, dessin (voir ci-dessous). |
| `src/ReportDesigner.QuestPdf` | Ancien moteur QuestPDF, **optionnel** : il sert aux tests de comparaison et n'est pas référencé par l'application web. |
| `src/ReportDesigner.Web` | API ASP.NET Core (minimal API) et designer web (HTML/CSS/JS sans framework ni build) dans `wwwroot`. |
| `tests/ReportDesigner.Tests` | Tests xUnit : expressions, pagination, mise en page du texte, polices, rendu PDF, comparaison des deux moteurs et API. |

## Le designer

- **Bandes** comme dans Jasper : Titre, En-tête de page, En-tête de colonnes (répété sur chaque page),
  En-tête de groupe, Détail (une fois par ligne), Pied de groupe, Résumé, Pied de page.
- **Éléments** : texte et expressions, champ, image (PNG/JPEG intégrée au modèle), rectangle,
  lignes horizontale et verticale, numéro de page, date.
- Placement **au point près** : glisser-déposer, redimensionnement par 8 poignées, grille magnétique,
  règle, zoom, sélection multiple (<kbd>Maj</kbd>+clic ou sélection par zone), outils d'alignement.
  Pour déplacer un élément d'une bande à une autre, faites-le glisser vers la bande voulue.
- **Champs** déduits automatiquement des données d'exemple. Faites-les glisser dans une bande :
  - dans l'en-tête de colonnes, un libellé est créé ;
  - dans le résumé ou un pied de groupe, un champ numérique devient `SUM(...)`.
- **Propriétés** : police, taille, gras, italique, souligné, couleurs, alignements, bordures, arrondis,
  marges intérieures, comportement si le texte est trop long (tronquer ou réduire), césure ; format de page,
  orientation, marges, culture, langue, PDF/A.
- **Éléments extensibles** : un texte long agrandit sa bande au lieu d'être tronqué (équivalent de
  « Stretch with overflow » dans Jasper). Les éléments situés dessous descendent, et les cadres et
  séparateurs verticaux extensibles s'étirent avec la bande.
- **Conditions** : « Afficher si » sur un élément, « Imprimer si » sur une bande (ex. `remise > 0`,
  `$PAGE > 1`). Les couleurs et les images peuvent être calculées par une expression.
- **Groupes imbriqués** : autant de niveaux que nécessaire (ex. région, puis vendeur), chacun avec son
  en-tête, son pied et l'option « nouvelle page ».
- **Données d'exemple** (onglet « Données ») enregistrées avec le modèle pour l'aperçu.
- Annuler et rétablir, copier, coller, dupliquer ; enregistrement sur le serveur, import et export JSON.

## Expressions

Tout texte peut contenir des expressions `{expression}` ou `{expression:format}`. Le format est un format
.NET standard, appliqué avec la culture du rapport (par défaut `fr-FR`).

| Expression | Résultat |
|---|---|
| `{nom}`, `{client.adresse.ville}`, `{[nom complet]}` | champ de la ligne courante (chemin pointé, crochets si le nom contient des espaces) |
| `{montant:N2}`, `{date:dd/MM/yyyy}` | valeur formatée |
| `{$P.titre}` | paramètre du rapport |
| `{qte * prix:N2}`, `{nom + ' (' + ville + ')'}` | calculs `+ - * / %` et concaténation de textes |
| `{IF(montant > 1000, 'Gros', 'Petit')}` | conditions : `= != < <= > >=`, `and or not` |
| `{SUM(qte * prix):N2}` | agrégat sur une expression : `SUM AVG MIN MAX COUNT COUNT_DISTINCT FIRST LAST` |
| `{SUM(montant, 'report')}`, `{SUM(montant, 'group1')}` | portée explicite : rapport entier ou niveau de groupe |
| `{$PAGE}`, `{$PAGES}` | numéro de page et nombre total de pages (utilisables dans des calculs) |
| `{$DATE}`, `{$NOW:HH:mm}` | date et heure de génération |
| `{$ROW}`, `{$GROUP_ROW}`, `{$GROUP}`, `{$GROUP1}`… | numéro de ligne, valeur du groupe courant ou d'un niveau |
| `{actif:Oui\|Non}` | libellés d'un booléen |
| `{{` `}}` | accolades littérales |

Par défaut, les agrégats portent sur le groupe courant (bandes de groupe et détail), sinon sur tout le rapport.

**Fonctions** :
- conditions : `IF COALESCE ISNULL ISEMPTY` ;
- nombres : `ROUND FLOOR CEILING ABS MIN MAX NUMBER` ;
- texte : `UPPER LOWER TRIM LEN LEFT RIGHT SUBSTR REPLACE CONTAINS STARTSWITH ENDSWITH CONCAT FORMAT` ;
- dates : `DATE YEAR MONTH DAY ADDDAYS ADDMONTHS DATEDIFF`.

Une division par zéro donne une valeur vide. Une expression invalide est signalée avec sa position.

## Données

Les données sont du JSON, dans l'un de ces formats :

```jsonc
[ { "nom": "Alice", "montant": 12.5 }, ... ]                        // lignes seules
{ "parameters": { "titre": "Ventes" }, "rows": [ { ... }, ... ] }   // paramètres + lignes
```

Pour les groupes, ajoutez des niveaux dans **Groupes** (propriétés du rapport). Chaque niveau a pour clé un
champ ou une expression, par exemple `region` ou `YEAR(date)`. Les groupes suivent l'ordre de première
apparition : les données n'ont pas besoin d'être triées. Les anciens modèles qui utilisent `groupBy` sont
convertis automatiquement.

## API

| Méthode | URL | Description |
|---|---|---|
| `GET` | `/api/templates` | liste des modèles |
| `GET` / `PUT` / `DELETE` | `/api/templates/{nom}` | lire, enregistrer ou supprimer un modèle |
| `POST` | `/api/render` | `{ "template": {...}, "data": ... }` → PDF (aperçu d'un modèle non enregistré) |
| `POST` | `/api/reports/{nom}` | corps = vos données JSON → PDF du modèle enregistré (`?download` pour un fichier joint) |
| `GET` | `/api/reports/{nom}` | PDF du modèle avec ses données d'exemple |

```bash
curl -X POST http://localhost:5142/api/reports/facture \
     -H "Content-Type: application/json" \
     -d @ma-facture.json -o facture.pdf
```

Les erreurs de modèle (expression inconnue, couleur invalide, bande plus haute qu'une page…) sont
renvoyées en `422` au format *problem details* et affichées dans le designer.

## Le moteur PDF : ReportDesigner.Pdf

Une bibliothèque autonome qui remplace QuestPDF pour ce type de rapports.

| Composant | Rôle |
|---|---|
| `ReportPaginator` (Core) | Pagination géométrique des bandes : en-têtes et pieds répétés, en-tête de colonnes répété, en-tête de groupe jamais seul en bas de page, aucune bande coupée. Le nombre total de pages est connu avant le dessin. |
| `FontLibrary` | Lato intégrée (police par défaut, licence OFL), polices enregistrées, polices système, et **police de secours** pour les caractères absents (arabe, symboles…). Gras et italique simulés si la variante manque. |
| `TextLayoutEngine` / `TextShaper` | Mise en forme HarfBuzz (crénage, formes arabes), retour à la ligne au mot, **césure**, mots trop longs coupés, troncature « … », réduction pour tenir, justification, soulignement. |
| `Hyphenator` | Césure par l'algorithme de Liang (celui de TeX), avec les motifs hyph-utf8 pour le français et l'anglais. |
| `Bidi` / `BidiReorder` | **Algorithme bidirectionnel Unicode (UAX #9)** caractère par caractère : incorporations et isolats explicites, nombres et séparateurs, paires de parenthèses, inversion des parenthèses en miroir. |
| `BandStretcher` (Core) | Agrandissement des bandes contenant des éléments extensibles ; la hauteur est mesurée par le moteur PDF. |
| `FontSubsetter` | Intègre au PDF **uniquement les glyphes utilisés** (via `hb_subset` de HarfBuzz) : une facture fait environ 32 Ko au lieu d'environ 1 Mo. |
| `PdfReportRenderer` | Assemble le tout. Premier passage : relevé des glyphes utilisés. Second passage : écriture du PDF avec SkiaSharp, en **PDF/A-2b** si demandé. |

Le texte du PDF reste **sélectionnable et recherchable**. Le texte d'origine et la correspondance
glyphe → caractère sont transmis au PDF. Les ligatures facultatives (« fi », « ti ») sont désactivées pour
que le PDF contienne bien « f » + « i », et non le caractère `ﬁ`. Le crénage et les ligatures obligatoires
de l'arabe sont conservés.

**PDF/A** : la case « PDF/A » du rapport, ou `new PdfReportRenderer { PdfA = true }`, produit un PDF/A-2b :
métadonnées XMP `pdfaid`, profil de sortie sRGB, identifiant de document et polices intégrées. Ces
éléments sont vérifiés par les tests. Pour une conformité certifiée, validez vos PDF avec
[veraPDF](https://verapdf.org) : `verapdf --flavour 2b rapport.pdf`.

**Fidélité** : des tests comparent automatiquement les deux moteurs. Sur les modèles d'exemple, un rapport
groupé et un rapport à deux niveaux de groupes, on retrouve le même nombre de pages, les mêmes mots, avec
un écart de position maximal de 0,3 pt. Le moteur QuestPDF, conservé pour ces comparaisons, ne gère ni les
éléments extensibles, ni la césure, ni le PDF/A.

**Performances** (rapport groupé de 2 000 lignes, 51 pages) : environ 270 ms et 102 Ko, contre 220 ms et
135 Ko avec QuestPDF.

### Utilisation dans vos applications

Référencez `ReportDesigner.Pdf`, puis :

```csharp
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Pdf;

var renderer = new PdfReportRenderer();        // réutilisable et thread-safe
renderer.Fonts.RegisterDirectory("fonts");     // optionnel : vos polices .ttf/.otf

var template = ReportJson.Deserialize(File.ReadAllText("templates/facture.json"));
var data = ReportData.FromJson(json);          // ou new ReportData(rows, parameters)
byte[] pdf = renderer.RenderPdf(template, data);
```

Pour revenir à QuestPDF, référencez `ReportDesigner.QuestPdf` et utilisez `new QuestPdfReportRenderer()`
à la place : les deux moteurs implémentent `IReportRenderer`.

## Configuration

`appsettings.json` :

```json
"ReportDesigner": { "TemplatesPath": "templates", "FontsPath": "fonts" }
```

- **Modèles** : fichiers JSON dans `TemplatesPath`, faciles à versionner avec Git.
- **Polices** : déposez des fichiers `.ttf` ou `.otf` dans `fonts/`. Ils sont enregistrés au démarrage,
  utilisables par leur nom de famille et proposés dans le designer. Lato, intégrée au moteur, est toujours
  disponible. Les polices installées sur le serveur sont aussi utilisables.
  Le designer charge Lato depuis Google Fonts pour que le canevas ressemble au PDF ; hors ligne, il utilise
  une police système, ce qui ne modifie pas le PDF.

## Licences

- **Application et moteur par défaut** : SkiaSharp et HarfBuzzSharp sous licence MIT, police Lato sous
  SIL Open Font License 1.1 (voir `src/ReportDesigner.Pdf/Resources/Fonts/OFL.txt`). Aucune restriction
  d'usage commercial.
- **Moteur optionnel `ReportDesigner.QuestPdf`** : QuestPDF est gratuit (licence Community) sous
  1 M$ USD de chiffre d'affaires annuel brut ; au-delà, une licence payante est requise
  (<https://www.questpdf.com/license/>). Seuls les tests l'utilisent.

## Limites actuelles

- Une bande agrandie ne se coupe pas entre deux pages : sa hauteur est limitée à une page, et un texte
  plus long est tronqué.
- Pas de sous-rapports ni de graphiques.
- Une seule source de données JSON par rapport : pas de connexion SQL directe.
- Moteur PDF :
  - bidi : les suites d'isolement de UAX #9 sont approchées par des suites de même niveau, et les classes
    de caractères sont déduites des plages Unicode ;
  - césure : français et anglais uniquement ;
  - PDF/A : niveau B (pas de PDF/A balisé « A »), à valider avec veraPDF ;
  - pas de signature électronique.
