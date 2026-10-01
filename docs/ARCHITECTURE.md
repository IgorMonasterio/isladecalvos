# Arquitectura del plugin

Este documento explica cómo funciona un plugin de uMod/Oxide para Rust y qué
convenciones seguimos en **Isla de Calvos**. La funcionalidad está descrita en
el [README](../README.md) y en los issues de cada versión.

> ℹ️ **Fuentes.** Las APIs de Oxide que se citan aquí están verificadas contra
> el código fuente de [Oxide.Core](https://github.com/OxideMod/Oxide.Core),
> [Oxide.CSharp](https://github.com/OxideMod/Oxide.CSharp) y
> [Oxide.Rust](https://github.com/OxideMod/Oxide.Rust) (incluido
> `resources/Rust.opj`, que define dónde se inyecta cada hook) y contra
> [Oxide.Docs](https://github.com/OxideMod/Oxide.Docs), cuyo `docs.json` trae
> para cada hook el **código descompilado de Rust** alrededor del punto de
> inyección (`CodeAfterInjection`). Esa es la mejor fuente disponible para la
> API del propio juego. umod.org y docs.oxidemod.com estaban bloqueados desde el
> entorno de trabajo. Lo que no se ha podido verificar está en la sección 8.

## 1. Cómo carga Oxide un plugin

- Un plugin es **un único fichero `.cs`** que se deja en `oxide/plugins/` del
  servidor. *Verificado en `Oxide.CSharp`*: cada `.cs` de `oxide/plugins/` es
  un plugin independiente. No hay forma de repartir un plugin en varios
  ficheros. La carpeta `oxide/plugins/include/` solo admite ficheros
  `Ext.<Nombre>.cs` que sustituyen a extensiones ausentes, y `// Requires:`
  enlaza plugins distintos. Por eso todo va en `src/IslaDeCalvos.cs`,
  organizado con `#region`.
- Oxide lo **compila en caliente** con su propio compilador al arrancar y cada
  vez que el fichero cambia (hot reload). No hay `.csproj` ni DLL que distribuir.
- El **nombre del fichero debe coincidir con el nombre de la clase**:
  `IslaDeCalvos.cs` → `class IslaDeCalvos`. La declaración
  `public class IslaDeCalvos : RustPlugin` debe ir en una sola línea: Oxide
  detecta la clase principal con una expresión regular sobre esa línea.
- La clase vive en el namespace `Oxide.Plugins` y hereda de `RustPlugin`.
- Metadatos obligatorios mediante atributos:
  - `[Info("Título", "Autor", "x.y.z")]`
  - `[Description("...")]`
- Comandos útiles en la consola del servidor:
  `oxide.reload IslaDeCalvos`, `oxide.load ...`, `oxide.unload ...`.
  Los errores de compilación aparecen en la consola y en `oxide/logs/`.

## 2. Hooks

Los hooks son **métodos privados con un nombre concreto** que Oxide invoca por
reflexión cuando ocurre algo. No se registran: basta con declararlos.

En muchos hooks, **devolver un valor distinto de `null` cancela el
comportamiento del juego** (p. ej. `OnPlayerDeath` o `OnPlayerWound`). Por eso
nuestros hooks de juego son `void`: solo observamos, nunca cancelamos.

Hooks que usa la v1.0 y de dónde sale cada uno:

| Hook | Cuándo se llama | Verificado en |
|---|---|---|
| `Init()` | Al cargar el plugin. Registrar permisos y leer datos. | Oxide.Core |
| `OnServerInitialized()` | Servidor listo (o justo tras recargar el plugin en caliente). | `RustCore.cs` |
| `Unload()` | Al descargar el plugin. También al apagar el servidor: `OnShutdown` descarga todos los plugins. | `OxideMod.cs` |
| `OnServerSave()` | Cada guardado automático del servidor. | `Rust.opj` (`SaveRestore.DoAutomatedSave`) |
| `OnNewSave(string filename)` | Al crearse un mapa nuevo (wipe). | `Rust.opj` (`SaveRestore.Load`) |
| `OnPlayerConnected(BasePlayer player)` | Jugador conectado. | `RustHooks.cs` |
| `OnPlayerWound(BasePlayer player, HitInfo info)` | Jugador derribado (downed). | `Rust.opj` (`BasePlayer.BecomeWounded`) |
| `OnPlayerRecovered(BasePlayer player)` | Jugador que se levanta tras estar derribado. | `Rust.opj` (`BasePlayer.RecoverFromWounded`) |
| `OnPlayerDeath(BasePlayer player, HitInfo info)` | Muerte de cualquier `BasePlayer` (también NPCs humanos). No salta si el jugador queda derribado en vez de morir. | `Rust.opj` + código descompilado (`BasePlayer.Die`) |
| `OnEntityDeath(BaseCombatEntity entity, HitInfo info)` | Muerte de cualquier entidad de combate: NPCs, animales, Bradley, CH47… **También de jugadores**: `BasePlayer.Die` lanza `OnPlayerDeath` y luego llama a `base.Die`, que lanza este. | Código descompilado (`BaseCombatEntity.Die`) |
| `OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)` | Daño a entidades. Oxide.Rust lo genera desde `IOnBaseCombatEntityHurt` (y desde otros dos hooks internos para jugadores). | `RustHooks.cs` + código descompilado (`BaseCombatEntity.Hurt`) |
| `OnPatrolHelicopterTakeDamage(PatrolHelicopter heli, HitInfo info)` | Daño al helicóptero de patrulla, que **sobrescribe `Hurt`**. | Código descompilado (`PatrolHelicopter.Hurt`) |
| `OnPatrolHelicopterKill(PatrolHelicopter heli, HitInfo info)` | Cuando el daño supera la vida del heli. **El heli no muere ahí**: el juego le pone 10000 de vida y lo manda a estrellarse. Es el momento de "derribado". | Código descompilado (`PatrolHelicopter.Hurt`) |
| `OnHelicopterAttack(CH47HelicopterAIController heli, HitInfo info)` | Ataque al Chinook, antes de `base.OnAttacked`. | Código descompilado (`CH47HelicopterAIController.OnAttacked`) |
| `OnEntityKill(BaseNetworkable entity)` | Cualquier entidad destruida (también al despawnear). Solo lo usamos para limpiar memoria. | Código descompilado (`BaseNetworkable.Kill`) |
| `OnPlayerSleepEnded(BasePlayer player)` | El jugador despierta (tras conectar y tras cada respawn). Es cuando se dibuja el contador en pantalla (y, tras un wipe, cuando sale el anuncio del ganador del mapa). | Código descompilado (`BasePlayer.EndSleeping`) |
| `OnPlayerDisconnected(BasePlayer player, string reason)` | Jugador desconectado. Termina la cacería si se va el objetivo. | Código descompilado (`ServerMgr`) + `RustHooks.cs` |

## 3. Configuración

- Fichero: `oxide/config/IslaDeCalvos.json` (lo edita el admin del servidor).
- Patrón: una clase `Configuration` tipada; `LoadDefaultConfig()` la crea con
  valores por defecto, `Config.ReadObject<Configuration>()` la lee al cargar y
  `Config.WriteObject(...)` la guarda.
- **Trampa de Newtonsoft (verificada)**: Oxide lee la config con los ajustes
  por defecto (`ObjectCreationHandling.Auto`). Con ellos, las listas del JSON
  se *añaden* a las que ya trae el valor por defecto del campo, y las claves
  borradas de un diccionario reaparecen. Por eso **toda colección con valores
  por defecto lleva `ObjectCreationHandling = ObjectCreationHandling.Replace`**
  en su `[JsonProperty]`.
- Si el JSON está corrupto, se avisa con `PrintWarning` y se usan los valores
  por defecto. Tras leerla se valida (títulos ordenados, intervalos mínimos) y
  se vuelve a guardar, para que las opciones nuevas aparezcan en el fichero.

## 4. Datos persistentes

- Fichero `oxide/data/IslaDeCalvos.json` mediante
  `Interface.Oxide.DataFileSystem.ReadObject<T>` / `WriteObject`.
- **Config** = lo que decide el admin; **data** = el estado que genera la
  partida.
- Se guarda solo si hay cambios (flag `dataDirty`), en `OnServerSave()` y en
  `Unload()`, nunca en cada kill.
- Lo que no hace falta que sobreviva a un reinicio (cooldowns anti-farmeo,
  registros de derribos) vive solo en memoria.

## 4b. Recompensas de evento

Sistema genérico para "objetivos grandes" cuya caída paga a un grupo. Hoy lo
usan el heli, la Bradley y el CH47 (`SharedRewardTargets`), pero está pensado
para más eventos de equipo:

- `TrackEventParticipant(target, info)`: se llama desde los hooks de daño.
  Apunta al atacante (jugador real) y su `currentTeam` en el estado del
  objetivo. No depende del atacante del hook de muerte.
- `CompleteEventTarget(target)`: se llama cuando el objetivo cae. Marca el
  objetivo como cobrado (nunca se paga dos veces) y construye un `RewardEvent`.
- `PayEventReward(rewardEvent)`: paga a todos los participantes y a los
  compañeros de sus equipos conectados y cerca de la posición, **una vez por
  jugador**.
- Los equipos se guardan al registrar el daño, así que los compañeros se
  encuentran aunque el atacante ya esté muerto o desconectado.
- El estado vive en memoria y se limpia en `OnEntityKill`.

Para añadir otro evento: decidir qué hook marca "participar" y cuál marca
"completado", y llamar a esas funciones con su propio objetivo y recompensa.

## 4c. Eventos globales (v1.1)

- Un único evento activo (`activeEvent`), con un `timer.Every` que cada
  `IntervalMinutes` intenta arrancar uno al azar y un `timer.Once` que lo
  termina. Los timers de Oxide se destruyen solos al descargar el plugin, y el
  evento en curso se pierde (no se guarda).
- **Toda ganancia de calvicie por juego pasa por `GainBaldness`**: así la Hora
  de la calvicie la multiplica en un solo sitio. Los cambios de admin y los
  premios de la cacería van directos por `ChangeBaldness`.
- La Lluvia de champú multiplica en `OnPlayerDeath`. El Brote de alopecia (enum `BladeStorm`, de cuando se llamó Tormenta de cuchillas), en
  `CompleteEventTarget` (recompensa compartida).
- Para añadir un evento: nuevo valor en el enum `GlobalEvent`, su bloque en la
  config, su `case` en `StartEvent`/`EndEvent` y sus textos en `lang`.

## 4d. Interfaz en pantalla (v1.2)

- Se usa la CUI de Oxide.Rust (`Oxide.Game.Rust.Cui`: `CuiHelper.AddUi`/
  `DestroyUi`, `CuiElementContainer`, `CuiPanel`, `CuiLabel`), verificada en
  `src/RustCui.cs`.
- Elementos con nombre fijo: `IslaDeCalvos.Counter`, `IslaDeCalvos.Delta`
  e `IslaDeCalvos.Banner` (y desde la 1.9.1 `IslaDeCalvos.Wallet`, §4m). Se añaden con `destroyUi` = su propio nombre, así
  cada redibujado sustituye al anterior sin parpadeo.
- El contador se redibuja desde `ChangeBaldness` (todo cambio pasa por ahí) y
  al despertar (`OnPlayerSleepEnded`). No se manda UI a jugadores dormidos.
- Los mensajes de evento van por `BroadcastEvent` (chat + cartel central).
- `Unload` borra la UI de todos. `PlayerData.Id` (no se guarda en disco, se
  rellena al cargar) permite encontrar al jugador desde sus datos.
- **No se puede ver la UI desde el entorno cloud**: posiciones y tamaños hay
  que ajustarlos mirando el juego.

## 4e. El Calvario (v1.3)

- Objetos: `ItemManager.FindItemDefinition(nombre)` → `itemid`;
  `ItemManager.CreateByItemID(id, 1)`; `player.inventory.GiveItem(item)` y,
  si falla, `item.Drop(player.GetDropPosition(), player.GetInheritedDropVelocity())`;
  `inventory.GetAmount(id)` / `inventory.Take(null, id, 1)`. Todo visto en el
  código descompilado de Oxide.Docs y en Oxide.Rust.
- **Marca propia** (1.6.8): lo que reparte el plugin se crea con
  `ItemManager.CreateByItemID(id, cantidad, MarkSkin)`, con el tercer
  argumento como skin, igual que GUIShop 2.4.48. El barbero no usa
  `GetAmount`/`Take` (cuentan cualquier objeto de ese tipo, también el del loot
  normal): `FindMarkedItems` recorre `containerMain`, `containerBelt` y
  `containerWear` y se queda con los que tienen `item.info.itemid` y
  `item.skin == MarkSkin`, como hace GUIShop. Para gastar uno se llama a
  `item.UseItem(1)` (en `docs.json`). Los premios por título se dan sin marca
  (`GiveItem(..., marked: false)`). `MarkSkin` por defecto es 9202609270, por
  encima de los IDs de Workshop actuales; cambiarlo deja sin valor lo que ya
  esté repartido.
- Repartos: barriles en `OnEntityDeath` (un `LootContainer` cuyo prefab
  contiene `barrel`), NPCs tras cobrar su tier, y placas rojas en
  `PayEventReward` para cada jugador pagado que esté conectado.
- Ventanas: CUI sobre `Overlay` con `CursorEnabled`. Los botones llaman a
  comandos de consola del plugin (`calvos.tab` para las pestañas de `/calvos`;
  `calvos.barber`, `calvos.use` y `calvos.carne` para el barbero), que leen
  `arg.Player()` y `arg.FullString`. Cada acción redibuja la ventana.
- El barbero (desde la 1.5.0) es un cuadro de conversación: su frase arriba y
  las respuestas numeradas abajo (`OpenBarber`, páginas `Main`, `Items` y
  `Carne`). `UseCursedItem` y `DeliverIdTags` **devuelven** la frase del
  barbero en vez de mandarla al chat. Los anuncios globales (carné completo)
  y el aviso de pila agotada siguen yendo al chat.
- Estado: `HasDeathShield`, `CarneColors` y `CarnesCompleted` en `PlayerData`
  (se guardan); la pila vive en memoria (`batteryUntil`).
- Todas las ganancias de objetos pasan por `GainBaldness`, así que eventos y
  pila las multiplican.

## 4f. Puntos de Chola de Server Rewards (plugin 1.4.0)

- Referencia blanda: `[PluginReference] private Plugin ServerRewards = null;`.
  Oxide rellena el campo por su nombre con el plugin cargado
  (`PluginReferenceAttribute`, en Oxide.CSharp `CSharpPlugin.cs`). Si no está
  cargado, se queda en `null` y no se paga nada. Antes de llamar se mira
  `IsLoaded` (Oxide.Core `Plugin.cs`).
- Pago: `ServerRewards.Call("AddPoints", ulong userId, int amount)`
  (`Plugin.Call(string, params object[])`, Oxide.Core). Firma en Server
  Rewards 0.4.78 (k1lly0u): `object AddPoints(object userID, int amount)`.
  Acepta `ulong`, `string` o `EncryptedValue<ulong>` y devuelve `true` si
  paga. Verificado en su código fuente (copia pública en GitHub,
  publicrust/umod-pluigns-dataset); umod.org está bloqueado desde el entorno
  cloud.
- El servidor tiene **Server Rewards 2.0.8**. En la 2.0.7 (copia pública,
  0xF1o/random-free-rustplugins) `AddPoints` está sobrecargado (`BasePlayer`,
  `IPlayer`, `string`, `EncryptedValue<ulong>`, `ulong`) y devuelve `bool`.
  Oxide elige la sobrecarga cuyo tipo coincide exactamente con el argumento
  (`CSPlugin.FindHooks` → `HookMethod.HasMatchingSignature`, Oxide.Core). El
  plugin pasa un `ulong`, así que entra en `AddPoints(ulong, int)`, que
  devuelve `true`.
- Reloj: va con `SurvivalTick` (cada minuto, solo vivo, conectado y
  despierto). `RpSeconds` y `RpMoved` se guardan en `PlayerData`. La última
  posición vive en memoria (`lastPositions`) y se borra al desconectar.
- Los Puntos de Chola no pasan por `GainBaldness` ni `ChangeBaldness`: no tocan la calvicie
  ni se multiplican.
- **Aviso si falta el parche** (1.6.8): `CheckServerRewardsPatch` llama a
  `CheckPointsLong` en `OnServerInitialized` y en `OnPluginLoaded` de Server
  Rewards (hooks de `OxideMod.cs`). Si devuelve `null`, saca un
  `PrintWarning` en español, una vez por detección; `OnPluginUnloaded` lo
  rearma. Los centinelas del servidor reenvían la consola a Telegram.
- **Límite de 32 bits** (1.6.7). Server Rewards 2.0.7 guarda el saldo en
  `Hash<ulong, int>` y `AddPoints` hace `+=` sin comprobar: pasado
  `int.MaxValue` da la vuelta a negativo (C# no comprueba desbordamientos por
  defecto). La 2.0.8 del servidor no se ha podido leer.
  - Todos los pagos pasan por `AddRp`/`TakeRp`/`CheckRp`/`RpRoom`. Con la API
    `int`, `AddRp` rechaza lo que no quepa hasta `int.MaxValue`, y la paga de
    cada 30 min se recorta con `RpRoom` (aviso en consola una vez).
  - Si Server Rewards trae `AddPointsLong(ulong, long)`,
    `TakePointsLong(ulong, long)` → `bool` y `CheckPointsLong(ulong)` →
    `long` (parche propio del servidor, no existen en el Server Rewards
    oficial), se usan esos. Se detecta sin configurar nada: si un plugin no
    tiene el método, `CSPlugin.OnCallHook` no encuentra hook y devuelve
    `null` (Oxide.Core), y entonces se usa la API `int`.
  - Parche aplicado por Jano en el Server Rewards 2.0.8 del servidor el
    2026-09-27, con esas firmas exactas: `AddPointsLong` devuelve `false` si
    `amount <= 0` y se satura en `long.MaxValue`; `TakePointsLong` devuelve
    `false` si `amount <= 0` o no hay saldo suficiente; `CheckPointsLong`
    devuelve 0 si no hay saldo. Si una actualización lo pisa, los métodos
    desaparecen y el plugin vuelve solo a la API `int` con freno.
  - El parche no puede cambiar `CheckPoints` a `long`: GUIShop hace
    `(int)ServerRewards.Call("CheckPoints", …)` y petaría. `CheckPoints` y
    el hook `OnPointsUpdated(ulong, int)` siguen en `int`, recortados.
- **Números grandes en nuestro lado**: la alopecia es `long`. `ChangeBaldness`,
  los multiplicadores de eventos y pila y los precios del cambio usan
  `SaturatingAdd`/`SaturatingMultiply`, que se paran en `long.MaxValue` en vez
  de dar la vuelta. `FormatCompact` abrevia a M/B/T desde 10^9 en los sitios
  estrechos; `FormatBaldness` (entero con puntos) en el resto.

## 4g. La peluquería: Calvario en un NPC (plugin 1.5.0)

- Hook de **HumanNPC** (no de Oxide): `OnUseNPC(BasePlayer npc, BasePlayer
  player)`. Se lanza con `Interface.Oxide.CallHook` cuando un jugador pulsa
  USAR mirando a uno de sus NPC, a 5 m como máximo. Visto en el código de
  HumanNPC 0.5.4 (copia pública, publicrust/umod-pluigns-dataset). No hace
  falta `[PluginReference]`: si HumanNPC no está, el hook no llega y el
  Calvario sencillamente no se abre.
- Los NPC del Calvario se configuran por `userid` (`Calvario NPC ids`). Sus
  ids no son SteamID, así que `IsRealPlayer` los descarta en muertes y
  supervivencia.
- `calvarioNpcInUse` guarda el NPC con el que habló cada jugador (en memoria).
  `calvos.barber`, `calvos.use` y `calvos.carne` exigen haber hablado con él
  y estar a `MaxDistance` o menos. Si no, cierran la ventana y mandan al
  jugador a la peluquería. Hace falta porque los comandos de consola se pueden
  escribir desde cualquier sitio.
- `/calvos` no abre objetos, solo el ranking (`OpenCalvos`; desde la 1.8.0,
  con las pestañas del §4j). No hay plan B sin barbero (decisión de Igor): si
  `Calvario NPC ids` está vacío, `ValidateConfig` lo avisa en la consola y
  nadie puede usar objetos.
- Los cadáveres (`*.corpse`) mueren al desollarlos. `IsPossibleNpc` los
  descarta para que no salgan en el log de NPC sin tier. Desde la 1.9.0 también
  descarta construcciones, puertas y desplegables (ver §4l).
- El Mercalvona (GUIShop), Cambio de divisas (Server Rewards; antes Premios
  Calvos) y el TP `/peluqueria`
  (NTeleportation, "Dynamic Commands") son configuración de esos plugins; el
  plugin no los llama. Los pasos están en el README.

## 4h. Premios por título, paga lineal de Puntos de Chola y cambio de calvicie (plugin 1.6.0)

- **Economics** es la segunda dependencia blanda (`[PluginReference] Plugin
  Economics`), decidida por Igor para premios y cambio. API verificada en
  Economics 3.9.2 (copia pública): `Deposit(string playerId, double)` → `bool`,
  `Withdraw(string, double)` → `bool` y `Balance(string)` → `double`. El plugin
  pasa el id como `string` y la cantidad como `double`, para caer en la
  sobrecarga exacta. La versión del servidor no se ha comprobado.
- **Server Rewards 2.x**: además de `AddPoints`, `TakePoints(ulong, int)` →
  `bool` (falla si no hay saldo) y `CheckPoints(ulong)` → `int`. Visto en la 2.0.7.
- **Premios**: `ChangeBaldness` llama a `PayTierPrizes` al subir de título si
  `announce` es verdadero (así que no se paga con los cambios de admin).
  `PlayerData.PrizedTier` guarda el título más alto ya tratado. Vale -1 hasta
  el primer cambio de título; entonces se inicializa con el título que tenía,
  para no pagar títulos ya conseguidos antes de la 1.6.0. La alopecia comprada
  en el cambio paga como cualquier otra desde la 1.10.0 (antes, con `bought` y la
  opción `Tier prizes also for bought baldness` apagada, se marcaba como tratado
  sin pagar; el parámetro y la opción ya no existen).
- **Cartel de subida**: `ShowBanner(texto, segundos)`, compartido con
  `BroadcastEvent`.
- **Paga lineal de Puntos de Chola**: `GetRpRate` = `calvicie / X` en `long` (sin tope desde la 1.6.7).
- **Cambio**: comando `calvos.exchange mode|amount|confirm`. La operación
  pendiente vive en el servidor (`pendingExchanges`); `confirm` no lleva
  datos. Se comprueba que el modo existe (`Enum.IsDefined`), que la cantidad
  está en la lista de la config y que llega el saldo. Primero se mueve el
  Puntos de Chola o los pelones y después la calvicie. La calvicie comprada entra por
  `ChangeBaldness` (no por `GainBaldness`), para que no la multipliquen
  eventos ni la pila.

## 4i. Grupos por título, premios en silencio y aviso a otros plugins (plugin 1.7.0)

- **Título real**: `GetTitleIndex` devuelve -1 por debajo del primer título (a 0
  no hay título); `GetTierIndex` sigue devolviendo 0 ahí y lo usan el contador,
  el ranking y los anuncios del chat, que no cambian (pasar de 0 a 1 no se
  anuncia, como antes).
- **Grupos**: `SyncTitleGroup(data)` mete al jugador en el grupo de su título y
  lo saca de los demás grupos de `"Title groups"`, por id (vale también para
  desconectados). Se llama en `OnServerInitialized` (conectados, y crea los
  grupos que falten), `OnPlayerConnected`, `OnNewSave` (tras el reset, a todos
  los guardados) y en `ChangeBaldness` cuando cambia el título real, **antes**
  del corte por `announce`, así que también con admin y con alopecia comprada.
  API de `Oxide.Core/Libraries/Permission.cs`: `GroupExists(string)`,
  `CreateGroup(string name, string title, int rank)` → `bool`,
  `UserHasGroup(string id, string group)`, `AddUserGroup` y `RemoveUserGroup`
  (los tres últimos no hacen nada si el grupo no existe). Ojo:
  `RemoveUserGroup(id, "*")` vacía todos los grupos del jugador; por eso `*`,
  `default` y `admin` se rechazan como grupo de título (`ProtectedGroups`).
- **Premios**: `PayTierPrizes` se llama cuando sube el título real, así que el
  primer título (desde -1) también paga. `PrizedTier` = -1 sigue sirviendo:
  se inicializa con el título real anterior, que puede ser -1. Los objetos no
  salen en el mensaje (`TierPrizeV2` solo con Puntos de Chola y pelones;
  `TierPrizeItems` si solo hubo objetos). `GiveItem` los mete en el inventario
  o, si `PlayerInventory.GiveItem` falla, los suelta con `item.Drop` (§4e).
  `"Message"` se manda tal cual con `SendChat`.
- **Hook para otros plugins**: `Interface.CallHook("OnIslaTitleChanged", ulong,
  string, string, string, bool, long)` (sobrecarga de 6 argumentos de
  `Oxide.Core/Interface.cs`), al final de `ChangeBaldness`, después de anuncios
  y premios, solo si `announce` (no con `/calvoadmin`) y cuando cambia el
  título real. Título vacío = sin título.
- **Cartel de bajada**: mismo `ShowBanner`, con la duración del de subida.

## 4j. Salón de la fama, cabezas y Calvo del Día (plugin 1.8.0)

Encargo de Jano. Todo el estado nuevo va en el mismo `oxide/data/IslaDeCalvos.json`
(`StoredData`): `HallOfFame`, `HallNextNumber`, `PendingWipeAnnouncement`,
`Bounties` y `CalvoDelDia`, con `ObjectCreationHandling.Replace` en las colecciones y
comprobación de `null` en `LoadData`. `PlayerData` gana `WipeKills` y `WipeDeaths`.

- **Salón de la fama**: `OnNewSave` llama primero a `SaveHallEntry(false)` y después
  pone `WipeKills`/`WipeDeaths` a 0 y, si toca, resetea la alopecia. La entrada lleva
  número propio (`HallNextNumber`, no se reutiliza), fecha (`DateTime.Now`), podio
  (orden del ranking), `TopKiller` y `TopDeaths`. Como al arrancar no hay nadie
  conectado, el anuncio queda en `PendingWipeAnnouncement`. `/calvoadmin salon
  guardar` usa la misma función. (Desde la 1.8.2 todo esto va en `CloseMap`, ver §4k.)
  - **Cuándo llega `OnNewSave`** (verificado en Oxide.Core/CSharp): `OxideMod.Load` →
    `LoadAllPlugins(true)` **espera** a que acaben de compilarse y cargarse los
    plugins C# (`while (loader.LoadingPlugins.Count > 0)`, `OxideMod.cs`), así que
    `Init` y `LoadData` ya han corrido. `SaveRestore.Load` lanza `OnNewSave` solo si
    no existe el `.sav` (docs.json). **No verificable**: que `Bootstrap.Init_Tier0`
    (donde se inyecta `InitOxide`) vaya antes que `SaveRestore.Load` en el arranque
    de Rust. Un plugin cargado en caliente **nunca** recibe `OnNewSave`, y uno que no
    compila al arrancar tampoco. `SaveRestore.SaveCreatedTime`/`WipeId` no aparecen
    en ninguna fuente verificable, así que no hay detección alternativa del wipe.
- **Cabezas**: `/cabeza` busca al objetivo con `FindStoredPlayers` (la cantidad es la
  última palabra), cobra con `TakeRp` (API `long` si está el parche) y solo entonces
  sube el bote. El cobro (`TryClaimBounty`) va en `OnPlayerDeath` justo después de
  contar la kill y antes del cooldown anti-farmeo; paga con `AddRp` y, si Server
  Rewards no paga, el bote se queda. Equipo: `currentTeam` del asesino y del muerto
  (verificado en docs.json).
- **Calvo del Día**: `CheckCalvoDelDia` cada 60 s compara `DateTime.Now` (hora del
  server) con la hora de la config y con `LastPickDate`, así que se elige una vez al
  día aunque se reinicie. `PickCalvoDelDia` mira a los de `Seen` (conectados desde
  la foto anterior, más los conectados ahora), gana el que más haya subido desde
  `Snapshot` (neto) y hace la foto nueva. Sin foto previa (primera carga) se hace la
  foto en `InitCalvoDelDia`. Con el reset del wipe activado, la foto se vacía (todos
  cuentan desde 0).
  - **Grupo** (`Oxide.Core/Libraries/Permission.cs`): `GetUsersInGroup` devuelve
    `"<id> (<último apodo>)"`; `SyncCalvoDelDiaGroup` saca a todo el que no sea el
    vigente y mete al vigente. `AddUserGroup`/`RemoveUserGroup` funcionan con
    jugadores desconectados (crean su entrada si no existe). El grupo no puede ser
    uno de título ni de `ProtectedGroups`.
- **Hooks** (`Interface.CallHook`, sobrecargas fijas de 1 a 10 argumentos `object`
  en `Oxide.Core/src/Interface.cs`; no hay versión `params`, y un único argumento
  de tipo array se repartiría como varios): `OnIslaWipeHallOfFame(string)`,
  `OnIslaBountyPlaced` (6), `OnIslaBountyClaimed` (5), `OnIslaCalvoDelDia` (3) y
  `OnIslaHuntEnded` (4). Los JSON llevan los SteamID como texto.
- **`isla.ranking`**: `[ConsoleCommand]` normal. Oxide lo registra con
  `ServerUser = true, Client = true` (`Oxide.Rust/src/Libraries/Command.cs`), así
  que **un jugador puede escribirlo en F1**: se le rechaza con
  `arg.Connection != null` (la consola y RCON no tienen conexión; así lo trata el
  propio Oxide en `RustCommandSystem.cs`). La respuesta va por
  `arg.ReplyWith(string)` (visto en docs.json). **No verificable**: cómo devuelve
  RCON de Facepunch el texto de `ReplyWith` (su código no está en ninguna fuente
  pública); con el RCON propio de Oxide (apagado por defecto) no se devuelve.
- **Ventana**: `/calvos` tiene pestañas (`OpenCalvos` con `MenuTab`); los botones
  llaman a `calvos.tab <ranking|salon|cabezas> <página>`. El paginador es común
  (`DrawPager`) y el oro/plata/bronce, `PodiumColors`.

## 4k. Arreglos del cierre de mapa (plugin 1.8.2)

Encargo de Jano antes del wipe del 1 de octubre.

- **`CloseMap(force, source)`**: lo que antes hacía `OnNewSave`, en un método común
  que llaman `OnNewSave`, `/calvoadmin salon cerrar [forzar]` y `isla.salon cerrar
  [forzar]` (consola/RCON, rechazado con `arg.Connection != null` como `isla.ranking`;
  los argumentos salen de `MenuArgs`, que ya usa `FullString.ToString()`). Orden:
  comprobación de repetición → entrada → anuncio y hook → `WipeKills`/`WipeDeaths`
  a 0 → reset de alopecia solo con `ResetBaldnessOnWipe` → `SaveData` → grupos de
  título. Con reset, además redibuja el contador de los conectados (en un cierre a
  mano puede haber gente dentro).
- **Un wipe, un cierre**: el día del wipe forzado el server se relanza ~1 min
  después con semilla nueva, y como cambia el nombre del `.sav`, `OnNewSave` llega
  dos veces. `CloseMap` no hace nada (solo `PrintWarning`) si el último cierre fue
  hace menos de `MapCloseRepeatHours` (12). El último cierre es el mayor entre
  `StoredData.LastMapClose` (nuevo; se pone en cada cierre, se guarde entrada o no)
  y la fecha de la entrada no manual más nueva (para datos anteriores a la 1.8.2).
- **Entrada vacía**: la de un cierre solo se guarda si alguien tiene `WipeKills` o
  `WipeDeaths` > 0; con el reset apagado la alopecia nunca vuelve a 0 y no sirve
  para saber si hubo mapa. La foto a mano (`Manual`) sigue con la regla vieja
  (alopecia o kills). En la pestaña, `Manual` dice "Foto del mapa del…"
  (`HallSnapshot`).
- **Hook del salón**: `OnNewSave` corre mientras carga el mundo, antes de
  `OnServerInitialized`, y ahí nadie escucha por RCON. `serverReady` (se pone en
  `OnServerInitialized`) decide: si es `false`, el número de la entrada se guarda en
  `StoredData.PendingWipeHook` y `OnServerInitialized` lo entrega con
  `timer.Once(60 s)`. Está en los datos, así que si el server se reinicia antes de
  los 60 s (el relanzamiento del cron), sale en el arranque siguiente. `salon
  guardar` y `salon cerrar` llaman al hook al momento.
- **Anuncio por jugador**: `PendingWipeAnnouncement` ya no se vacía al anunciar; dura
  hasta el siguiente cierre. `StoredData.WipeAnnouncementSeen` (se vacía en cada
  cierre) guarda quién lo ha visto; `OnPlayerSleepEnded` programa el mensaje a los
  5 s y se marca como visto solo al enviarlo (si sigue conectado). Va por `Reply`,
  al chat del jugador. `salon borrar` de la entrada pendiente pasa el anuncio a la
  entrada no manual más nueva con podio a menos de 12 h de la borrada (el caso de
  la entrada repetida de la 1.8.1); si no hay, lo quita.
- **Calvo del Día**: `ExcludeFromCalvoDelDia(data, before)` suma a la foto del
  jugador lo que ha cambiado su alopecia (saturado; puede quedar negativa, y está
  bien: lo ganado se mantiene). Se llama tras comprar en el barbero y tras
  `/calvoadmin set`/`reset`. Vender no se excluye. `/calvoadmin calvodeldia ahora`
  pone `LastPickDate` a hoy antes de elegir.
- **`OnIslaHuntEnded`**: `outcome = "stopped"` cuando un admin para la cacería con
  `/calvoadmin evento parar` (se llama después de `EndEvent`).

## 4l. Catálogo del barbero, reliquias y log de NPC sin tier (plugin 1.9.0)

Encargo de Jano.

- **Catálogo**: `OpenCatalog(player, line)` es la ventana de objetos de la 1.3.1
  (`DrawItemsTab` del código anterior a 8c843db), dentro del barbero: cabecera con
  poste de barbero, 7 tarjetas con icono (`AddIcon`), cuántas llevas y qué hacen,
  una tarjeta de refrán y la fila del Carné de Calvo. Se abre con `calvos.barber
  catalog` (opción del menú principal). Los botones son los mismos comandos de
  siempre con un argumento más: `calvos.use <clave> catalog` y `calvos.carne
  catalog` contestan en el catálogo en vez de en la conversación; sin el
  argumento, todo sigue igual. Todo pasa por `RequireCalvarioNpc`, así que hace
  falta estar al lado del barbero. No hay comando `/calvario`.
- **Reliquias**: en los textos para jugadores los objetos del Calvario son
  "reliquias" (TONO.md). En el código y en la config siguen siendo `Cursed*`
  (`CursedItemKeys`, `"Cursed items (El Calvario)"`) y la marca 9202609270, para
  no romper configs ni objetos ya repartidos.
- **Log de NPC sin tier**: las bases de RaidableBases salen con `OwnerID` 0, así
  que el último filtro de `IsPossibleNpc` las tomaba por NPC y la consola se llenaba
  de `Unlisted NPC killed: 'door.hinged.metal'`. Ahora `IsPossibleNpc` devuelve
  `false` para `BuildingBlock`, `Door`, `DecayEntity` y cualquier prefab acabado en
  `.deployed` o `_deployed` (p. ej. `chair.deployed`, `repairbench_deployed`). Solo
  afecta a prefabs que **no** están en `NpcTiers`: lo que está listado (sentries
  incluidas) sigue su camino, y el heli, la Bradley y el Chinook se tratan antes,
  en `SharedRewardTargets`. **No verificado**: la jerarquía de clases (que `Door` y
  los contenedores hereden de `DecayEntity`). docs.json confirma que los tipos
  existen, no de quién heredan; por eso `BuildingBlock` y `Door` van también a mano
  y los desplegables se filtran además por nombre.

## 4m. Cartera bajo el contador (plugin 1.9.1)

- Elemento `IslaDeCalvos.Wallet`: tira de 19 px pegada debajo del contador, con
  el mismo ancla y ancho (`ShiftY` sobre `Counter offset min`: con la config por
  defecto, `-212 -78` / `-16 -59`). Texto `HudWallet`, tamaño 11.
- Cifras con `FormatWallet`: entera por debajo del millón, `12,3 M` hasta mil
  millones y `1,5 mil M` desde ahí (un decimal, truncado). No es `FormatCompact`,
  que no abrevia hasta mil millones y usa B/T.
- Puntos por `CheckRp` (`CheckPointsLong` si está el parche; si no, `CheckPoints`);
  pelones por `CoinBalance` (Economics `Balance`). Si falta uno, su cifra es `-`;
  si faltan los dos, no hay cartera (`WalletActive`).
- Se dibuja desde `DrawCounter` (forzado) y con `PollWallets` cada 3 s, que solo
  manda UI si el texto cambió (`walletTexts`). Nada a jugadores dormidos. El
  sondeo solo se arranca con `Show baldness counter` y `Show wallet under the
  counter` activos.
- El `+X`/`-X` baja 20 px más cuando la cartera está activa y el contador está en
  la mitad de arriba; en la de abajo sigue saliendo encima del contador.

## 5. Localización (lang)

- Todos los textos del plugin pasan por `lang`: se registran en
  `LoadDefaultMessages()` con `lang.RegisterMessages(...)` y se leen con
  `lang.GetMessage(clave, this, userId)`.
- Oxide genera `oxide/lang/<idioma>/IslaDeCalvos.json`, que el admin puede
  editar sin tocar código.
- **El español es el idioma por defecto.** *Verificado en `Oxide.Rust` y
  `Oxide.Core`*: Oxide asigna a cada jugador el idioma de su cliente de Rust
  (casi siempre `en`) y, si falta un texto, recurre a `en`. Por eso los textos
  en español se registran **en `es` y también en `en`**. Si algún día se quiere
  traducir al inglés, basta con editar `oxide/lang/en/IslaDeCalvos.json`.
- **Cambiar un texto ya desplegado**: `Lang.MergeMessages` (Oxide.Core
  `Libraries/Lang.cs`) solo añade las claves que faltan en el fichero y borra
  las que ya no se registran. Nunca pisa un texto que ya existe. Para que un
  texto nuevo llegue solo, se renombra su clave. Convención desde la 1.4.1:
  sufijo `V2`, `V3`… (`TitleUp` → `TitleUpV2`). La 1.3.1 lo hizo con el
  prefijo `Calvario*`. La 1.6.3 renombró así los 17 textos que llamaban
  "calvicie" a la cifra, que en el juego se llama alopecia
  (`BarberExIntroV2`, `CalvarioYouV2`, `EventBaldHourStartV3`…). La 1.9.0
  cambió "objetos malditos" por "reliquias" del mismo modo (`BarberOptItemsV3`,
  `BarberItemsIntroV3`…).
- Nombres de eventos: `EventName(GlobalEvent)` busca `"EventName" + valor +
  "V3"` (p. ej. `EventNameHairiestHuntV3`). Si vuelve a cambiar un nombre, se
  sube el sufijo en ese método y en las cuatro claves a la vez.
- El tono y el vocabulario de los textos están en `docs/TONO.md`.
- Colores de la casa (1.6.5): en los textos, `<color=#e0a526>` (dorado) para
  comandos y cifras buenas y `<color=#e0662f>` (óxido) para lo que duele; en
  la interfaz, `ColorGold`, `ColorRust` y `ColorMuted` (gris `#9a9288`). Los
  fondos, el poste de barbero y las medallas del podio no cambian.
- Refranes del Calvario: `CalvarioProverb<n>`, con los números en
  `ProverbNumbers`. El 6 se quitó en la 1.4.1 y no se reutiliza: su clave
  vieja sigue en los ficheros desplegados hasta que Oxide la borra al cargar.

## 6. Permisos

- Se registran en `Init()` con `permission.RegisterPermission(nombre, this)` y se
  comprueban con `permission.UserHasPermission(userId, nombre)`.
- Se asignan desde la consola: `oxide.grant user|group <quién> <permiso>`.

## 7. Convenciones del proyecto

- **Un solo plugin, un solo fichero**: `src/IslaDeCalvos.cs`, con `#region`.
- **Idioma**: código, identificadores y comentarios en inglés; documentación y
  textos para jugadores en español.
- **Permisos**: siempre con el prefijo `isladecalvos.` (p. ej.
  `isladecalvos.admin`), declarados como constantes al principio de la clase.
- **Textos para jugadores por `lang`**. Excepción decidida: los nombres de los
  títulos van en la config, junto con sus tramos.
- **Nada de magic numbers** ajustables por el admin: van a la config.
- **Orden dentro de la clase** (con `#region`): campos/constantes → config →
  data → lang → hooks de ciclo de vida → hooks de juego → comandos → helpers.
- **Defensivo**: comprobar `null` en jugadores/entidades (desconexiones,
  entidades destruidas) y no hacer nada bloqueante en el hilo del servidor.
- **Limpieza**: todo lo que el plugin cree (UI, entidades) se destruye en
  `Unload()`. Los timers de `timer` los destruye Oxide al descargar.
- **Versionado**: SemVer en `[Info]`; se sube la versión en cada PR que cambie
  comportamiento.
- **Cero dependencias** de otros plugins salvo decisión explícita.

## 8. Compilación y pruebas

- **Oficial**: la única compilación que vale es la del propio Oxide en el
  servidor (copiar el `.cs` y mirar consola/logs).
- Las DLL de Rust (`Assembly-CSharp.dll`, etc.) son propietarias de Facepunch y
  las de Oxide vienen con el servidor: **no se suben al repo** (`.gitignore`
  ignora `*.dll`, `lib/` y `References/`).
- En el entorno cloud no hay ni DLL de Rust ni de Oxide (NuGet no las tiene;
  MyGet, umod.org y Steam están bloqueados). Lo máximo que se puede hacer ahí
  es compilar contra *stubs*:
  - clases de Oxide con las firmas copiadas de su código fuente;
  - clases de Rust con las firmas vistas en el código de Oxide.Rust o en el
    código descompilado de Oxide.Docs: `HitInfo.InitiatorPlayer`,
    `HitInfo.Initiator`, `BasePlayer.userID` (`EncryptedValue<ulong>`),
    `IsConnected`, `IsSleeping()`, `IsDead()`, `IsNpc`, `currentTeam`,
    `BasePlayer.FindByID`, `BaseNetworkable.ShortPrefabName`,
    `BaseEntity.OwnerID`, `RelationshipManager.ServerInstance.FindTeam`,
    `PlayerTeam.members`, `Vector3.Distance`, `transform.position`.
  - **No verificado**: `HitInfo.isHeadshot` (solo en plugins públicos
    antiguos), el tipo exacto de `PlayerTeam.members` (se asume `List<ulong>`)
    y la jerarquía de clases (qué hereda de qué). Si algo de esto no cuadra, el
    compilador de Oxide lo dirá al cargar.
- Además, en el entorno cloud se ejecuta un arnés de pruebas (fuera del repo)
  que llama a los hooks con stubs funcionales y comprueba los resultados:
  eventos compartidos, tiers, sin doble pago, config de muertes por NPC,
  debug. Prueba la lógica, no la integración con el juego.
