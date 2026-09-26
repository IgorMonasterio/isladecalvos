# CLAUDE.md — contexto para sesiones de Claude

## Proyecto

Plugin principal del servidor modded de Rust **Isla de Calvos**. Temática: todo
lo que pasa en la isla acaba teniendo que ver con el pelo o la calvicie.

## Premisa (lore)

- **Ser calvo es la gloria. El pelo es una maldición** que vuelve a crecer.
- Cada jugador tiene una **puntuación de calvicie**: entero persistente, **sin
  límite por arriba**, nunca por debajo de 0. Lo glorioso (matar, sobrevivir)
  la sube; morir hace que crezca el pelo (la baja).
- Es humor: **no hay cambio visual del pelo**. Todo son puntos, títulos y mensajes.
- La calvicie da **RP de Server Rewards** cada 30 min: 1 RP por cada 100 de
  calvicie, sin tope (lineal desde la 1.6.0; antes, por escalones de título).
  1 RP = 10 monedas. Desde la 1.6.0 hay además un cambio de calvicie ↔ RP ↔
  monedas en El Calvario.

## Estado y hoja de ruta

Cada versión tiene su issue en GitHub. Solo se trabaja en lo que tenga el
alcance cerrado:

- **v1.0** — sistema de calvicie: puntos, títulos, ranking, anti-farmeo, comandos.
  Implementada (issue #2).
- **v1.1** — eventos globales (issue #3): Hora de la calvicie, Lluvia de
  champú, Cazar al más peludo y Tormenta de cuchillas (hasta la 1.6.1, Brote
  de alopecia). Uno al azar cada hora.
  Implementada.
- **v1.2 (plugin 1.2.0)** — en pantalla: contador de calvicie fijo abajo a la
  derecha con popup `+X`/`-X`, y los eventos globales en un cartel grande en
  el centro. (Nota: la numeración del plugin ya no coincide con la de los
  issues de la hoja de ruta.)
- **Plugin 1.3.0** — **El Calvario** (issue #4): objetos que existen en Rust
  pero no salen en servidores normales (lejía, cinta, pila, placas, gemas,
  tarjetas de color), repartidos por el plugin y usados desde `/calvos`;
  Carné de Calvo. `/calvos` es **el único comando de jugador**: ventana con
  pestañas Objetos y Ranking (`/calvo` desapareció).
- **Plugin 1.4.0 — Economía (#8)**: RP de **Server Rewards** (`AddPoints`)
  por título, confirmado por Igor. Cada 30 min vivo, conectado y no AFK
  (que se haya movido): Coronilla 1, Caballero 3, Lord 10, Majestad 30. Sin
  Server Rewards cargado no se paga nada. Ver ARCHITECTURE §4f.
- **Plugin 1.5.0 — La peluquería (issue #5)**, decidido por Igor: tres
  casitas con NPC de HumanNPC (Calvario → este plugin; Mercalvona → GUIShop;
  Premios Calvos → Server Rewards 2.0.8). TP con `/peluqueria` (Dynamic Command de
  NTeleportation). `/shop` y `/s` solo en sus NPC. `/calvos` solo muestra el
  ranking; los objetos se usan hablando con el barbero. El plugin solo
  escucha `OnUseNPC` de HumanNPC; lo demás es config de esos plugins.
  (1.5.1: El Calvario es una conversación con el barbero, al estilo de los
  dependientes vanilla.)
- **Plugin 1.6.0** — encargo de Igor: cartel grande al subir de título; premios
  por título (RP, monedas, objetos; una vez por jugador y título, todo a 0 por
  defecto); RP lineal (`floor(calvicie/100)`); títulos ×10 por defecto; y
  **cambio de calvicie** en el barbero, con tasas asimétricas y confirmación.

Decisiones de diseño de la v1.0 que no venían en la especificación inicial:
- Toda muerte resta calvicie (PvP, NPC, entorno, suicidio), también la de un
  sleeper.
- Matar a un sleeper o a un desconectado cuenta en las estadísticas, pero no
  da calvicie. El cooldown por víctima solo frena la calvicie, no las
  estadísticas.
- Una muerte por desangrado o rendición tras un derribo se atribuye a quien derribó.
- El reset por wipe solo pone a cero la calvicie; las estadísticas se conservan.
- Los cambios hechos con `/calvoadmin` no generan anuncios globales.

Cambio de diseño de la v1.0 (el servidor tiene pocos jugadores, así que los
NPCs tienen que dar calvicie):
- NPCs por **tiers 1-20**: `NpcTiers` (por `ShortPrefabName`, nunca por la ruta
  completa) y `TierRewards` (calvicie por tier: de 1 a 1000).
- NPC no listado → no da nada, y se loguea una vez en consola.
- `npc_bandit_guard` y `sentry.*` están en la config pero desactivados
  (`DisabledNpcs`).
- **Recompensas de evento** (sistema genérico, ver ARCHITECTURE 4b): heli,
  Bradley y CH47 pagan su tier completo a todos los que les hicieron daño y a
  sus compañeros de equipo conectados cerca (300 m), una vez por jugador. Se
  añadirán más eventos de equipo reutilizando ese sistema.
- Morir a manos de un NPC resta como cualquier muerte (configurable).
- `/calvoadmin debug on|off` muestra en el chat cada cambio y su motivo.
- Las estadísticas (kills, headshots) siguen siendo solo contra jugadores.

Cambio de escala (de % a puntos enteros sin límite):
- PvP: **toda kill +1000** (headshot o no). **Toda muerte −10 % de la
  calvicie actual** (redondeado hacia arriba), sea cual sea la causa;
  Lluvia de champú −20 %.
- Supervivencia: +100 cada 30 min vivo y conectado.
- Títulos por cada cero, de mucho pelo a nada, con humor calvo británico.
  Desde la 1.6.0: Greñas Sucias (1), Pelambrera Lamentable (1.000), Entradas
  Incipientes (10.000), Coronilla a la Intemperie (100.000), Caballero de la
  Tonsura (1.000.000), Lord Bola de Billar (10.000.000), Su Calvísima
  Majestad (100.000.000).
- Se anuncia cada subida de título. Al llegar al más alto sale el mensaje de
  calvicie suprema en su lugar. Las bajadas también se anuncian.

## Stack (fijo)

- **uMod/Oxide + C#.** Nada de Carbon, ni APIs específicas de Carbon.
- La clase hereda de `RustPlugin`, namespace `Oxide.Plugins`.
- Oxide compila el `.cs` en el servidor; no hay proyecto .NET ni DLL que publicar.
- **Un plugin = un fichero** (verificado en el código de Oxide.CSharp). Nada
  de carpetas o ficheros extra que Oxide no vaya a cargar.
- Cero dependencias de otros plugins salvo decisión explícita de Igor. Las
  decididas son **Server Rewards** y **Economics** (1.6.0), ambas blandas
  (`[PluginReference]`): si no están cargadas, el plugin funciona igual, pero
  sin RP o sin monedas. HumanNPC no es dependencia de código: solo se escucha
  su hook `OnUseNPC`.

## Estructura

```
src/IslaDeCalvos.cs     # el plugin entero, organizado con #region
docs/ARCHITECTURE.md    # cómo es un plugin Oxide, hooks verificados y convenciones
README.md               # descripción, instalación, comandos, permisos, config
```

Lee `docs/ARCHITECTURE.md` antes de tocar código.

## Reglas

- **No inventes features.** Solo se implementa lo que Igor pida
  explícitamente. Ante la duda, pregunta.
- **No inventes APIs.** Los hooks y métodos de Oxide se verifican contra su
  código fuente (OxideMod/Oxide.Core, Oxide.CSharp, Oxide.Rust y su
  `resources/Rust.opj`). La API de Rust se verifica en el `docs.json` de
  OxideMod/Oxide.Docs, que trae el código descompilado del juego alrededor de
  cada hook. Todos son repos públicos que se pueden clonar en solo lectura. Si
  algo no se puede verificar, se dice explícitamente.
- **Cambiar valores ya desplegados**: los configs existentes no se
  sobrescriben. Para forzar un valor nuevo, subir `CurrentConfigVersion` y
  migrar en `ValidateConfig` (ver la migración 131).
- **Cambiar un texto ya desplegado**: Oxide conserva el texto viejo si la
  clave ya existe en `oxide/lang/*`. Para que el nuevo llegue solo, **cambiar
  el nombre de la clave** (Oxide añade las nuevas y borra las que ya no
  existen). Así se hizo con las claves `Calvario*` en la 1.3.1.
- **Espacio en pantalla**: abajo, entre el cinturón y las barras, lo usa el
  panel de RaidableBases; arriba en el centro, otro panel de RaidableBases.
  El contador va arriba a la derecha.
- **Contexto del servidor** (a 2026-09-24): 1-3 jugadores, x5 gather, loot x1
  (BetterLoot), StackSizeController x5, RaidableBases (NPCs `scientistnpc_heavy`,
  skinID 3710562502), Economics + GUIShop (monedas) y ServerRewards **2.0.8**
  (RP, 1 RP = 10 monedas; sus claves de lang no son las de la v1, p. ej.
  `Message.Notification.Unspent.NPC`). No hay otros plugins que den
  recompensas por kills.
- **Objetos del juego**: el tipo `Item` se escribe `global::Item`, porque
  `RustPlugin` tiene un campo llamado `Item`.
- **Argumentos de comandos de consola**: `arg.FullString` siempre con
  `.ToString()` antes de usarlo como `string` (p. ej. `Split`). En el Rust
  actual `ConsoleSystem.Arg.FullString` es un `StringView`, no un `string`, y
  sin `.ToString()` no compila en el servidor real (lo detectó Igor en la
  1.3.1). Las imitaciones con las que se compila aquí no lo detectan.
- **Colecciones en la config**: siempre con
  `ObjectCreationHandling = ObjectCreationHandling.Replace` (ver ARCHITECTURE §3).
- **Flujo por PR**: cada cambio en una rama propia + Pull Request, con commits
  atómicos. Nunca se commitea ni se pushea directamente a `main`.
- **Nunca metas secretos en el repo**: ni contraseñas de RCON, ni tokens, ni
  IPs/puertos privados del servidor, ni ficheros `.env`. Tampoco DLL
  propietarias de Rust/Oxide.
- **Nada de emojis en textos para jugadores**: el chat de Rust no los dibuja
  (salen como `??`, comprobado en el servidor). Para resaltar, usar `<color>`.
- **Ganancias de calvicie siempre por `GainBaldness`** (para que los eventos
  puedan multiplicarlas). `ChangeBaldness` directo solo para admin, muertes,
  premios de eventos y calvicie comprada en el cambio (esta con `bought: true`,
  para que no cobre premios de título).
- **Mensajes al chat siempre por `Broadcast`/`SendChat`** del plugin (nunca
  `PrintToChat`/`SendReply` directos): así llevan como icono el avatar de la
  cuenta de Steam de la isla (`ChatIconSteamId`, 76561198635630459). Es el
  SteamID que se pasa a `chat.add` (verificado en Oxide.Rust `Server.Broadcast`
  / `Player.Message`).
- **Idioma**: documentación y conversación en español; código, identificadores
  y comentarios en inglés. Los textos para jugadores van por `lang`, en español
  por defecto (registrado en `es` y `en`, ver ARCHITECTURE).
- **Honestidad sobre la compilación**: en el entorno cloud no hay DLL de Rust
  ni de Oxide, así que no se puede afirmar que el plugin compila de verdad.
  Distingue siempre lo comprobado (stubs) de lo supuesto. La prueba real es
  cargarlo en el servidor.
- Sube la versión SemVer de `[Info]` en los PR que cambien comportamiento.
