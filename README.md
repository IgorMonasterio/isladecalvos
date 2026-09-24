# isladecalvos

Plugin principal del servidor de Rust **Isla de Calvos**. Supervivencia, locuras
y calvicie en cantidades innecesarias. Todo por la patria capilar.

Hecho para **uMod/Oxide** en C#. Sin dependencias de otros plugins.

## La premisa

En Isla de Calvos **ser calvo es la gloria** y **el pelo es una maldición** que
vuelve a crecer. Cada jugador tiene una **puntuación de calvicie**: un número
entero **sin límite por arriba** (nunca baja de 0) que se guarda entre
sesiones. Lo glorioso te deja más calvo; morir hace que te salga pelo.
No hay cambio visual: todo son puntos, títulos y mensajes.

## Cómo funciona (v1.0)

Todos los valores se pueden cambiar en la config. Estos son los de por defecto:

| Qué pasa | Calvicie |
|---|---|
| Matas a otro jugador (headshot o no) | **+1.000** |
| Matas a un NPC | según su tier, de **+1** (T1) a **+1.000** (T20) |
| Cada 30 min vivo y conectado | **+100** |
| Mueres, sea como sea (PvP, headshot, NPC, caída, suicidio…) | **−1.000** |

Siempre son números enteros.

**Anti-farmeo:**
- Matar a un jugador **dormido (sleeper) o desconectado** no da calvicie.
- **Cooldown por víctima**: matar al mismo jugador solo da calvicie una vez
  cada 30 min.
- Suicidio = muerte normal, sin premio.

Si derribas a alguien y se desangra o se rinde, la muerte te cuenta a ti. El
headshot se mira en el golpe que lo mató o, en esos casos, en el que lo
derribó.

### NPCs por tiers

Cada NPC tiene un **tier del 1 (fácil) al 20 (difícil)** según su
`ShortPrefabName`, y cada tier da una calvicie fija (config `NpcTiers` y
`TierRewards`). Recompensas por defecto: curva creciente de ×1,44 por tier,
de 1 a 1.000.

| Tier | Calvicie | NPCs |
|---|---|---|
| 1 | +1 | chicken |
| 2 | +2 | zombie |
| 3 | +3 | snake.entity, boar, stag |
| 4 | +4 | beeswarm, scientistnpc_ptboat, scientistnpc_rhib |
| 5 | +5 | beemasterswarm, wolf2, frankensteinpet |
| 6 | +6 | npc_tunneldweller, npc_tunneldwellerspawned |
| 7 | +9 | scientistnpc_junkpile_pistol, npc_underwaterdweller, simpleshark |
| 8 | +13 | scientistnpc_full_pistol, scientistnpc_full_shotgun, scientistnpc_full_mp5, scientistnpc_full_lr300, scientistnpc_full_any |
| 9 | +18 | scientistnpc_roam, scientistnpc_roamtethered, scientistnpc_patrol, scientistnpc_patrol_arctic, scientistnpc_arena |
| 10 | +26 | scientistnpc_outbreak, scientistnpc_excavator, scientistnpc_ch47_gunner, scientistnpc_bradley, panther, tiger, scarecrow, scarecrow_dungeon, scarecrow_dungeonnoroam |
| 11 | +38 | bear, scientist2, scientist2.shotgun, npc_bandit_guard ⛔ |
| 12 | +55 | scientistnpc_oilrig, scientistnpc_cargo, scientistnpc_cargo_turret_any, scientistnpc_cargo_turret_lr300 |
| 13 | +78 | polarbear, crocodile, gingerbread_dungeon |
| 14 | +113 | scientistnpc_heavy, scientistnpc_peacekeeper, scientistnpc_roam_nvg_variant, gingerbread_meleedungeon |
| 15 | +162 | scientistnpc_bradley_heavy |
| 16 | +234 | sentry.scientist.static ⛔, sentry.scientist.barge ⛔, sentry.scientist.barge.static ⛔, sentry.bandit.static ⛔ |
| 17 | +336 | scientist2.heavy |
| 18 | +483 | bradleyapc 👥 |
| 19 | +695 | ch47scientists.entity 👥 |
| 20 | +1.000 | patrolhelicopter 👥 |

⛔ = en la config pero **desactivado por defecto** (`DisabledNpcs`).
👥 = **recompensa compartida** (ver abajo).

- Solo cobra quien da el golpe final (salvo en los objetivos compartidos).
- Un NPC que no esté en `NpcTiers` **no da nada**, y la primera vez que alguien
  mata uno sale en la consola del servidor:
  `Unlisted NPC killed: '<shortprefabname>'. Add it to NpcTiers…`. Así sabes qué
  añadir. Ojo: también puede salir algo que no sea un NPC (p. ej. un vehículo
  sin dueño); ignóralo.
- Morir a manos de un NPC resta como cualquier muerte (desactivable con
  `Deaths caused by NPCs lower baldness`).

