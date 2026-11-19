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
  marges intérieures, comportement si le texte est trop long (tronquer ou réduire) ; format de page,
  orientation, marges, culture, regroupement.
- **Données d'exemple** (onglet « Données ») enregistrées avec le modèle pour l'aperçu.
- Annuler et rétablir, copier, coller, dupliquer ; enregistrement sur le serveur, import et export JSON.

## Expressions

Tout texte peut contenir des expressions `{expression}` ou `{expression:format}`. Le format est un format
.NET standard, appliqué avec la culture du rapport (par défaut `fr-FR`).

| Expression | Résultat |
|---|---|
| `{nom}`, `{client.adresse.ville}` | champ de la ligne courante (chemin pointé) |
| `{montant:N2}`, `{date:dd/MM/yyyy}` | valeur formatée |
| `{$P.titre}` | paramètre du rapport |
| `{SUM(montant)}`, `AVG`, `MIN`, `MAX`, `{COUNT()}` | agrégats : sur le groupe dans les bandes de groupe, sur tout le rapport ailleurs |
| `{$PAGE}`, `{$PAGES}` | numéro de page et nombre total de pages |
| `{$DATE}`, `{$NOW:HH:mm}` | date et heure de génération |
| `{$ROW}`, `{$GROUP_ROW}`, `{$GROUP}` | numéro de ligne dans le rapport ou le groupe, valeur du groupe |
| `{actif:Oui\|Non}` | libellés d'un booléen |
| `{{` `}}` | accolades littérales |

## Données

Les données sont du JSON, dans l'un de ces formats :

```jsonc
[ { "nom": "Alice", "montant": 12.5 }, ... ]                        // lignes seules
{ "parameters": { "titre": "Ventes" }, "rows": [ { ... }, ... ] }   // paramètres + lignes
```

Pour les groupes, indiquez le champ dans **Regrouper par** (propriétés du rapport). Les groupes suivent
l'ordre de première apparition : les données n'ont pas besoin d'être triées.

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
| `TextLayoutEngine` / `TextMeasurer` | Mise en forme HarfBuzz (crénage, ligatures, arabe), retour à la ligne au mot, mots trop longs coupés, troncature « … », réduction pour tenir, justification, soulignement. |
| `BidiReorder` | Réordonnancement droite-à-gauche simplifié (UAX #9) pour l'arabe et l'hébreu au sein d'un texte latin, et inversement. |
| `FontSubsetter` | Intègre au PDF **uniquement les glyphes utilisés** (via `hb_subset` de HarfBuzz) : une facture fait environ 32 Ko au lieu d'environ 1 Mo. |
| `PdfReportRenderer` | Assemble le tout. Premier passage : relevé des glyphes utilisés. Second passage : écriture du PDF avec SkiaSharp. |

Le texte du PDF reste **sélectionnable et recherchable**, y compris avec les ligatures (« ti », « fi ») :
le texte d'origine et la correspondance glyphe → caractère sont transmis au PDF.

**Fidélité** : des tests comparent automatiquement les deux moteurs. Sur les modèles d'exemple et sur un
rapport groupé de 4 pages, on retrouve le même nombre de pages, les mêmes mots, avec un écart de position
maximal de 0,3 pt.

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

- Les bandes ont une hauteur fixe : un texte plus long que sa zone est tronqué (« … ») ou réduit, mais ne
  fait pas grandir la bande.
- Les expressions ne gèrent pas l'arithmétique (`qte * prix`) : fournissez les valeurs calculées dans
  les données ou les paramètres.
- Un seul niveau de regroupement ; pas de sous-rapports ni de graphiques.
- Les images sont statiques : elles sont intégrées au modèle et ne proviennent pas d'un champ.
- Une seule source de données JSON par rapport : pas de connexion SQL directe.
- Moteur PDF : le réordonnancement droite-à-gauche est simplifié (pas de caractères de contrôle
  bidirectionnels ni d'inversion des parenthèses), pas de césure, pas de PDF/A ni de signature.
