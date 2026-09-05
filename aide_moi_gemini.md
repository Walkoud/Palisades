# Aide demandée : cover art absente du statut Discord Rich Presence

## Contexte projet
- **Palisades** : app desktop WPF .NET 8 (Windows). Fichiers concernés :
  - `Palisades.Application/Services/DiscordPresenceService.cs` — client Discord RPC maison (pas de NuGet, protocole IPC brut sur named pipe `discord-ipc-0..9`)
  - `Palisades.Application/Services/DiscordArtUploader.cs` — résolution/upload des pochettes
  - `Palisades.Application/Plugins/NowPlayingPlugin.cs` — widget "Now Playing" (lit le média via `GlobalSystemMediaTransportControlsSessionManager`, SMTC Windows), appelle `DiscordPresenceService.Instance.Report(...)` sur changement de morceau / play-pause
  - Logs : `%LOCALAPPDATA%\Palisades\debug.log` (lignes `[Discord]`)
- Protocole IPC Discord : frame = `opcode uint32 LE` + `length uint32 LE` + JSON UTF-8. Handshake opcode 0 `{"v":1,"client_id":"..."}`, commandes opcode 1.

## Ce qui MARCHE déjà (vérifié dans les logs)
1. Connexion au pipe + handshake OK (réponse READY reçue, user `walkoud` authentifié).
2. Le **texte** du statut s'affiche ("Listening to" type 2 avec titre/artiste/app + timer). Donc Client ID valide, `SET_ACTIVITY` accepté, pas d'erreur IPC (on loggue toute frame contenant `"code"`, aucune reçue).
3. La résolution de pochette marche : ex. log `cover result: https://is1-ssl.mzstatic.com/.../600x600bb.jpg` (Apple Music CDN) ou fallback upload catbox `https://files.catbox.moe/....png` (upload testé OK via curl aussi, fetch public HTTP 200 vérifié).
4. La clé envoyée est exactement `mp:external/https/is1-ssl.mzstatic.com/image/thumb/.../600x600bb.jpg` (logguée, format vérifié, **aucune troncature** — le `Truncate()` ne s'applique qu'aux textes, jamais aux clés d'image).
5. L'envoi avec cover est bien émis (`hasCover=True` dans les logs).

## Le problème
**La pochette ne s'affiche jamais dans le statut Discord : toujours le logo statique** (`large_image: "palisades"`, asset uploadé sur le portail dev, qui lui s'affiche).
Ni erreur IPC, ni refus — Discord accepte le payload mais n'affiche pas l'image `mp:external/`.