### Recompensa compartida (helicóptero, Bradley, Chinook)

Para los objetivos de `SharedRewardTargets` (por defecto `patrolhelicopter`,
`bradleyapc` y `ch47scientists.entity`), cuando el objetivo cae cobran **la
recompensa completa de su tier**:

1. **Todos los jugadores que le hicieron daño** durante el combate (aunque ya
   estén muertos o desconectados), y
2. **sus compañeros de equipo** (equipo de Rust) que estén **conectados y a
   menos de 300 m** del objetivo al caer (configurable).

Cada jugador cobra **una sola vez** por objetivo.

### Títulos

Un título por cada cero:

| Desde | Título |
|---|---|
| 1 | Aspirante a Calvo |
| 10 | Calvo Novato |
| 100 | Calvo Profesional |
| 1.000 | Calvo Veterano |
| 10.000 | Maestro de la Calvicie |
| 100.000 | Gran Calvo |
| 1.000.000 | Dios Calvo |

Con 0 puntos también eres Aspirante a Calvo.

**Anuncios globales** (se pueden desactivar):
- Al subir de título: `🧑‍🦲 {jugador} asciende a {título}`
- Al llegar al título más alto (Dios Calvo), en lugar del anterior:
  `🧑‍🦲 {jugador} HA ALCANZADO LA CALVICIE SUPREMA`
- Al bajar de título: `⚠️ A {jugador} le está saliendo pelo (ahora es {título})`

**Estadísticas** por jugador: kills, muertes y kills de headshot (solo contra
jugadores). Las kills cuentan aunque no den calvicie (sleeper o cooldown).

## Comandos

| Comando | Quién | Qué hace |
|---|---|---|
| `/calvo` | Todos | Tu calvicie y tu título. |
| `/calvos` | Todos | Top 10 de la isla. |
| `/calvoadmin set <jugador> <valor>` | Admin | Fija la calvicie de un jugador (entero, 0 o más). |
| `/calvoadmin reset <jugador>` | Admin | Pone la calvicie de un jugador a 0. |
| `/calvoadmin debug on\|off` | Admin | Muestra en tu chat cada cambio de calvicie (de cualquier jugador) con su motivo: NPC, tier, valor… También avisa cuando algo **no** da calvicie y por qué. Para probar. Se apaga al recargar el plugin. |

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
| `Baldness gained per player kill` | 1000 | Calvicie por kill. |
| `Baldness gained per headshot kill (instead of the normal kill reward)` | 1000 | Calvicie por kill de headshot. |
| `Baldness gained per survival interval` | 100 | Calvicie por sobrevivir. |
| `Survival interval (minutes alive and connected)` | 30 | Cada cuántos minutos se gana. |
| `Baldness lost on death` | 1000 | Calvicie perdida al morir. |
| `Extra baldness lost when the death is a headshot` | 0 | Extra si la muerte es de headshot. |
| `Deaths caused by NPCs lower baldness` | true | Si morir a manos de un NPC resta calvicie. |
| `Kill cooldown per victim (minutes)` | 30 | Anti-farmeo por víctima (0 = sin cooldown). |
| `Announce when a player reaches the highest title` | true | Anuncio de calvicie suprema. |
| `Announce when a player rises to a higher title` | true | Anuncio de subida de título. |
| `Announce when a player drops to a lower title` | true | Anuncio de bajada de título. |
| `Reset baldness on map wipe (stats are kept)` | false | Si el wipe pone a todos a 0. |
| `Titles (minimum baldness -> title)` | ver tabla | Lista de títulos y desde cuántos puntos se consiguen. |
| `NpcTiers` | ver tabla | `"<shortprefabname>": <tier>` para cada NPC que da calvicie. |
| `TierRewards` | curva 1 … 1000 | `"<tier>": <calvicie>` para los tiers 1-20. Números enteros. |
| `DisabledNpcs` | bandit guard y sentries | NPCs que están en `NpcTiers` pero no dan nada. |
| `SharedRewardTargets` | heli, Bradley, CH47 | Objetivos con recompensa compartida. Tienen que estar también en `NpcTiers`. |
| `Shared reward: teammate radius from the target (meters)` | 300 | Distancia máxima de los compañeros de equipo al objetivo. |

Ejemplo del bloque de NPCs:

```json
"NpcTiers": {
  "chicken": 1,
  "scientistnpc_roam": 9,
  "patrolhelicopter": 20
},
"TierRewards": {
  "1": 1,
  "9": 18,
  "20": 1000
},
"DisabledNpcs": [ "npc_bandit_guard" ],
"SharedRewardTargets": [ "patrolhelicopter", "bradleyapc", "ch47scientists.entity" ]
```

Si quitas una entrada de una lista o de un diccionario, se queda quitada: el
plugin no vuelve a meter los valores por defecto.

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
