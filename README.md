# isladecalvos

Plugin principal del servidor de Rust **Isla de Calvos**. Supervivencia, locuras
y calvicie en cantidades innecesarias. Todo por la patria capilar.

Hecho para **uMod/Oxide** en C#. Sin dependencias de otros plugins.

## La premisa

En Isla de Calvos **el pelo está sobrevalorado y el futuro es calvo**.
El pelo es una maldición que vuelve a crecer. Cada jugador tiene un **nivel de
alopecia**: un número entero **sin límite por arriba** (nunca baja de 0) que se
guarda entre sesiones. Matar y sobrevivir te dejan más calvo; morir hace que te
salga pelo.
No hay cambio visual: todo son puntos, títulos y mensajes.

"Calvicie", "calva" y compañía solo salen en chistes y nombres de eventos. En
el código la cifra sigue siendo `Baldness`, y las claves de la config están en
inglés. Los textos para jugadores siguen [docs/TONO.md](docs/TONO.md).

## Cómo funciona

Todos los valores se pueden cambiar en la config. Estos son los de por defecto:

| Qué pasa | Alopecia |
|---|---|
| Matas a otro jugador (headshot o no) | **+1.000** |
| Matas a un NPC | según su tier, de **+1** (T1) a **+1.000** (T20) |
| Cada 30 min vivo y conectado | **+100** |
| Mueres, sea como sea (PvP, headshot, NPC, caída, suicidio…) | **−10 % de tu alopecia** (redondeado hacia arriba) |

Siempre son números enteros.

**Anti-farmeo:**
- Matar a un jugador **dormido (sleeper) o desconectado** no da alopecia.
- **Cooldown por víctima**: matar al mismo jugador solo da alopecia una vez
  cada 30 min.
- Suicidio = muerte normal, sin premio.

Si derribas a alguien y se desangra o se rinde, la muerte te cuenta a ti. El
headshot se mira en el golpe que lo mató o, en esos casos, en el que lo
derribó.

### NPCs por tiers

Cada NPC tiene un **tier del 1 (fácil) al 20 (difícil)** según su
`ShortPrefabName`, y cada tier da una alopecia fija (config `NpcTiers` y
`TierRewards`). Recompensas por defecto: curva creciente de ×1,44 por tier,
de 1 a 1.000.

| Tier | Alopecia | NPCs |
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

Un título por cada cero (valores por defecto desde la 1.6.0, los mismos que
tiene el servidor):

| Desde | Título |
|---|---|
| 1 | Greñas Sucias |
| 1.000 | Pelambrera Lamentable |
| 10.000 | Entradas Incipientes |
| 100.000 | Coronilla a la Intemperie |
| 1.000.000 | Caballero de la Tonsura |
| 10.000.000 | Lord Bola de Billar |
| 100.000.000 | Su Calvísima Majestad |

Con 0 de alopecia también eres Greñas Sucias.

**Anuncios globales** (se pueden desactivar):
- Al subir de título: `{jugador} asciende a {título}. Su peluquero ya ha pedido el paro.`
- Al llegar al título más alto (Su Calvísima Majestad), en lugar del anterior:
  `{jugador} HA ALCANZADO LA CALVICIE SUPREMA`
- Al bajar de título: `A {jugador} le está saliendo pelo (ahora es {título})`

**Cartel grande al subir de título** (1.6.0): además del chat, a todos los
conectados les sale en el centro de la pantalla, durante 6 s,
`¡{jugador} ya es CABALLERO DE LA TONSURA!`. Es el mismo cartel que los
eventos globales. Las bajadas siguen solo en el chat.

**Premio por subir de título** (1.6.0): cada título puede dar RP, monedas y/o
objetos. Por defecto todo está a 0. Se cobra **una sola vez por jugador y
título**: bajar y volver a subir no paga otra vez. A los jugadores que ya
existían se les apunta como cobrado el título que tenían la primera vez que
cambian de título. La alopecia **comprada** en el cambio no cobra premios,
salvo que se active en la config.

Los mensajes van resaltados con los colores de la casa: dorado para cifras
buenas y comandos, y óxido para lo que duele (ver [docs/TONO.md](docs/TONO.md)).
Llevan como **icono** el avatar de la cuenta de Steam de la isla. Para cambiar el icono, cambia el avatar de esa
cuenta en Steam o pon otro SteamID64 en la config. Si acabas de cambiar el
avatar, los clientes pueden tardar en verlo (Steam lo cachea). **No uses emojis en los textos**: el
chat de Rust no los dibuja y salen como `??`.

