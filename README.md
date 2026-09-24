# isladecalvos

Plugin principal del servidor de Rust **Isla de Calvos**. Supervivencia, locuras
y calvicie en cantidades innecesarias. Todo por la patria capilar.

Hecho para **uMod/Oxide** en C#. De momento es solo el esqueleto: la
funcionalidad está por decidir.

## Instalación

1. Ten un servidor dedicado de Rust con **Oxide (uMod)** instalado.
2. Copia `src/IslaDeCalvos.cs` en la carpeta `oxide/plugins/` del servidor.
3. Oxide lo compila y carga solo. En la consola deberías ver algo como:
   `[Isla de Calvos] Isla de Calvos loaded.`
4. Para recargarlo tras cambiar el fichero (normalmente se recarga solo):
   `oxide.reload IslaDeCalvos`

Si falla la compilación, el error sale en la consola y en `oxide/logs/`.

## Documentación

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): cómo es un plugin de Oxide y
  las convenciones del proyecto.
- [`CLAUDE.md`](CLAUDE.md): contexto para sesiones de Claude.

## Licencia

[MIT](LICENSE)
