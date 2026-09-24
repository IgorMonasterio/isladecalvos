# CLAUDE.md — contexto para sesiones de Claude

## Proyecto

Plugin principal del servidor modded de Rust **Isla de Calvos**. Temática: todo
lo que pasa en la isla acaba teniendo que ver con el pelo o la calvicie.

## Premisa (lore)

- **Ser calvo es la gloria. El pelo es una maldición** que vuelve a crecer.
- Cada jugador tiene un **% de calvicie (0-100)** persistente. Lo glorioso
  (matar, sobrevivir) sube la calvicie; morir hace que crezca el pelo (baja).
- Es humor: **no hay cambio visual del pelo**. Todo es %, títulos y mensajes.

## Estado y hoja de ruta

Cada versión tiene su issue en GitHub. Solo se trabaja en lo que tenga el
alcance cerrado:

- **v1.0** — sistema de calvicie: %, títulos, ranking, anti-farmeo, comandos.
  Implementada (issue #2).
- **v1.1** — eventos globales (issue #3). Alcance por definir.
- **v1.2** — objetos malditos/lore: minoxidil, peluca, champú… (issue #4). Alcance por definir.
- **v1.3** — peluquería/NPC (issue #5). Alcance por definir.

Decisiones de diseño de la v1.0 que no están escritas en el código:
- Toda muerte resta calvicie (PvP, NPC, entorno, suicidio), también la de un
  sleeper.
- Matar a un sleeper o a un desconectado cuenta en las estadísticas, pero no
  da calvicie. El cooldown por víctima solo frena la calvicie, no las
  estadísticas.
- Una muerte por desangrado tras un derribo se atribuye a quien derribó.
- El reset por wipe solo pone a cero el %; las estadísticas se conservan.
- Los cambios hechos con `/calvoadmin` no generan anuncios globales.

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
  `resources/Rust.opj`). Si algo de la API de Rust no se puede verificar, se
  dice explícitamente.
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