### RP de Server Rewards por alopecia (plugin 1.4.0, lineal desde la 1.6.0)

Ser calvo da de comer. Cada **30 minutos vivo, conectado y despierto**, el
plugin paga **RP de Server Rewards**: **1 RP por cada 100 de alopecia**, sin
tope (100 → 1, 3.900 → 39, 2.000.000 → 20.000). Se calcula en entero largo y
se limita al máximo que admite Server Rewards (2.147.483.647).

Con `"RP per X baldness (0 = use the table)": 0` se vuelve a la tabla de
escalones por título de la 1.4.0.

- Solo cobra quien se haya **movido** durante esos 30 minutos (anti-AFK). Se
  mira cada minuto; basta con moverse 1 m entre dos comprobaciones.
- Cuenta el título que tengas **en el momento del pago**. Morir no reinicia
  el reloj de los RP, pero los minutos muerto o dormido no cuentan.
- El jugador ve en el chat `+X RP por lucir calva de {título}`.
- Los RP no se multiplican con eventos ni con la pila, y no tocan la alopecia.
- Si **Server Rewards** no está cargado, no se paga nada y se avisa una vez
  en la consola. La alopecia sigue funcionando igual.

**Estadísticas** por jugador: kills, muertes y kills de headshot (solo contra
jugadores). Las kills cuentan aunque no den alopecia (sleeper o cooldown).

## En pantalla

- **Contador de alopecia**, siempre visible en la **esquina superior
  derecha**: `ALOPECIA 1.174` y tu título debajo (desde la 1.6.1; antes
  ponía `CALVICIE`). Se actualiza con cada cambio. (Abajo chocaba con el
  panel de RaidableBases.)
- Al ganar alopecia sale debajo un **`+18`** en dorado durante 2,5 s; al
  perderla, un **`-117`** en óxido.
- Los **eventos globales** salen además en un **cartel grande en el centro
  de la pantalla** durante 8 s (y en el chat, como siempre).
- La posición del contador, los tiempos y activar o desactivar cada cosa se
  cambian en el bloque `On-screen UI` de la config. La posición va en anclas
  de pantalla (0-1) y desplazamientos en píxeles; si en tu resolución queda
  montado sobre algo, ajusta los `offset`.

## Eventos globales (v1.1)

**Cada hora** arranca un **evento al azar**. Solo hay uno a la vez, y solo si
hay jugadores conectados. Todos salen anunciados en el chat al empezar y al
terminar.

| Evento | Qué hace | Duración |
|---|---|---|
| **Hora de la calvicie** | Todo lo que da alopecia da **el doble**: kills, NPCs, supervivencia y objetivos compartidos. | 30 min |
| **Lluvia de champú** | Morir resta **el doble** (−20 %). | 20 min |
| **Cacería del peludo** | Se anuncia al jugador conectado con **menos alopecia**. Quien lo mate gana **+2.000** además de la kill normal. Si aguanta los 20 min, él gana **+1.000**. Si muere por otra cosa o se desconecta, se acaba sin premio. Hacen falta al menos 2 jugadores conectados. | 20 min |
| **Brote de alopecia** | Heli, Bradley y Chinook dan **el triple**. | 60 min |

Si toca la cacería y solo hay un jugador conectado, se elige otro evento.
Todo (intervalo, duraciones, multiplicadores, premios, activar o desactivar
cada evento) se cambia en el bloque `Global events` de la config.

## El Calvario: objetos calvos (v1.3)

