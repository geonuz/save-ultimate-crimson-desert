# Journal des modifications

## [1.10.0]

Mis à jour pour Crimson Desert 2.06.00.

### Nouveautés

- **SaveUltimate devient CDSE** (Crimson Desert Save Editor), avec un nouveau logo, de nouvelles couleurs et polices. Le thème par défaut est CDSE Obsidienne ; l'ancien reste disponible sous le nom Charbon et or. Après une mise à jour depuis la 1.9.0, le programme garde le nom `SaveUltimate.exe` jusqu'à la mise à jour suivante, qui le renomme `CDSE.exe`. Les codes d'erreur commencent désormais par `CDSE-` au lieu de `SU-` ; les numéros sont inchangés.
- **Historique :** chaque modification est listée à droite. Cliquez pour revenir à ce moment ou avancer de nouveau (Ctrl+Y). L'enregistrement le conserve.
- **Objets → Base de données des objets :** ajouter des objets ordinaires à l'inventaire, un type ou plusieurs à la fois. Les emplacements nécessaires sont indiqués d'abord ; objets de quête, clés et documents ne peuvent pas être ajoutés.
- **Inventaire :** augmenter la quantité d'un objet non empilable, comme un équipement, ajoute autant de copies séparées avec la même amélioration, les mêmes emplacements et la même teinture.
- **Équipement :** chaque emplacement montre son équipement avec une image, comme pour les familiers, et le sélecteur ne liste que l'équipement abyssal compatible, chacun avec son effet : la valeur d'un équipement de caractéristique (Attaque +2, Vitesse de déplacement +4 %) ou l'effet d'un équipement de compétence, et la description du jeu au survol. Les listes d'objets n'ont plus la colonne Emplacement.
- **Nouveau menu Familiers et montures :** ajouter et supprimer familiers, chevaux, montures spéciales, carrioles et voiliers. Un voilier arrive avec sa voile et son canon. Supprimer un familier ou une monture supprime aussi son équipement. Quand une suppression est impossible, la raison est affichée : principal, lié à une quête ou chargé.
- **Familiers et montures → Équipement :** changer l'équipement des familiers, chevaux, montures spéciales, voiliers et de l'A.T.A.G., canons de navire et armes de l'A.T.A.G. compris. L'équipement est créé et remplace celui qui était porté.
- **Personnage → Teinture :** voir la couleur, la teinture, la matière et la saleté de chaque partie. Exporter une sélection ou la tenue de Kliff, Oongka ou Damiane dans un fichier ou sous forme de code à coller dans un message, et importer l'un ou l'autre. Pour chaque entrée, choisir lequel de vos objets reçoit la teinture, avec son emplacement, son amélioration et sa teinture actuelle, ou en créer un nouveau déjà teint.
- **Vie quotidienne → Décoration :** pivoter de 180° toute une maison de construction libre.

### Corrections

- Sélectionner une cellule ne coupe plus son texte.
- Codex : le niveau ne se vide plus après avoir été appliqué.
- Tous les mercenaires montrent un portrait du visage cadré comme dans la liste de mercenaires du jeu ; les 46 sans image en ont maintenant une.
- Compétences : Pêche aux objets n'est plus listée ; elle ne semble pas encore présente dans le jeu.

### Divers

- Nouveau menu Personnage : équipement, compétences et teinture.
- Les images des familiers, montures et poissons sont plus grandes, sans fond et propres à chaque couleur.
- Familiers et montures n'affiche plus le bétail, les poissons pêchés ni les données internes.
- Moins de boutons, deux par ligne ; les actions rares sont dans le menu ⋯.
- Toutes les fenêtres ont la barre de titre de l'éditeur.
- Les images du jeu synchronisées occupent environ un tiers de l'espace disque (590 Mo → 195 Mo) et la synchronisation est plus rapide. Le premier démarrage après la mise à jour resynchronise une fois.

## [1.9.0]

### Nouveautés

- **Vie quotidienne → Décoration :** activer les parcelles du DLC, exporter et importer des maisons.
- **PNJ → Familiers et montures :** réordonner les navires.
- **Français, allemand, espagnol et portugais du Brésil.**
- Les notes de mise à jour s'affichent dans la langue de l'éditeur.

### Corrections

- Codex : « Appris par » affichait « Aucun enregistrement » juste après l'ouverture d'une sauvegarde.
- Restaurer une sauvegarde en garde aussi une copie dans le dossier `backups/` de l'éditeur.

### Divers

- L'aide des onglets est plus claire. L'aide de Sauvegarde/Restauration indique que chaque fichier garde ses 10 sauvegardes les plus récentes.
