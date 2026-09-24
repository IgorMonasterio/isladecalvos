# CLAUDE.md — contexto para sesiones de Claude

## Proyecto

Plugin principal del servidor modded de Rust **Isla de Calvos**. Temática: todo
lo que pasa en la isla acaba teniendo que ver con el pelo o la calvicie.

## Stack (fijo)

- **uMod/Oxide + C#.** Nada de Carbon, ni APIs específicas de Carbon.
- La clase hereda de `RustPlugin`, namespace `Oxide.Plugins`.
- Oxide compila el `.cs` en el servidor; no hay proyecto .NET ni DLL que publicar.

## Estructura

```
src/IslaDeCalvos.cs     # el plugin (nombre de fichero = nombre de clase)
docs/ARCHITECTURE.md    # cómo es un plugin Oxide y nuestras convenciones
README.md               # descripción e instalación
```

Lee `docs/ARCHITECTURE.md` antes de tocar código.

## Reglas

- **No inventes features.** La funcionalidad no está decidida; solo se
  implementa lo que Igor pida explícitamente. Ante la duda, pregunta.
- **Flujo por PR**: cada cambio en una rama propia + Pull Request. Nunca se
  commitea ni se pushea directamente a `main`.
- **Nunca metas secretos en el repo**: ni contraseñas de RCON, ni tokens, ni
  IPs/puertos privados del servidor, ni ficheros `.env`. Tampoco DLL
  propietarias de Rust/Oxide.
- **Idioma**: documentación y conversación en español; código, identificadores
  y comentarios en inglés. Los textos para jugadores van por `lang` (es + en).
- **Honestidad sobre la compilación**: en el entorno cloud no hay DLL de Rust
  ni de Oxide, así que no se puede afirmar que el plugin compila de verdad.
  Distingue siempre lo comprobado (sintaxis, stubs) de lo supuesto, y la
  prueba real es cargarlo en el servidor.
- Sube la versión SemVer de `[Info]` en los PR que cambien comportamiento.
