# Palisades — Todo List

## ✅ FAIT — Base (Moteur + Survie)

- [x] Fenêtre WPF sans bordure (WindowStyle=None)
- [x] Suppression du Alt+Tab (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
- [x] Conteneurs visibles au-dessus du bureau
- [x] Bordure semi-transparente #25FFFFFF → #45FFFFFF au hover
- [x] Coins arrondis (CornerRadius personnalisable)
- [x] Ombre portée (DropShadowEffect)
- [x] Mica backdrop (Windows 11 Fluent)
- [x] Scrollbar fine style Windows 11
- [x] Drag & drop de fichiers depuis le bureau
- [x] Refonte Overlay unique plein écran
- [x] HWND_BOTTOM en Z-order
- [x] WndProc : blocage de SC_SHOWDESKTOP, SC_MINIMIZE, WM_WINDOWPOSCHANGING, WM_SHOWWINDOW, WM_ACTIVATEAPP
- [x] WndProc : renforcement HWND_BOTTOM via WM_WINDOWPOSCHANGED

## ✅ FAIT — Systray & Raccourcis

- [x] NotifyIcon dans la barre d'état (Open, New Container, Show/Hide, Toggle Icons, Install Context Menu, Exit)
- [x] Raccourci global : Win+Shift+H (afficher/masquer tout)
- [x] Raccourci global : Win+Shift+N (nouveau conteneur)

## ✅ FAIT — Interaction Utilisateur

- [x] Clic droit sur conteneur → menu contextuel
- [x] Clic droit sur icône → menu Windows original (ShellContextMenu)
- [x] Double-clic sur icône → lancer le raccourci
- [x] Redimensionnement par poignées (8 directions)
- [x] Déplacement par glisser (header)
- [x] Verrouillage position/taille (Lock)
- [x] Drag & drop pour ajouter des raccourcis
- [x] Suppression d'un conteneur (menu contextuel + bouton)
- [x] Création par tracé (drag-to-create) : Clic + dessiner rectangle
- [x] Snap / Magnétisme : Alignement auto entre boîtes + bords d'écran (seuil 25px, bypass Alt)
- [x] Clamp drag : Empêcher les conteneurs d'être glissés hors-écran (marge 40px)
- [x] Recenter Box : Bouton dans Edit Properties pour replacer au centre

## ✅ FAIT — Auto-Hide (Rideau)

- [x] Animation rideau (timer 30ms, 12 frames)
- [x] Hauteur minimale = 40px (header)
- [x] _suppressSave pour ne pas sauvegarder l'anim
- [x] Timer délai avant repli + annulation au survol
- [x] RestoreFullHeight à l'arrêt
- [x] Fix FullHeight : Ne plus écraser _fullHeight si hauteur < 150px
- [x] Rideau fermé au démarrage

## ✅ FAIT — Design & Apparence

- [x] Opacité = transparence (WS_EX_LAYERED + SetLayeredWindowAttributes)
- [x] Couleurs personnalisables (header, body, titre, labels)
- [x] Style Fluent Design (Segoe UI, semi-bold)
- [x] Icônes extraites avec PathToImageConverter + cache + SHGetFileInfo
- [x] Thème global (Dark, Light, Frost, Glass, etc.)
- [x] Palette de 10 couleurs prédéfinies (quick-pick dans Edit Properties)
- [x] Opacité dynamique Idle vs Hover (29% → 41%, transition 150ms)
- [x] Sélecteur de police (dropdown + taille)
- [x] Bordure activable/désactivable
- [x] Coins arrondis activables/désactivables
- [x] Flèches de raccourci (overlay Windows)
- [x] Ombre sous le texte (DropShadowEffect)
- [x] Auto-hide au bord d'écran
- [x] Boutons Show All / Hide All

## ✅ FAIT — Filtres & Organisation

- [x] Filtre : All, Programs, Documents, Folders, Custom (regex)
- [x] Click / Double-click pour ouvrir (option configurable)

## ✅ FAIT — Menu contextuel du Bureau

- [x] Installation du registre pour clic droit → "Create Palisades Container"
- [x] Gestion de l'argument `--create-container`

## ✅ FAIT — Folder Portal

- [x] Conteneur miroir d'un dossier réel
- [x] Sélecteur de dossier à la création
- [x] Populate shortcuts (fichiers + sous-dossiers)
- [x] FileSystemWatcher pour synchro temps réel
- [x] Bouton "Change Folder" dans Edit Properties
- [x] Affichage du FolderPortalPath dans les propriétés
- [x] Sync All pour Folder Portals

## ✅ FAIT — Stabilité & Bugs Corrigés

- [x] Cache d'icônes (ConcurrentDictionary)
- [x] Gestion d'erreurs try/catch partout
- [x] Journalisation des crashs (crash.log)
- [x] **ShellContextMenu — Crash AccessViolation :** Mauvais GUID IContextMenu (`000214F4` → `000214E4`) + `ref IntPtr` au lieu de `IntPtr[]`
- [x] **ShellContextMenu — Disparition au clic overlay :** WH_MOUSE_LL hook pour les clics hors-menu (contourne WS_EX_NOACTIVATE/TRANSPARENT)
- [x] **ShellContextMenu — Auto-dismiss 5s :** SetTimer + EndMenu via HwndSource hook
- [x] **Sauvegarde des options :** Setters écrivaient dans des champs privés au lieu de `_model`
- [x] **Scrollbar visuel :** RepeatButtons avec artéfacts — template vide personnalisé
- [x] **Drag-to-create :** Rubber band actif uniquement quand icônes cachées
- [x] **Redimensionnement libre :** Poignées avec Grid.RowSpan="2" + Fill="Transparent" + 6px/12px
- [x] **Snap-to-grid :** Alignement magnétique sur une grille virtuelle
- [x] **Anti-collision :** Empêcher les conteneurs de se chevaucher
- [x] **Override Shift :** Maintenir Shift pour désactiver snap/collision
- [x] **Taskbar click fix :** `IsDesktopPoint` évite `GetAncestor(GA_ROOT)`, appliqué seulement au DOWN, WM_ACTIVATEAPP débloqué, HWND_BOTTOM forcé via WndProc + timer, pas de parent Progman
- [x] **Win+D fix :** `ImmunizeAgainstWinD` (GWLP_HWNDPARENT → desktop) + `WorkArea` au lieu de `Bounds` → overlay survit à Win+D
- [x] **Rubber band + multi-select + drag dans conteneur :** remplacement de GongSolutions par hook souris custom, sélection 2D, insertion marker visuel
- [x] **Drag icons bureau → conteneur avec insertion marker** : marker visuel aussi visible depuis l'overlay

## ✅ FAIT — Multi-écran & Disposition

- [x] Gestion multi-écran : Mémoriser positions par résolution + restauration automatique
- [x] Resize to icon multiples (Snap to Grid — 60px/cell)

## ✅ FAIT — Stabilité

- [x] Détection de redémarrage explorer.exe (timer 5s + réapplication reparenting)
- [x] Détection de changement de résolution + repositionnement overlay
- [x] Gestion des droits admin (log)

## ✅ FAIT — Menu contextuel Bureau

- [x] Icône personnalisée dans le menu contextuel du bureau

## ✅ FAIT — Moteur de tri automatique

- [x] Surveillance du bureau (FileSystemWatcher sur DesktopDirectory)
- [x] Interface de configuration par conteneur (checkboxes 8 catégories)
- [x] Catégories complètes : Documents, Images, Vidéos, Musique, Archives, Programs (Exécutables), Links (Web), Dossiers
- [x] Appliquer aux non-assignés / Appliquer à tous (Sort Unassigned / Sort All dans les propriétés)
- [x] Compteur d'icônes dans le titre (ShowCounter, format "Nom (N)")
- [x] Keep originals after sort (check par conteneur, supprime le .lnk du bureau si décoché)
- [x] Notification toast nouveau fichier trié (via TrayService.ShowNotification)
- [x] **Toggle global "Tri auto des nouveaux"** dans le menu principal (MainWindow) — start/stop AutoSortManager, persisté dans DefaultModel

## ✅ FAIT — Clichés (Snapshots)

- [x] Moteur de snapshots : sauvegarde complète de la config (containers + thème + notes)
- [x] Historique : Nom, Date, Type, Options (Restore / Rename / Delete)
- [x] Snapshot auto sur changement de résolution (SystemEvents.DisplaySettingsChanged)
- [x] Restaurer / Renommer / Supprimer
- [x] Notes incluses dans les snapshots (SnapshotModel.Notes)

## ✅ FAIT — Améliorations Demandées

- [x] **Sync All pour Folder Portals** — Bouton "Sync All Portals" dans les propriétés, rafraîchit tous les dossiers miroirs
- [x] **Appliquer les options à tous les conteneurs** — Bouton "Apply Options to All Containers" copie opacité, polices, couleurs, filtres, options
- [x] **Thèmes de conteneurs prédéfinis** — ComboBox ContainerTheme (Custom, Dark, Light, Frost, Glass, Midnight, Amber, Forest, Plum) avec ApplyThemePreset()
- [x] **Filtre live sync ContainerWindow** — CollectionViewSource + FilterShortcut + PropertyChanged refresh (identique à ContainerControl)
- [x] **Option flèche de raccourci** — ShowShortcutArrow (bool) + ShortcutIconMultiConverter pour SHGFI_LINKOVERLAY conditionnel
- [x] **Double-clic titre renommer inline** — BeginEditTitle/CommitEditTitle/CancelEditTitle dans VM, TextBox overlay dans ContainerControl + ContainerWindow
- [x] **Private Box (AES-256)** — EncryptionService (PBKDF2, AES-256-CBC), Set/Lock/Unlock/Remove Password dans UI, lock overlay avec déverrouillage
- [x] **Drag to reorder icons** — gong-wpf-dragdrop + ShortcutReorderHandler (IDropTarget), réordonne ObservableCollection<ShortcutItem>
- [x] **Icônes HD (jumbo 256x256)** — `PathToImageConverter` remplacé par `SHGetImageList` + `IImageList.GetIcon` au lieu de `SHGetFileInfo` 32×32
- [x] **Live preview Default Options complet** — Tous les contrôles (FontSize, HeaderIconSize, TitleHoverEffect, IsLocked, AutoHideOnEdge, OpenOnDoubleClick, UseShellContextMenu, FontFamily, TitleAlignment, AutoHideDelayMs, couleurs) ont leur `Tag` + gestionnaire d'événement pour mise à jour immédiate du conteneur d'aperçu
- [x] **Auto-sort immédiat** — Cocher une catégorie déclenche `CollectDesktopItemsIntoContainer` pour importer les raccourcis du Bureau correspondants dans le conteneur sélectionné
- [x] **"Trier les nouveaux" ciblé** — Le bouton n'importe plus que dans le conteneur sélectionné (pas de distribution globale)
- [x] **UI Trier automatiquement** — Boutons déplacés dans la section "Trier automatiquement" avec texte explicatif, libellés clairs "Importer les raccourcis correspondants" / "Trier par nom"

## ✅ FAIT — Vue Détails & Scroll

- [x] **Vue Détails pour les raccourcis** — Toggle Icônes/Détails dans le menu hamburger (via `ViewMode` + `IsDetailsView`), affiche une liste avec colonnes Icône, Nom, Type, Chemin cible. Même drag-drop + hover que la vue Icônes
- [x] **`ShortcutItem.DisplayType`** — Type affiché (Programme, Dossier, Lien web, Fichier…) basé sur `TargetPath`
- [x] **`EqualsConverter` support Visibility** — Retourne `Visibility.Visible/Collapsed` quand le target type est `Visibility` (utilisé par les bindings de `ViewMode`)
- [x] **Hover dans la vue Détails** — Retiré `Background="{TemplateBinding Background}"` du Border (écrasait les Style triggers). Passage de `#25FFFFFF` à `#30FFFFFF` pour plus de visibilité
- [x] **Molette de souris dans la vue Détails** — Remplacé `ListView` par `ItemsControl` (le `ListView` a un `ScrollViewer` interne qui bouffe les events de molette). Le `ScrollViewer` extérieur gère maintenant tout le scroll
- [x] **Scroll direction inversée** — `IsDirectionReversed="True"` sur le `Track` de la `ScrollBar` personnalisée dans `ContainerControl.xaml` et `ContainerWindow.xaml`
- [x] **Mode d'affichage dans MainWindow** — ComboBox "Mode d'affichage" (Icônes/Détails) dans les options par défaut ET dans les options du conteneur sélectionné
- [x] **`InvertBoolConverter`** — Utilisé pour le binding `IsChecked` du menu "Icônes" (inverse de `IsDetailsView`)
- [x] **`Mode=OneWay` sur les menus IsChecked** — `MenuItem.IsChecked` est en `TwoWay` par défaut, ce qui crashait sur la propriété read-only `IsDetailsView`

## ✅ FAIT — Note Post-it

- [x] Gadget Note (NoteItem + NoteControl) : drag, resize, écriture, couleur, suppression
- [x] Focus hack WS_EX_NOACTIVATE pour TextBox dans les notes
- [x] Persistance dans notes.json, chargement/sauvegarde auto (timer 5s)
- [x] Sauvegarde immédiate au changement de couleur
- [x] Menu ☰ hamburger : 5 tailles de texte (10, 12, 16, 20, 40)
- [x] Double-clic sur le titre pour renommer inline
- [x] Export/Import inclut les notes (GetNotes() depuis la mémoire overlay)
- [x] Snapshots incluent les notes (SnapshotModel.Notes)
- [x] Force-save notes avant export pour garantir données à jour

## ✅ FAIT — Outils

- [x] **Auto-backup config** — Sauvegarde automatique (timer 5min, dossier backup/, garde 20)
- [x] **Export/Import config** — JSON avec containers, defaults, notes
- [x] **Minimize to tray** — Fermer MainWindow → tray, Exit dans le tray pour quitter
- [x] **Merge conteneurs** — Drag header sur un autre → confirmation → fusion
- [x] **Démarrer avec Windows** — Toggle dans menu (HKCU\...\Run)
- [x] **Undo delete shortcut** — Ctrl+Z, LastDeleted + UndoLastDeleteCommand

## ✅ FAIT — Icônes du bureau sur l'overlay

- [x] Affichage des icônes du bureau non-assignées directement sur l'overlay (Canvas, position libre)
- [x] Collection UnassignedShortcuts dans ContainerManager : scanne le bureau, filtre les déjà dans conteneurs
- [x] Double-clic → lance le raccourci
- [x] Clic droit → menu contextuel Windows (ShellContextMenu)
- [x] Drag libre des icônes sur l'overlay (glisser-déposer, snap to grid)
- [x] Multi-sélection (Ctrl+clic, Shift+clic)
- [x] Sélection par zone (dessiner rectangle)
- [x] Déplacer le groupe d'icônes sélectionné (drag groupé)
- [x] Mémoriser positions des icônes dans le fichier de config (par résolution)
- [x] ContainerManager.MoveToContainer / ReturnToUnassigned
- [x] Création initiale ne pré-remplit plus le conteneur "All Apps"
- [x] Hook souris globale (WH_MOUSE_LL) pour intercepter clics sur l'overlay
- [x] **Drag & drop overlay → conteneur** : Déposer des icônes de l'overlay dans un conteneur (MoveToContainer via HandleDragEnd)

## ✅ FAIT — Header iTop-like (Barre de Titre)

- [x] Barre d'outils : Hamburger gauche, titre/search centre, chevron collapse droite
- [x] Zone centrale hybride : TextBlock titre ↔ TextBox recherche au clic
- [x] TextBox recherche design pilule
- [x] Chevron collapse ∨/∧
- [x] Menu Hamburger : Créer, Visualisation, Tri, Règles, Réglages, Rafraîchir, Renommer, Geler, Supprimer

## 📋 À FAIRE — Actions Rapides

- [ ] Toggle icônes bureau : Double-clic fond d'écran
- [ ] Show icons at startup
- [ ] Raccourci global ALT+ù : Panneau de recherche
- [ ] Raccourci global ALT+T : Toggle tous les conteneurs
- [ ] Configuration des hotkeys personnalisables

## 📋 À FAIRE — Icônes du bureau sur l'overlay

- [ ] **Icônes manquantes** : Fallback si PathToImageConverter renvoie null (icône par défaut)
- [ ] **Hover** : Fond léger au survol sur les icônes

## 📋 À FAIRE — Optionnels

- [ ] **Recherche globale** (Ctrl+Espace) — Popup de recherche pour lancer n'importe quel raccourci
- [ ] **Snap to grid** — Alignement magnétique des conteneurs sur une grille
- [ ] **Épingler des raccourcis** — Pin favoris en haut de chaque conteneur
- [ ] **Quick actions** — Clic milieu sur icône → menu d'actions rapides
- [ ] **Fonds d'écran** : Changeur intégré (images, diaporama, couleurs)
- [ ] **Gadgets** : Widgets météo, horloge, calendrier
- [ ] **Assistant IA intégré**
- [ ] **Double-clic bureau magique**

---

**Légende :** `[x]` = Fait | `[ ]` = À faire