## Historique des tentatives (toutes dans les logs)
1. **Upload thumbnail SMTC → catbox → `mp:external/`** : upload OK (HTTP 200), clé `mp:external/https/files.catbox.moe/....png` envoyée, acceptée, mais image jamais affichée. (Note : premier bug trouvé au passage : `HttpClient` .NET sans User-Agent se faisait tuer la connexion mid-upload — reproduit en PowerShell, corrigé en ajoutant un UA `Mozilla/5.0 ... Palisades/1.0`. L'upload marche depuis.)
2. **URL directe Apple Music CDN** (style PreMiD, sans upload) : `ResolveArtworkUrlAsync` (iTunes Search API `itunes.apple.com/search?term=...&media=music&entity=song`, prend `artworkUrl100` en remplaçant `100x100bb` → `600x600bb`) retourne une URL valide, envoyée en `large_image`, acceptée, mais toujours pas affichée.
3. **Spam / rate-limit** : avant, 4-5 `SET_ACTIVITY` identiques partaient dans la même seconde (2 vues × props + playback, + `timestamps.start` qui changeait à chaque seconde et cassait la dédup). **Corrigé** : debounce 1.5s + signature sémantique (sans le timestamp volatile, le timer tourne côté Discord) + intervalle min 2.5s + 1 seul renvoi de sécurité à +15s. Les logs actuels montrent des envois espacés.
4. **Timing** : avant on envoyait d'abord sans image puis avec image 1s après. Maintenant le premier envoi d'un morceau part en général déjà avec la cover (résolution iTunes ~300ms < debounce 1.5s).

## Code actuel pertinent

### Construction de l'activity (`DiscordPresenceService.BuildActivityJson`)
```csharp
var act = new JObject
{
    ["type"] = 2, // Listening
    ["details"] = Truncate(details, 120)   // titre
};
if (!string.IsNullOrEmpty(state))
    act["state"] = Truncate(state, 120);    // "artiste • app"

var assets = new JObject();
string dynamicCover = "";
if (_showCover && !string.IsNullOrEmpty(_coverUrl))
    dynamicCover = DiscordArtUploader.ToExternalKey(_coverUrl);
if (!string.IsNullOrEmpty(dynamicCover))
{
    assets["large_image"] = dynamicCover;   // ex: "mp:external/https/is1-ssl.mzstatic.com/image/thumb/.../600x600bb.jpg"
    assets["large_text"] = Truncate(_title, 120);
    string smallFallback = !string.IsNullOrEmpty(_smallImage) ? _smallImage : _largeImage; // "palisades"
    assets["small_image"] = smallFallback;
    assets["small_text"] = "Palisades";
}
else { /* logo statique large_image="palisades" */ }
act["assets"] = assets;
act["timestamps"] = new JObject { ["start"] = <unix now - position> };
// root: {"cmd":"SET_ACTIVITY","args":{"pid":<pid>,"activity":act},"nonce":"<guid>"}
```

### Formatage clé (`DiscordArtUploader.ToExternalKey`)
```csharp
// "https://is1-ssl.mzstatic.com/.../600x600bb.jpg"
// → "mp:external/https/is1-ssl.mzstatic.com/.../600x600bb.jpg"
u = "https/" + u.Substring("https://".Length);
return "mp:external/" + u;
```

### Preuve que le format est bon
Un statut PreMiD/Spotify observé chez l'utilisateur donne côté client :
`https://media.discordapp.net/external/<hash>/https/i.scdn.co/image/ab67616d...`
C'est exactement la transformation que fait le proxy Discord à partir d'une clé `mp:external/https/i.scdn.co/...`. Notre clé suit le même format.

### Logs typiques (track Spotify connu)
```
[Discord] resolving artwork for 'Cold Days'
[Discord] cover result: https://is1-ssl.mzstatic.com/image/thumb/.../600x600bb.jpg
[Discord] cover key: mp:external/https/is1-ssl.mzstatic.com/image/thumb/.../600x600bb.jpg
[Discord] send activity: title='Cold Days' playing=True hasCover=True
```
Puis 15s plus tard un renvoi identique. Aucune erreur IPC. Image jamais affichée.

## Questions pour l'IA experte
1. Y a-t-il une condition côté Discord pour que `mp:external/` fonctionne via IPC brut (ex: l'application doit avoir au moins un asset enregistré ? un flag "Rich Presence" ? un type d'activité incompatible avec `type: 2` + images externes ? une taille/format d'image requis — nos images font 600x600 JPG ou PNG 100-250KB) ?
2. Le `pid` envoyé est celui de Palisades. Faut-il que le PID corresponde à un jeu détecté / une app enregistrée pour que les images externes chargent ?
3. Existe-t-il une différence entre `mp:external/https/...` (ce qu'on envoie) et `mp:external/https/...` avec slash final, query params, ou encodage URL requis ?
4. L'alternative `mp:` + URL complète du proxy (`mp:https://media.discordapp.net/external/<url-sans-...>`) : quel est le format exact (faut-il le hash HMAC ? peut-on mettre l'URL brute sans hash) ?
5. Autre piste : Discord refuse-t-il silencieusement les images externes quand `small_image` référence un asset statique en même temps ? Quand `large_text` est présent ? Quand l'activité est de type 2 (Listening) plutôt que 0 (Playing) ?
6. Comment déboguer côté client (logs Discord Ctrl+Shift+I, cache `Cache/` du client à vider, délai de propagation du proxy media) ?

## Contraintes
- Pas de package NuGet Discord (client IPC maison à garder).
- Pas d'upload obligatoire : préférer les URL directes (Apple CDN / i.scdn.co style PreMiD) ; l'upload catbox n'est qu'un fallback.
- C# / .NET 8 / WPF. Répondre avec du code concret et minimal.
