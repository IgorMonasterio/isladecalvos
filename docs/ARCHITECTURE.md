# Arquitectura del plugin

Este documento explica cómo funciona un plugin de uMod/Oxide para Rust y qué
convenciones seguimos en **Isla de Calvos**. No describe funcionalidad: todavía
no hay ninguna decidida.

> ⚠️ Las referencias a la API de Oxide de este documento se han escrito de
> memoria y **no se han podido contrastar** con la documentación oficial
> (umod.org estaba bloqueado desde el entorno en el que se redactó). Antes de
> apoyarse en una firma concreta, compruébala en <https://umod.org/documentation>
> o en el código de Oxide.

## 1. Cómo carga Oxide un plugin

- Un plugin es **un único fichero `.cs`** que se deja en `oxide/plugins/` del
  servidor.
- Oxide lo **compila en caliente** con su propio compilador al arrancar y cada
  vez que el fichero cambia (hot reload). No hay `.csproj` ni DLL que distribuir.
- El **nombre del fichero debe coincidir con el nombre de la clase**:
  `IslaDeCalvos.cs` → `class IslaDeCalvos`.
- La clase vive en el namespace `Oxide.Plugins` y hereda de `RustPlugin`
  (API específica de Rust; `CovalencePlugin` sería la alternativa multijuego,
  que no usamos).
- Metadatos obligatorios mediante atributos:
  - `[Info("Título", "Autor", "x.y.z")]`
  - `[Description("...")]`
- Comandos útiles en la consola del servidor:
  `oxide.reload IslaDeCalvos`, `oxide.load ...`, `oxide.unload ...`.
  Los errores de compilación aparecen en la consola y en `oxide/logs/`.

## 2. Hooks

Los hooks son **métodos privados con un nombre concreto** que Oxide invoca por
reflexión cuando ocurre algo. No se registran: basta con declararlos.

Ciclo de vida (los que usaremos seguro):

| Hook | Cuándo se llama |
|---|---|
| `Init()` | Al cargar el plugin, antes de que el servidor esté listo. Registrar permisos, leer config. |
| `OnServerInitialized()` | Cuando el servidor ya está listo (o inmediatamente si el plugin se recarga en caliente). Aquí es seguro tocar entidades y jugadores. |
| `Unload()` | Al descargar/recargar el plugin. Limpiar timers, UI, entidades propias y guardar datos. |
| `OnServerSave()` | Cada guardado periódico del servidor. Buen momento para persistir datos. |

Hay cientos de hooks de juego (`OnPlayerConnected`, `OnEntityDeath`, …). En
muchos, **devolver un valor distinto de `null` cancela o modifica el
comportamiento por defecto**, así que un `return` descuidado puede romper
mecánicas del juego. Cada hook se documentará en el PR que lo introduzca.

## 3. Configuración

- Fichero: `oxide/config/IslaDeCalvos.json` (lo edita el admin del servidor).
- Patrón habitual: una clase `Configuration` tipada, `LoadDefaultConfig()` para
  generarla, `Config.ReadObject<Configuration>()` al cargar y
  `Config.WriteObject(...)` para guardarla.
- Si el JSON está corrupto o incompleto, se avisa con `PrintWarning` y se
  regenera/completa con valores por defecto; nunca se deja el plugin a medias.

## 4. Datos persistentes

- Ficheros en `oxide/data/` mediante
  `Interface.Oxide.DataFileSystem.ReadObject<T>(nombre)` / `WriteObject(nombre, obj)`.
- Se distinguen claramente: **config** = lo que decide el admin; **data** = el
  estado que genera la partida.
- Se guarda en `OnServerSave()` y en `Unload()`; no en cada evento.

## 5. Localización (lang)

- Todos los textos que ve un jugador pasan por el sistema `lang` de Oxide:
  se registran en `LoadDefaultMessages()` con `lang.RegisterMessages(...)` y se
  leen con `lang.GetMessage(clave, this, userId)`.
- Oxide genera `oxide/lang/<idioma>/IslaDeCalvos.json`, que el admin puede
  editar sin tocar código.
- Idiomas: **`es` es el principal**; se registra también `en` como respaldo.

## 6. Permisos

- Se registran en `Init()` con `permission.RegisterPermission(nombre, this)` y se
  comprueban con `permission.UserHasPermission(userId, nombre)`.
- Se asignan desde la consola: `oxide.grant user|group <quién> <permiso>`.

## 7. Convenciones del proyecto

- **Un solo plugin, un solo fichero**: `src/IslaDeCalvos.cs`. Si crece demasiado
  se discutirá dividirlo en varios plugins antes de hacerlo.
- **Idioma**: código, identificadores y comentarios en inglés; documentación y
  textos para jugadores en español (vía `lang`).
- **Permisos**: siempre con el prefijo `isladecalvos.` (p. ej.
  `isladecalvos.admin`), declarados como constantes al principio de la clase.
- **Nada de textos hardcodeados** para el jugador: todo por `lang`.
- **Nada de magic numbers** ajustables por el admin: van a la config.
- **Orden dentro de la clase** (con `#region`): campos/constantes → config →
  data → lang → hooks de ciclo de vida → hooks de juego → comandos → helpers.
- **Defensivo**: comprobar `null` en jugadores/entidades (desconexiones,
  entidades destruidas) y no hacer nada bloqueante en el hilo del servidor.
- **Limpieza**: todo lo que el plugin cree (timers, UI, entidades) se destruye
  en `Unload()`.
- **Versionado**: SemVer en `[Info]`; se sube la versión en cada PR que cambie
  comportamiento.
- **Temática**: todo acaba teniendo que ver con el pelo o la calvicie, pero la
  funcionalidad concreta se decide explícitamente antes de programarla.

## 8. Compilación y pruebas

- **Oficial**: la única compilación que vale es la del propio Oxide en el
  servidor (copiar el `.cs` y mirar consola/logs).
- Las DLL de Rust (`Assembly-CSharp.dll`, etc.) son propietarias de Facepunch y
  las de Oxide vienen con el servidor: **no se suben al repo** (`.gitignore`
  ignora `*.dll`, `lib/` y `References/`).
