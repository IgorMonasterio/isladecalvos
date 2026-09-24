# isladecalvos

Plugin principal del servidor de Rust **Isla de Calvos**. Supervivencia, locuras
y calvicie en cantidades innecesarias. Todo por la patria capilar.

Hecho para **uMod/Oxide** en C#. Sin dependencias de otros plugins.

## La premisa

En Isla de Calvos **ser calvo es la gloria** y **el pelo es una maldición** que
vuelve a crecer. Cada jugador tiene un **% de calvicie (0-100)** que se guarda
entre sesiones. Lo glorioso te deja más calvo; morir hace que te salga pelo.
No hay cambio visual: todo son porcentajes, títulos y mensajes.

## Cómo funciona (v1.0)

Todos los valores se pueden cambiar en la config. Estos son los de por defecto:

| Qué pasa | Calvicie |
|---|---|
| Matas a otro jugador | **+3** |
| Lo matas de headshot | **+7** (en lugar de +3) |
| Cada 30 min vivo y conectado | **+1** |
| Mueres (por lo que sea, suicidio incluido) | **−5** |
| Mueres de headshot | **−3** extra (−8 en total) |

**Anti-farmeo:**
- Matar a un jugador **dormido (sleeper) o desconectado** no da calvicie.
- **Cooldown por víctima**: matar al mismo jugador solo da calvicie una vez
  cada 30 min.
- **Los NPCs no cuentan** (activable en la config).
- Suicidio = muerte normal, sin premio.

Si derribas a alguien y se desangra o se rinde, la muerte te cuenta a ti. El
headshot se mira en el golpe que lo mató o, en esos casos, en el que lo
derribó.

**Títulos**, según el % de calvicie:

| Desde | Título |
|---|---|
| 0 % | Aspirante a Calvo |
| 15 % | Calvo Novato |
| 30 % | Calvo Profesional |
| 50 % | Calvo Veterano |
| 70 % | Maestro de la Calvicie |
| 90 % | Gran Calvo |
| 100 % | Dios Calvo |

**Anuncios globales** (se pueden desactivar):
- Al llegar al 100 %: `🧑‍🦲 {jugador} HA ALCANZADO LA CALVICIE SUPREMA`
- Al bajar de título: `⚠️ A {jugador} le está saliendo pelo (ahora es {título})`

**Estadísticas** por jugador: kills, muertes y kills de headshot. Las kills
cuentan aunque no den calvicie (sleeper o cooldown); las de NPC solo si están
activadas en la config.

## Comandos

| Comando | Quién | Qué hace |
|---|---|---|
| `/calvo` | Todos | Tu % de calvicie y tu título. |
| `/calvos` | Todos | Top 10 de la isla. |
| `/calvoadmin set <jugador> <valor>` | Admin | Fija la calvicie de un jugador (0-100). |
| `/calvoadmin reset <jugador>` | Admin | Pone la calvicie de un jugador a 0. |

`<jugador>` puede ser el SteamID o el nombre (o parte del nombre). Funciona
también con jugadores desconectados que ya tengan datos. Los cambios de admin
no generan anuncios globales.

## Permisos

| Permiso | Para qué |
|---|---|
| `isladecalvos.admin` | Usar `/calvoadmin`. |

Se da desde la consola del servidor:
`oxide.grant user <SteamID o nombre> isladecalvos.admin`
(o a un grupo: `oxide.grant group admin isladecalvos.admin`).

## Configuración

Se genera sola en `oxide/config/IslaDeCalvos.json` la primera vez que carga el
plugin. Después de editarla: `oxide.reload IslaDeCalvos`.

| Opción | Por defecto | Qué hace |
|---|---|---|
| `Baldness gained per player kill` | 3 | Calvicie por kill. |
| `Baldness gained per headshot kill (instead of the normal kill reward)` | 7 | Calvicie por kill de headshot. |
| `Baldness gained per survival interval` | 1 | Calvicie por sobrevivir. |
| `Survival interval (minutes alive and connected)` | 30 | Cada cuántos minutos se gana. |
| `Baldness lost on death` | 5 | Calvicie perdida al morir. |
| `Extra baldness lost when the death is a headshot` | 3 | Extra si la muerte es de headshot. |
| `Kill cooldown per victim (minutes)` | 30 | Anti-farmeo por víctima (0 = sin cooldown). |
| `Count kills of NPC players (scientists, etc.)` | false | Si matar NPCs da calvicie. |
| `Announce when a player reaches 100% baldness` | true | Anuncio de calvicie suprema. |
| `Announce when a player drops to a lower title` | true | Anuncio de bajada de título. |
| `Reset baldness on map wipe (stats are kept)` | false | Si el wipe pone a todos a 0 %. |
| `Titles (minimum baldness -> title)` | ver tabla | Lista de títulos y desde qué % se consiguen. |

Los textos de los mensajes se editan en `oxide/lang/es/IslaDeCalvos.json` y
`oxide/lang/en/IslaDeCalvos.json`. Los dos están en español por defecto: la
mayoría de jugadores tienen el cliente en inglés, y Oxide les mostraría el
fichero `en`.

Los datos de los jugadores se guardan en `oxide/data/IslaDeCalvos.json`, en
cada guardado automático del servidor y al descargar el plugin o apagar el
servidor.

## Instalación

1. Ten un servidor dedicado de Rust con **Oxide (uMod)** instalado.
2. Copia `src/IslaDeCalvos.cs` en la carpeta `oxide/plugins/` del servidor.
3. Oxide lo compila y carga solo. En la consola deberías ver algo como
   `Loaded plugin Isla de Calvos v1.0.0 by Igor Monasterio`.
4. Para recargarlo tras cambiar el fichero (normalmente se recarga solo):
   `oxide.reload IslaDeCalvos`

Si falla la compilación, el error sale en la consola y en `oxide/logs/`.

## Documentación

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): cómo es un plugin de Oxide,
  hooks usados y convenciones del proyecto.
- [`CLAUDE.md`](CLAUDE.md): contexto para sesiones de Claude.

## Licencia

[MIT](LICENSE)
