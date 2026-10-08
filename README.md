# Bubulle

Un lanceur à bulles pour Windows : une bulle flottante au bord de l'écran, par-dessus tes jeux. Un clic (ou **Alt+Espace**) déploie tes apps en cascade, et chacune s'ouvre dans sa propre bulle-fenêtre, à côté, sans quitter le jeu.

## Fonctionnalités

- **Bulles d'apps** : Chrome, Discord, Steam, l'Explorateur… s'affichent dans un cadre arrondi, avec opacité, épingle et volume par bulle.
- **Sites web intégrés** : Discord, Messenger, WhatsApp, YouTube, YouTube Music… Tu te connectes une fois, la session est gardée. Tu peux aussi ajouter ton propre site.
- **Mini-lecteur et mode traversable** : une vidéo ou un guide dans un coin, que la souris traverse pour continuer à jouer.
- **Pastilles de messages non lus**, **cercle de téléchargement** (Steam, Chrome, Edge, Brave), **mélangeur audio**.
- **Lanceurs** : des répertoires d'apps et de jeux lancés normalement, avec un nom et un logo au choix.
- **Personnalisation** : ordre des bulles (appui long), noms, logos, sons, curseur, écran, raccourcis clavier.
- **Mises à jour automatiques** : Bubulle vérifie les nouvelles versions au démarrage. Tes réglages sont toujours gardés.

## Installation

1. Télécharge **`Bubulle-Setup-x.y.z.exe`** dans la [dernière version](https://github.com/thino-yoshi/bubulle-app/releases/latest).
2. Lance-le. Si Windows affiche « Windows a protégé votre ordinateur », clique sur **Informations complémentaires**, puis **Exécuter quand même** : l'app n'est pas signée numériquement.
3. Bubulle s'installe dans ton dossier utilisateur, sans droits administrateur.

Windows 10 ou 11 (64 bits) est nécessaire. Rien d'autre à installer.

## Pour les jeux

Les bulles s'affichent par-dessus les jeux en mode **fenêtré sans bordure** (borderless). Le plein écran exclusif ne permet à aucune fenêtre de passer devant.

## Développement

- Code : C# / .NET 8 / WPF (`src/`).
- Publier une version : mettre à jour `<Version>` dans `src/Bubulle.csproj`, puis lancer `tools/release.ps1 -Notes "…"`. Ça demande le SDK .NET 8, Inno Setup 6 et GitHub CLI.
