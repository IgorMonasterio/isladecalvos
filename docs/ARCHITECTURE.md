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
| `OnDispenserGathered` / `OnDispenserBonusReceived(ResourceDispenser, BasePlayer, Item)` | Recolección, después de los multiplicadores de otros plugins (1.13.0, encargos). | `Rust.opj` + código descompilado (`ResourceDispenser.GiveResourceFromItem` / `AssignFinishBonus`) |
| `OnPlayerLanguageChanged(BasePlayer, string)` | El cliente cambia de idioma (1.13.0). | `RustHooks.cs` (`OnPlayerSetInfo`) |
| `OnRaidableBaseCompleted(...)` | Casa de Padre Jano completada (1.13.0, encargos). Hook de Raidable Bases, no de Oxide. | Código de Raidable Bases 3.1.2 (copia pública) |
| `OnIslaLanguageChanged(BasePlayer, string)` | Banderita de idioma del menú `/info` (1.13.0). Lo llama IslaInfo. | Encargo de Igor (issue #44) |

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

## 4n. El Calvario ×10, cambio en la escala ×10 y premios que se spawnean (plugin 1.11.0)

- **Cambio**: las tasas de la escala vieja (por 1 o por 100 de alopecia) no admiten
  ×10 en enteros, así que hay claves nuevas: `Sell: coins per 1000 baldness`,
  `Buy: RP per 10 baldness` y `Buy: coins per 10 baldness` (campos `SellCoinsPer1000`,
  `BuyRpPer10`, `BuyCoinsPer10`). `ExchangeAmountValid` pide múltiplos de 1.000 para
  vender por pelones y de 10 para comprar; `ExchangePrice` divide por 1.000 o por 10.
  Textos con claves nuevas: `BarberExSellCoinsV5`, `BarberExBuyRpV6`, `BarberExBuyCoinsV5`.
- **Migración 1110** (`MigrateExchangeTo1110`): las claves viejas se leen en campos
  `long?` con `NullValueHandling.Ignore` (`OldSellCoinsPer100`, `OldBuyRpPerBaldness`,
  `OldBuyCoinsPerBaldness`) y se ponen a `null` siempre después de validar, así que no
  vuelven al fichero. Si alguna estaba en el fichero: las renombradas conservan el
  número con la unidad nueva (25 por 100 → 25 por 1.000; 1 por 1 → 1 por 10) y
  `Sell: baldness for 1 RP` y `Minimum baldness to sell` se multiplican por 10. Sin
  claves viejas (config sin cambio, o ya migrada) no se toca nada. `Amounts offered`
  no cambia. Probado con la config del server de las 17:20, en imitación: sale 1.000 /
  25 / 1 / 25 / 1.000 y una segunda carga no cambia nada.
- **`MigrateMinicopterPrizes`** (misma migración): quita de cualquier premio por título
  los objetos `minicopter` (en el `items.json` de Oxide.Docs solo existe
  `minicopter.repair.item`, y Jano lo confirmó en el server) y, si el premio no tenía
  `Spawn prefabs`, le pone el helicóptero de combate.
- **`Spawn prefabs`** en `TierPrize` (también en el premio del Calvo del Día, que usa
  la misma clase). `GivePrize` devuelve cuántos no se pudieron spawnear;
  `ReplySpawnedPrize` manda `PrizeSpawned` o `PrizeSpawnFailed` después del mensaje
  del premio.
- **`SpawnPrizePrefab` / `TryFindSpawnSpot`**: dirección = `player.eyes.BodyForward()`
  sin componente vertical. Para 4, 7, 10, 14 y 18 m: `Physics.Raycast` hacia abajo
  desde 3 m sobre los pies del jugador (12 m de largo) con la máscara `1084293377`;
  se descarta si el suelo está por debajo de 0 (mar), si `Physics.CheckSphere` (3 m
  de radio, centro a 3,5 m sobre el suelo) toca algo con `1084293377 | 1218519041`,
  o si `GamePhysics.LineOfSight(player.eyes.position, centro, 1218519041)` es falso.
  Luego `GameManager.server.CreateEntity(prefab, sitio, Quaternion.LookRotation(dir))`,
  `OwnerID` y `Spawn()`. Si `CreateEntity` devuelve `null` (prefab mal escrito), aviso
  en consola.
- **Verificado** en el código del juego de `docs.json`: `GameManager.server.CreateEntity
  (string, Vector3, Quaternion)` + `Spawn()` (`CH47HelicopterAIController`, `CargoPlane`),
  `eyes.BodyForward()` y `eyes.position`, `Physics.Raycast(origen, dirección, out hit,
  distancia, máscara)`, `GamePhysics.LineOfSight(a, b, máscara)`. Las dos máscaras son
  las que usa el juego: `1084293377` en `HackableLockedCrate.LandCheck` (capas 0, 8,
  16, 21, 23 y 30: Default, Deployed, World, Construction, Terrain y Tree, según los
  nombres de capa de Rust que conozco) y `1218519041` en la comprobación de visibilidad
  previa a `OnEntityVisibilityCheck`.
- **No verificado**: `Physics.CheckSphere` (es la API estándar de Unity, pero no sale
  en `docs.json`); que el mar esté en y = 0 (lagos y ríos por encima no se detectan
  como agua); y que 3 m de radio basten para el helicóptero de combate (las aspas son
  más anchas). La prueba real es en el server.

## 4o. Comandos JSON para la web (plugin 1.12.0)

- `isla.salon` (sin argumentos; con `cerrar` sigue siendo el cierre de mapa),
  `isla.calvodeldia`, `isla.cabezas` e `isla.evento`: `[ConsoleCommand]` como
  `isla.ranking`, rechazados con `arg.Connection != null` y contestados con
  `arg.ReplyWith(JsonConvert.SerializeObject(..., Formatting.None))`. **Sin
  SteamIDs**: los nombres salen de `PlayerData.Name` o de los guardados en el salón y
  en el historial.
- Horas con `IsoUtc`: `ToUniversalTime()` + `yyyy-MM-ddTHH:mm:ssZ`; un `DateTime` por
  defecto ("nunca") da `null`. Las fechas guardadas son hora del server (`DateTime.Now`),
  así que la conversión usa la zona del server.
- Datos nuevos solo para estos comandos (el juego no cambia):
  - `StoredData.BountyInfo` (`BountyRecord`: `Since` y `PlacedBy`), que se rellena en
    `/cabeza` y se borra con el bote (al cobrarlo o con `cabeza quitar`). Un bote de
    antes de la 1.12.0 no tiene registro; si alguien lo sube, se crea con `Since` por
    defecto (no se inventa la fecha).
  - `eventEndsUtc` (al empezar un evento) y `nextRandomEventUtc` (al cargar y en cada
    vuelta del `timer.Every` de eventos), solo en memoria.
- `NextCalvoDelDiaPick`: hoy a la hora de elección, mañana si `LastPickDate` es hoy, o
  "ahora" si ya pasó la hora sin elegir (`CheckCalvoDelDia` elige en el siguiente minuto).
- Probado en imitación (mono + Newtonsoft, con los datos y los cuatro métodos sacados
  tal cual): formato, orden, `null` donde toca, y que con conexión de jugador no se
  contesta. **No verificable**, igual que con `isla.ranking`: cómo devuelve el RCON de
  Facepunch el texto de `ReplyWith`.

## 4p. Venganza, encargos, seguro, kit y tres idiomas (plugin 1.13.0)

Issue #44. Todo lo nuevo, en la config (versión 1130) con los valores del issue.

- **Venganza capilar**: `grudges` (en memoria) guarda, por víctima, quién la mató y cuándo
  (solo la última vez de cada pareja). En `OnPlayerDeath`, tras contar la kill y el bote,
  `HasGrudge(asesino, víctima)` mira si la víctima mató al asesino hace menos de `Window`;
  después se apunta el agravio nuevo (la víctima ahora debe una). Si la kill paga
  (`IsKillRewardable`, el de siempre: durmientes, cooldown) y la recompensa es > 0, se
  multiplica, se gasta el agravio y sale `RevengeChat` a todos. Si no paga, el agravio no se
  gasta. Pasa por `GainBaldness`, así que la Hora de la calvicie y la pila también multiplican.
- **Kit de consuelo**: `CountDeathForKit` en cada muerte que no sea suicidio (asesino = la
  propia víctima, la regla de siempre; que `/kill` llegue así no se ha podido verificar).
  Con `Deaths` muertes en `Within` minutos (y fuera del enfriamiento,
  `PlayerData.LastConsolationKit`), el jugador entra en `pendingKits`; en el siguiente
  `OnPlayerSleepEnded` vivo, `GiveConsolationKit` le da los objetos con `GiveItem` sin marca
  (`PlayerInventory.GiveItem`: no sale el "SERVER gave you" de `inventory.giveto`) y el
  mensaje `ConsolationKit`. Las muertes que lo ganaron empiezan de cero.
- **Seguro capilar**: página `Insurance` del barbero y comando `calvos.insurance`. El precio
  (`InsurancePrice`: `max(mínimo, ceil(alopecia / 1000 × recargo))`) que ve el jugador queda
  en `insuranceQuotes`; al confirmar se recalcula: si ha subido, se le enseña el nuevo sin
  cobrar (`BarberInsuranceRepriced`); si no, `TakeRp` y `HasDeathShield = true`. Con la cinta
  ya puesta no deja comprar (`ItemShieldAlready`).
- **Encargos del Barbero**: catálogo en la config (`JobDefinition`; `ValidateJobs` descarta
  con aviso los que no sirven). `PlayerData.Jobs` guarda el día de encargos (fecha UTC del
  último reinicio a `New jobs every day at`) y, por encargo, `Id`, `Progress`, `Done` y
  `Claimed`. `EnsureJobs` reparte `Jobs per day` distintos al azar al cambiar de día, entre
  los que se pueden hacer (`JobAvailable`: el monumento está en el mapa; Raidable Bases
  cargado para los de bases). Lo no cobrado del día anterior se pierde. `AddJobProgress`
  suma (o, con `absolute`, sube al valor actual) y avisa en el chat al completar.
  - Barriles: el mismo `LootContainer` con `barrel` de los objetos del Calvario.
  - Científicos: NPC `BasePlayer` con `scientist` en el prefab (las torretas `sentry.*` no
    son jugadores). Con `Monument`, la posición del NPC al morir tiene que estar dentro de
    un monumento con ese `displayPhrase.english` (`MonumentInfo.IsInBounds`). `LoadMonuments`
    los agrupa por nombre desde `TerrainMeta.Path.Monuments` en `OnServerInitialized` y
    avisa de los encargos cuyo monumento no está en el mapa (con la lista de los que hay).
    **Verificado** en plugins públicos que compilan en el Rust actual (MonumentFinder,
    MonumentPlayerSettings y Raidable Bases 3.1.2, que usa `displayPhrase.english`), no en
    las fuentes oficiales: `MonumentInfo` no sale en `docs.json`.
  - Animales: `Animal prefabs` de la config (por `ShortPrefabName`, como `NpcTiers`).
  - Recolección: `OnDispenserGathered` y `OnDispenserBonusReceived` (`ResourceDispenser`,
    `BasePlayer`, `Item`; `Rust.opj` y `docs.json`). Llegan **después** de
    `OnDispenserGather`, que es donde los plugins de recolección (GatherManager…)
    multiplican, así que el objeto ya trae lo que se lleva el jugador. Recoger del suelo
    (`OnCollectiblePickup`) no cuenta.
  - Supervivencia: en `SurvivalTick`, cada minuto vivo en el que el jugador se ha movido
    (`TrackMovement`, la misma comprobación de la paga) suma 1 a `surviveStreaks`; un minuto
    quieto no suma ni corta; morir o desconectarse lo pone a 0. En memoria.
  - Casas de Padre Jano: hook `OnRaidableBaseCompleted` de Raidable Bases, visto en su
    código 3.1.2 (copia pública): 17 argumentos, en este orden, y Oxide **descarta los que
    el método no declara** (`CSPlugin`, Oxide.Core), así que se declaran los 10 primeros.
    Cuentan los `raiders` y el `owner`. `mode` es la dificultad (0-4) en las versiones que
    la tienen; la 3.x ya no tiene dificultades y manda siempre 512, y entonces cualquier
    base vale para cualquier encargo de bases. La versión del server no se ha comprobado.
  - Cobro: página `Jobs` del barbero (`calvos.jobs claim <id>`), `AddRp`. Se ven también en
    la pestaña ENCARGOS de `/calvos` (`calvos.tab encargos`); con cuatro pestañas son más
    estrechas y el Calvo del Día se corre a la derecha.
- **Tres idiomas** (ver §5): textos en `es` y `es-ES` (español), `en` (inglés de verdad) y
  `ru` (ruso). `OnIslaLanguageChanged(BasePlayer, string)` (lo llaman las banderitas de
  IslaInfo después de `lang.SetLanguage`) y `OnPlayerLanguageChanged(BasePlayer, string)`
  (Oxide.Rust, `RustHooks.OnPlayerSetInfo`, cuando el cliente cambia `global.language`)
  redibujan contador y cartera al momento. Una ventana abierta cambia en su siguiente clic.
  **Ojo**: Oxide.Rust vuelve a poner el idioma del cliente en cada conexión
  (`IOnPlayerConnected`), así que lo elegido con una banderita dura hasta que el jugador
  se reconecta (lo resuelve IslaInfo, no este plugin).
- **Migración 1130**: solo añade. Las cuatro secciones nuevas salen solas (Newtonsoft deja
  el valor por defecto en las claves que faltan) y el premio de Greñas Sucias gana su
  `Message in other languages` si su `Message` sigue siendo el de por defecto y no tiene
  traducciones. Probado en imitación con la config por defecto de la 1.12.0: todo lo que ya
  estaba queda igual, el premio del Calvo del Día solo gana la clave nueva vacía y una
  segunda carga no cambia nada.

## 5. Localización (lang)

- Todos los textos del plugin pasan por `lang`: se registran en
  `LoadDefaultMessages()` con `lang.RegisterMessages(...)` y se leen con
  `lang.GetMessage(clave, this, userId)`.
- Oxide genera `oxide/lang/<idioma>/IslaDeCalvos.json`, que el admin puede
  editar sin tocar código.
- **Tres idiomas desde la 1.13.0.** *Verificado en `Oxide.Rust` y `Oxide.Core`*:
  Oxide asigna a cada jugador el idioma de su cliente de Rust y, si no existe el
  fichero de ese idioma, usa el del idioma del servidor (`en`), **no** `es`
  (`Lang.GetMessageKey`). Los clientes españoles llegan como `es-ES`, así que el
  español se registra en `es` y en `es-ES`; `en` es inglés y `ru`, ruso
  (`SpanishMessages`, `EnglishMessages`, `RussianMessages`, con las mismas claves).
  Cualquier otro idioma lee el inglés. Hasta la 1.12.0 el español se registraba
  también como `en`; Oxide no pisa claves que ya existen, así que en un servidor que
  venga de antes hay que borrar `oxide/lang/en/IslaDeCalvos.json` para que se
  regenere en inglés.
- **Texto por lector (`Txt`)**: lo que se manda a todos (anuncios, carteles de evento,
  título, Calvo del Día, venganza…) va jugador por jugador (`Broadcast` hace un `Reply`
  a cada conectado; `ShowBanner` recibe un `Txt`). Un `Txt` es un texto sin resolver:
  `T(clave, args)`, `Num`, `Compact`, `UnitTxt`, `DateTxt`, `TitleTxt`… y `Lang`
  resuelve cada argumento `Txt` con el id de quien lo lee. Los motivos del debug son
  `Txt` también, así que cada admin los lee en su idioma. Sin lector (`userId` nulo:
  consola, RCON, JSON de la web, hooks) sale en español (`GetMessageByLanguage`, en
  Oxide.Core desde 2024).
- **Números, fechas y plurales** siguen la clave `LanguageCode` del fichero que lee el
  jugador (`es`, `en` o `ru`; `TextLanguage`), así que nunca discrepan del texto que los
  rodea: 1.000.000 / 1,000,000 / 1 000 000, cifras cortas por idioma, fechas por idioma
  y, en ruso, tres formas (`UnitRpOneV2`, `UnitRpFew`, `UnitRpV2`; igual con los pelones).
- **Títulos** desde el lang (`Title_<alopecia mínima>`, `TitleText`), en todos los
  idiomas, también el español. El nombre de la config solo sale si el lang no tiene la
  clave (un título nuevo o con el tramo cambiado), y es el que usan `isla.ranking`,
  `isla.salon` y `OnIslaTitleChanged`, que siguen en español. Para renombrar un título
  ya desplegado: clave nueva, como cualquier texto.
- **Textos que viven en la config**: el `Message` de un premio y el `Text` de un encargo
  están en español y tienen al lado `Message in other languages` / `Text in other
  languages` (`"en"`, `"ru"`…), que se eligen por `LanguageCode` (`ConfigText`); si no
  hay del idioma, sale el español. No van al lang porque son de cada premio o encargo, y
  Oxide borra del fichero de lang las claves que el plugin no registra.
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
- **Idioma**: código, identificadores y comentarios en inglés; documentación en
  español; textos para jugadores en español, inglés y ruso (§5).
- **Permisos**: siempre con el prefijo `isladecalvos.` (p. ej.
  `isladecalvos.admin`), declarados como constantes al principio de la clase.
- **Textos para jugadores por `lang`**, también los títulos desde la 1.13.0 (§5). Los
  únicos en la config son el mensaje de cada premio y la coletilla de cada encargo,
  con su versión por idioma al lado.
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