Objetos que **existen en el código de Rust pero no salen en ningún servidor
normal**. Solo los reparte este plugin: van **directos a tu inventario** (o
caen a tus pies si lo llevas lleno) con aviso en el chat. Se pueden cambiar o
regalar como cualquier objeto, y se usan en **el Calvario de la peluquería**:
hablando con **El Barbero** (tecla **E**). Ver [La peluquería](#la-peluquería-plugin-150).

**La conversación con el barbero** imita a los dependientes vanilla (como el
del pueblo pesquero). Abajo sale un cuadro con lo que dice el barbero y tus
respuestas numeradas:

```
 EL BARBERO                        Tu alopecia: 2.944 — Coronilla a la Intemperie
 "Siéntate, peludo. ¿Qué te quito hoy, el pelo o la dignidad?"

   1. Traigo algo maldito
   2. Vengo a sellar el Carné de Calvo
   3. Vengo a vender (o comprar) calva
   4. Nada, solo miraba
```

- Te saluda con una de sus frases o con uno de los **refranes** de la isla.
- **1** lista los objetos que llevas, cada uno con cuántos tienes y qué hace.
  Al elegir uno, lo usa y el barbero contesta **en el cuadro** (no en el chat).
- **2** enseña qué colores del carné tienes sellados y cuáles te faltan, con
  la opción de sellar lo que traes.
- **3** es el [cambio de alopecia](#el-cambio-de-alopecia-plugin-160). Solo
  sale si está activado en la config.

El **Salón de la fama calva** (`/calvos`) va de barbería calva: rayas de
poste de barbero, barra de progreso hacia tu siguiente título,
oro/plata/bronce para el podio y cuánto te falta para adelantar al de
arriba.

| Objeto | Cómo se consigue | Qué hace al usarlo |
|---|---|---|
| **Lejía** (`bleach`) | 5 % al romper un barril | Apuesta: 70 % **+500** / 30 % **−500** |
| **Cinta americana** (`ducttape`) | 5 % al romper un barril | Tu próxima muerte **no resta** (máx. 1 activa) |
| **Pila pequeña** (`battery.small`) | 3 % al romper un barril | **x2** en todo lo que ganes durante 10 min |
| **Placa militar** (`dogtagneutral`) | 50 % al matar un NPC de tier 8-12 | **+200** |
| **Placas azules** (`bluedogtags`) | 30 % al matar un NPC de tier 13-17 (heavies, RaidableBases…) | **+500** |
| **Placas rojas** (`reddogtags`) | Siempre, a cada jugador que cobra el heli, la Bradley o el Chinook | **+1.500** |
| **Gemas** (`kickgems`) | 1 % al matar un NPC de tier 12 o más | **+5.000** |
| **Tarjetas de identificación** (11 colores) | 5 % al matar un NPC de tier 1-17, color al azar | Van al **Carné de Calvo** |

**Carné de Calvo:** entrega al barbero una tarjeta de cada color (opción
**Séllame lo que traigo**):
**+100** por tarjeta y **+10.000** al completar los 11 colores, con anuncio en
el cartel del centro. Después empieza un carné nuevo. Las tarjetas repetidas
no se gastan: sirven para cambiarlas con otros.

Lo que dan los objetos cuenta como ganancia normal: la **Hora de la
calvicie** lo duplica, y con la **pila** a la vez, x4. La lejía que sale mal y
el premio del carné completo no se multiplican.

Si un nombre interno no existe en la versión de Rust del servidor, el plugin
lo avisa en la consola al arrancar.

### El cambio de alopecia (plugin 1.6.0)

Opción **3. Vengo a vender (o comprar) calva** en la conversación con el barbero.
Alopecia, RP y monedas se cambian entre sí:

| Operación | Por defecto |
|---|---|
| Vender alopecia por RP | 100 de alopecia → 1 RP |
| Vender alopecia por monedas | 100 de alopecia → 10 monedas |
| Comprar alopecia con RP | 1 RP → 1 de alopecia |
| Comprar alopecia con monedas | 10 monedas → 1 de alopecia |

- Las tasas son **asimétricas a propósito**: la alopecia paga RP cada 30 min
  para siempre, y si comprarla fuera barato sería una máquina de hacer dinero.
- Cantidades fijas (100, 1.000, 10.000, 100.000 de alopecia), solo enteras. Para
  vender, un mínimo de 100 y múltiplos exactos.
- Antes de cada cambio sale una **confirmación** ("¿Seguro que cambias 1.000 de
  alopecia por 10 RP?…") con **CONFIRMAR** / **ME LO PIENSO**. Lo que se confirma se
  guarda en el servidor, no en el botón, y se vuelve a comprobar al confirmar.
- Primero se paga o se cobra en Server Rewards o Economics, y solo si eso sale
  bien se toca la alopecia.
- Vender puede bajarte de título (se anuncia en el chat, como cualquier
  bajada). Lo comprado sube de título normal, pero ni se multiplica con
  eventos o la pila ni cobra premios de título.
- Si Server Rewards o Economics no están cargados, sus opciones salen cerradas.

## La peluquería (plugin 1.5.0)

Tres casitas, cada una con un NPC de **HumanNPC**:

| Casa | NPC | Plugin que la atiende |
|---|---|---|
| **El Calvario** | El Barbero | Este plugin: objetos malditos y Carné de Calvo |
| **El Mercalvona®** | Tendero del Mercalvona | GUIShop (monedas) |
| **Cambio de divisas** (antes Premios Calvos) | Cambista de divisas | Server Rewards (RP) |

Se llega con **`/peluqueria`**, igual que `/bandit` o `/outpost`. `/shop` y
`/s` dejan de funcionar fuera de allí, y `/calvos` solo muestra el ranking.

Dónde va la peluquería y cómo se viste a los NPC depende de cada mapa y lo
decide quien monta el servidor tras cada wipe. El plugin solo se enlaza con
el barbero por su `userid`. **Hacen falta los tres NPC.** Sin barbero
configurado, los objetos no se pueden usar, y la consola lo avisa al cargar.

Los objetos solo se pueden usar **a 5 m o menos del barbero, y después de
haberle hablado**. Si el jugador se aleja o intenta usarlos por consola desde
otro sitio, se le cierra la ventana y se le manda a la peluquería.

### Cómo se monta en el servidor

Casi todo es configuración de otros plugins. Lo he mirado en su código
(copias públicas: HumanNPC 0.5.4, GUIShop 2.4.48, Server Rewards 2.0.7,
NTeleportation 1.8.9). El servidor tiene Server Rewards 2.0.8. Si alguna
versión es otra, los nombres pueden cambiar un poco.

1. **Las casas y los NPC.** Construye las tres casitas y pon un NPC de
   HumanNPC en cada una (`/npc_add`). Apunta el `userid` de cada NPC: sale
   con `/npc_list`.
2. **El Calvario (este plugin).** En `oxide/config/IslaDeCalvos.json`:
   ```json
   "Barber shop (HumanNPC)": {
     "Calvario NPC ids (HumanNPC userid)": [ <userid del barbero> ],
     "Max distance to the Calvario NPC to use items (meters)": 5.0
   }
   ```
   Después: `oxide.reload IslaDeCalvos`.
3. **El Mercalvona (GUIShop).** En la tienda que quieras asignar, activa
   `EnableNPC` y pon el `userid` del tendero en `NpcIds`. Para que `/shop` no
   abra nada fuera de la casa, deja `"Set Default Global Shop to open": ""`
   (vacío). Es la forma que indica el propio GUIShop para desactivar las
   tiendas globales.
4. **Cambio de divisas (Server Rewards).** Mirando al cambista, `/srnpc add`. En su
   config, `"Use NPC dealers only": true`: así `/s` solo funciona para
   admins.
5. **El TP (NTeleportation).** En `"Dynamic Commands"`, añade una entrada
   `"Peluqueria"` copiando la de `"Bandit"`. Recarga NTeleportation, ponte
   en la puerta y usa `/peluqueria set` (admin). Los jugadores ya pueden usar
   `/peluqueria`, con el cooldown y la cuenta atrás de esa entrada.

### Textos de las tiendas

Revisados por Igor. Van en la configuración de cada plugin, en el servidor;
este plugin no los toca.

**Nombre y frases de cada NPC (HumanNPC).** Se editan con `/npc_edit <userid>`
y después `/npc name "…"`, `/npc hello "…" "…"`, `/npc use "…"` y
`/npc bye "…"`. Cada frase entre comillas es una opción; sale una al azar.
`/npc_end` para terminar.

```
# El Mercalvona
/npc name "Tendero del Mercalvona"
/npc hello "¡Bienvenido al Mercalvona®! Precios bajos y cabezas relucientes." "Pasa, pasa. Champú no tenemos, que aquí eso es contrabando."
/npc use "¿Qué va a ser? Dale a la E y no toques lo que no vayas a pagar."
/npc bye "Gracias por comprar en el Mercalvona®. Vuelve con menos pelo y más cartera."

# El Calvario (use vacío: la E abre directamente El Calvario)
/npc name "El Barbero"
/npc hello "Huele a pelo. Siéntate, que te lo quito todo."
/npc use reset
/npc bye "Vuelve cuando te asome algo. Aquí no se deja crecer ni la duda."

# Cambio de divisas
/npc name "Cambista de divisas"
/npc hello "Cambio de divisas: tu calva vale RP y aquí se cobra. Pasa por caja."
/npc use "A ver cuánto te ha pagado esa cabeza. Dale a la E."
/npc bye "Sigue brillando, que cada media hora te cae algo."
```

**GUIShop** (`oxide/lang/es/GUIShop.json`). Solo estas cinco; el resto ya
está en el servidor con el tono de la isla y no se toca:

```json
"NPCResponseOpen": "¡Bienvenido a {0}! ¿Qué te pongo? Dale a la E, que no tengo todo el día.",
"NPCResponseClose": "Gracias por comprar en {0}. Vuelve pronto, y más pelado.",
"GlobalShopsDisabled": "Aquí no se compra desde el sofá, señorito. Ve al Mercalvona con /peluqueria y háblale al tendero.",
"Bought": "Te llevas {0} de {1}. El tendero ya está contando tus monedas.",
"Sold": "Has vendido {0} de {1}. Con eso no te da ni para un peine."
```

**Server Rewards 2.x** (`oxide/lang/es/ServerRewards.json`). Con
`"Use NPC dealers only": true`, el aviso de RP sin gastar usa esta clave
(conservar la etiqueta de color):

```json
"Message.Notification.Unspent.NPC": "Busca al <color=#B6F34A>cambista</color> con /peluqueria para gastarlos."
```

## Comandos

| Comando | Quién | Qué hace |
|---|---|---|
| `/calvos` | Todos | Abre el **Salón de la fama calva**: ranking de todo el servidor, de 10 en 10, con tu posición. Se cierra con la **X**. Los objetos se usan hablando con el barbero de la peluquería. |
| `/calvoadmin set <jugador> <valor>` | Admin | Fija la alopecia de un jugador (entero, 0 o más). |
| `/calvoadmin reset <jugador>` | Admin | Pone la alopecia de un jugador a 0. |
| `/calvoadmin evento <hora\|champu\|peludo\|alopecia>` | Admin | Lanza ese evento ya, sin esperar a la hora. Para probar. (`cuchillas` también vale para el Brote de alopecia.) |
| `/calvoadmin evento parar` | Admin | Cancela el evento en marcha. |
| `/calvoadmin debug on\|off` | Admin | Muestra en tu chat cada cambio de alopecia (de cualquier jugador) con su motivo: NPC, tier, valor… También avisa cuando algo **no** da alopecia y por qué. Para probar. Se apaga al recargar el plugin. |

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
| `Baldness gained per player kill` | 1000 | Alopecia por kill. |
| `Baldness gained per headshot kill (instead of the normal kill reward)` | 1000 | Alopecia por kill de headshot. |
| `Baldness gained per survival interval` | 100 | Alopecia por sobrevivir. |
| `Survival interval (minutes alive and connected)` | 30 | Cada cuántos minutos se gana. |
| `Baldness lost on death (% of current baldness)` | 10 | Porcentaje de tu alopecia que pierdes al morir. |
| `Deaths caused by NPCs lower baldness` | true | Si morir a manos de un NPC resta alopecia. |
| `Kill cooldown per victim (minutes)` | 30 | Anti-farmeo por víctima (0 = sin cooldown). |
| `Announce when a player reaches the highest title` | true | Anuncio de calvicie suprema. |
| `Announce when a player rises to a higher title` | true | Anuncio de subida de título. |
| `Announce when a player drops to a lower title` | true | Anuncio de bajada de título. |
| `Reset baldness on map wipe (stats are kept)` | false | Si el wipe pone a todos a 0. |
| `Titles (minimum baldness -> title)` | ver tabla | Lista de títulos y desde cuántos puntos se consiguen. |
| `NpcTiers` | ver tabla | `"<shortprefabname>": <tier>` para cada NPC que da alopecia. |
| `TierRewards` | curva 1 … 1000 | `"<tier>": <alopecia>` para los tiers 1-20. Números enteros. |
| `DisabledNpcs` | bandit guard y sentries | NPCs que están en `NpcTiers` pero no dan nada. |
| `SharedRewardTargets` | heli, Bradley, CH47 | Objetivos con recompensa compartida. Tienen que estar también en `NpcTiers`. |
| `Shared reward: teammate radius from the target (meters)` | 300 | Distancia máxima de los compañeros de equipo al objetivo. |
| `Chat icon: SteamID64 whose avatar is shown next to plugin messages (0 = default Rust icon)` | 76561198635630459 | Cuenta de Steam cuyo **avatar** sale como icono de los mensajes del plugin (la cuenta del calvo oxidado). 0 = icono de Rust. |

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

Bloque de pantalla (valores por defecto):

```json
"On-screen UI": {
  "Show baldness counter": true,
  "Counter anchor min": "1 1",
  "Counter anchor max": "1 1",
  "Counter offset min": "-212 -58",
  "Counter offset max": "-16 -22",
  "Seconds the +X / -X popup stays": 2.5,
  "Show event banner in the middle of the screen": true,
  "Seconds the event banner stays": 8.0
}
```

Bloque de objetos (valores por defecto, resumido; cada objeto tiene su
nombre interno y su probabilidad):

```json
"Cursed items (El Calvario)": {
  "Enabled": true,
  "Bleach (gamble)": { "Item shortname": "bleach", "Drop chance (0-1)": 0.05, "Win chance (0-1)": 0.7, "Baldness on win": 500, "Baldness lost on fail": 500 },
  "Duct tape (your next death costs nothing)": { "Item shortname": "ducttape", "Drop chance (0-1)": 0.05 },
  "Small battery (personal gain multiplier)": { "Item shortname": "battery.small", "Drop chance (0-1)": 0.03, "Gain multiplier": 2, "Duration (minutes)": 10 },
  "Dog tag": { "Item shortname": "dogtagneutral", "Drop chance (0-1)": 0.5, "Baldness when used": 200, "Drops from NPC tier (min)": 8, "Drops from NPC tier (max)": 12 },
  "ID tags (Carne de Calvo collection)": { "Drop chance per NPC kill (0-1)": 0.05, "Baldness per delivered tag": 100, "Bonus for completing all colors": 10000 }
}
```

Bloque de RP (valores por defecto). Las claves son la alopecia mínima, igual
que los títulos:

```json
"Server Rewards (RP by title)": {
  "Enabled": true,
  "Interval (minutes alive and connected)": 30,
  "Only pay players who moved during the interval (not AFK)": true,
  "Minimum movement between checks to count as active (meters)": 1.0,
  "Tell the player in chat when RP is paid": true,
  "RP per X baldness (0 = use the table)": 100,
  "RP per interval by title (minimum baldness -> RP)": {
    "1000": 1,
    "10000": 3,
    "100000": 10,
    "1000000": 30
  }
}
```

Bloques de premios por título y del cambio (valores por defecto). Las claves de
los premios son la alopecia mínima de cada título:

```json
"Tier prizes (title minimum baldness -> prize)": {
  "1000": { "RP (Server Rewards)": 0, "Coins (Economics)": 0,
            "Items": [ { "Item shortname": "scrap", "Amount": 100 } ] },
  ...
},
"Tier prizes also for bought baldness": false,
"Baldness exchange (El Calvario)": {
  "Enabled": true,
  "Sell: baldness for 1 RP": 100,
  "Sell: coins per 100 baldness": 10,
  "Buy: RP per 1 baldness": 1,
  "Buy: coins per 1 baldness": 10,
  "Minimum baldness to sell": 100,
  "Amounts offered (baldness)": [ 100, 1000, 10000, 100000 ]
}
```

(El `scrap` es solo un ejemplo: por defecto los premios vienen vacíos. En el
chat, los objetos del premio salen con su nombre interno.)

En `"On-screen UI"` hay dos opciones nuevas:
`"Show a banner to everyone when a player rises to a higher title": true` y
`"Seconds the title-up banner stays": 6.0`.

La config lleva además un `Config version (do not edit)`. Sirve para que una
actualización pueda corregir valores ya guardados (la 1.3.1 mueve el contador
a la esquina superior derecha una sola vez). No lo toques.

Bloque de eventos globales (valores por defecto):

```json
"Global events": {
  "Enabled": true,
  "Minutes between random events": 60,
  "Bald hour (all baldness gains multiplied)": { "Enabled": true, "Duration (minutes)": 30, "Gain multiplier": 2 },
  "Shampoo rain (death penalty multiplied)": { "Enabled": true, "Duration (minutes)": 20, "Death penalty multiplier": 2 },
  "Hunt the hairiest (bounty on the online player with least baldness)": {
    "Enabled": true, "Duration (minutes)": 20, "Bonus for the killer": 2000,
    "Bonus for the target if they survive": 1000, "Minimum online players": 2
  },
  "Alopecia outbreak (shared big-target rewards multiplied)": { "Enabled": true, "Duration (minutes)": 60, "Shared reward multiplier": 3 }
}
```

El bloque `Alopecia outbreak` es el **Brote de alopecia**.

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
   `Loaded plugin Isla de Calvos v1.6.6 by Igor Monasterio`.
4. Para recargarlo tras cambiar el fichero (normalmente se recarga solo):
   `oxide.reload IslaDeCalvos`

Si falla la compilación, el error sale en la consola y en `oxide/logs/`.

## Documentación

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): cómo es un plugin de Oxide,
  hooks usados y convenciones del proyecto.
- [`CLAUDE.md`](CLAUDE.md): contexto para sesiones de Claude.

## Licencia

[MIT](LICENSE)
