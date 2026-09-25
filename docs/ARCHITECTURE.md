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
| `OnPlayerSleepEnded(BasePlayer player)` | El jugador despierta (tras conectar y tras cada respawn). Es cuando se dibuja el contador en pantalla. | Código descompilado (`BasePlayer.EndSleeping`) |
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
- La Lluvia de champú multiplica en `OnPlayerDeath`. El Brote de alopecia, en
  `CompleteEventTarget` (recompensa compartida).
- Para añadir un evento: nuevo valor en el enum `GlobalEvent`, su bloque en la
  config, su `case` en `StartEvent`/`EndEvent` y sus textos en `lang`.

## 4d. Interfaz en pantalla (v1.2)

- Se usa la CUI de Oxide.Rust (`Oxide.Game.Rust.Cui`: `CuiHelper.AddUi`/
  `DestroyUi`, `CuiElementContainer`, `CuiPanel`, `CuiLabel`), verificada en
  `src/RustCui.cs`.
- Tres elementos con nombre fijo: `IslaDeCalvos.Counter`, `IslaDeCalvos.Delta`
  e `IslaDeCalvos.Banner`. Se añaden con `destroyUi` = su propio nombre, así
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
- Repartos: barriles en `OnEntityDeath` (un `LootContainer` cuyo prefab
  contiene `barrel`), NPCs tras cobrar su tier, y placas rojas en
  `PayEventReward` para cada jugador pagado que esté conectado.
- Ventanas: CUI sobre `Overlay` con `CursorEnabled`. Los botones llaman a
  comandos de consola del plugin (`calvos.tab` para el ranking;
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

## 4f. RP de Server Rewards (plugin 1.4.0)

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
- Los RP no pasan por `GainBaldness` ni `ChangeBaldness`: no tocan la calvicie
  ni se multiplican.

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
- `/calvos` abre solo el ranking (`OpenRanking`).
- Mercalvona (GUIShop), Premios Calvos (Server Rewards) y el TP `/peluqueria`
  (NTeleportation, "Dynamic Commands") son configuración de esos plugins; el
  plugin no los llama. Los pasos están en el README.

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
  prefijo `Calvario*`.
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
