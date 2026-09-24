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
- Idea futura, **no implementar hasta que Igor lo decida**: la calvicie dará
  monedas por hora en la tienda de economía del servidor (issue de economía).
  Ni los números ni el nombre de la escala están decididos.

## Estado y hoja de ruta

Cada versión tiene su issue en GitHub. Solo se trabaja en lo que tenga el
alcance cerrado:

- **v1.0** — sistema de calvicie: puntos, títulos, ranking, anti-farmeo, comandos.
  Implementada (issue #2).
- **v1.1** — eventos globales (issue #3). Alcance por definir.
- **v1.2** — objetos malditos/lore: minoxidil, peluca, champú… (issue #4). Alcance por definir.
- **v1.3** — peluquería/NPC (issue #5). Alcance por definir.

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
- PvP: kill +800, headshot +1000. **Toda muerte −1000**, sin extra por
  headshot, sea cual sea la causa.
- Títulos por cada cero: 1, 10, 100 … 1.000.000.
- Se anuncia cada subida de título. Al llegar al más alto sale el mensaje de
  calvicie suprema en su lugar. Las bajadas también se anuncian.

## Stack (fijo)

- **uMod/Oxide + C#.** Nada de Carbon, ni APIs específicas de Carbon.
- La clase hereda de `RustPlugin`, namespace `Oxide.Plugins`.
- Oxide compila el `.cs` en el servidor; no hay proyecto .NET ni DLL que publicar.
- **Un plugin = un fichero** (verificado en el código de Oxide.CSharp). Nada
  de carpetas o ficheros extra que Oxide no vaya a cargar.
- Cero dependencias de otros plugins salvo decisión explícita de Igor.

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
- **Colecciones en la config**: siempre con
  `ObjectCreationHandling = ObjectCreationHandling.Replace` (ver ARCHITECTURE §3).
- **Flujo por PR**: cada cambio en una rama propia + Pull Request, con commits
  atómicos. Nunca se commitea ni se pushea directamente a `main`.
- **Nunca metas secretos en el repo**: ni contraseñas de RCON, ni tokens, ni
  IPs/puertos privados del servidor, ni ficheros `.env`. Tampoco DLL
  propietarias de Rust/Oxide.
- **Idioma**: documentación y conversación en español; código, identificadores
  y comentarios en inglés. Los textos para jugadores van por `lang`, en español
  por defecto (registrado en `es` y `en`, ver ARCHITECTURE).
- **Honestidad sobre la compilación**: en el entorno cloud no hay DLL de Rust
  ni de Oxide, así que no se puede afirmar que el plugin compila de verdad.
  Distingue siempre lo comprobado (stubs) de lo supuesto. La prueba real es
  cargarlo en el servidor.
- Sube la versión SemVer de `[Info]` en los PR que cambien comportamiento.
