# Arquitectura del plugin

Este documento explica cómo funciona un plugin de uMod/Oxide para Rust y qué
convenciones seguimos en **Isla de Calvos**. La funcionalidad está descrita en
el [README](../README.md) y en los issues de cada versión.

> ℹ️ **Fuentes.** Las APIs de Oxide que se citan aquí están verificadas contra
> el código fuente de [Oxide.Core](https://github.com/OxideMod/Oxide.Core),
> [Oxide.CSharp](https://github.com/OxideMod/Oxide.CSharp) y
> [Oxide.Rust](https://github.com/OxideMod/Oxide.Rust) (incluido
> `resources/Rust.opj`, que define dónde se inyecta cada hook). La
> documentación de umod.org no se ha podido consultar porque estaba bloqueada
> desde el entorno de trabajo. La API del propio juego (clases de Rust) solo se
> puede verificar en parte: ver sección 8.

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
| `OnPlayerDeath(BasePlayer player, HitInfo info)` | Muerte de cualquier `BasePlayer` (también NPCs humanos). | `Rust.opj` (`BasePlayer.Die`) |

## 3. Configuración

- Fichero: `oxide/config/IslaDeCalvos.json` (lo edita el admin del servidor).
- Patrón: una clase `Configuration` tipada; `LoadDefaultConfig()` la crea con
  valores por defecto, `Config.ReadObject<Configuration>()` la lee al cargar y
  `Config.WriteObject(...)` la guarda.
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
  - clases de Rust con las firmas **supuestas**. De estas, `HitInfo.InitiatorPlayer`,
    `BasePlayer.userID` (`EncryptedValue<ulong>`), `IsConnected`, `IsDead()`,
    `displayName`, `UserIDString` y `activePlayerList` aparecen en el código de
    Oxide.Rust. `HitInfo.isHeadshot` y `BasePlayer.IsSleeping()` solo se han
    visto en plugins públicos antiguos.
