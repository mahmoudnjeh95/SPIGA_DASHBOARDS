# SPIGA Licence Manager - clés USB de licence

SPIGA Dashboards ne s'affiche que si une clé USB de licence est branchée sur le PC.
Sans elle : écran "Licence non détectée". Clé retirée : bandeau d'avertissement,
verrouillage au bout d'1 minute ; rebranchée : l'affichage revient seul.

La clé contient `spiga.lic`, signé avec la clé privée SPIGA et lié au numéro de
série matériel de la clé USB : copié sur une autre clé ou modifié, il est refusé.
Les licences sont permanentes.

## Construire l'exe (un seul fichier, .NET pas nécessaire sur le PC)

```powershell
dotnet publish tools\SpigaLicenseManager -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\licence-manager
```

→ `publish\licence-manager\SpigaLicenseManager.exe` (~70 Mo), à copier sur le PC
qui prépare les clés.

## Préparer une clé

1. Lancer `SpigaLicenseManager.exe`. "Clé privée" doit afficher ✔ (sinon
   "Changer de dossier…" et choisir le dossier qui contient `private.pem`).
2. Brancher la clé USB : elle apparaît seule, avec son numéro de série.
3. Client + ID (proposé automatiquement : SPIGA-001, 002…) → **Préparer la clé**.
4. Message vert "Clé prête… vérifiée" : la clé est livrable avec le Setup.exe.

**Vérifier** contrôle n'importe quelle clé (par ex. une clé retournée par un client).
Chaque clé émise est notée dans `dongles.csv`, à côté de `private.pem`.

## Clé privée

`private.pem` (dossier par défaut `Documents\SPIGA-Licence`).
- La sauvegarder hors ligne (2 copies). Perdue = plus aucune nouvelle clé possible
  sans recompiler SPIGA Dashboards avec une nouvelle clé publique.
- Ne jamais la mettre dans git, ni la laisser sur le PC d'un client.
- La clé publique correspondante est dans
  `src\SpigaDashboards.App\Licensing\LicensePublicKey.cs`.

Utiliser des clés de marque (SanDisk, Kingston…) : une clé sans numéro de série
est refusée. Ne pas formater la clé (le fichier serait perdu ; "Préparer" le recrée).
