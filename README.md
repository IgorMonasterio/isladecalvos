# isladecalvos

Plugin principal del servidor de Rust **[ES] Isla de Calvos**. Supervivencia, locuras
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
| Matas a otro jugador (headshot o no) | **+10.000** |
| Matas a un NPC | según su tier, de **+10** (T1) a **+10.000** (T20) |
| Cada 30 min vivo, conectado y moviéndote (no AFK) | **+100** |
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
de 10 a 10.000 (desde la 1.10.0, la de antes ×10, como en el servidor).

| Tier | Alopecia | NPCs |
|---|---|---|
| 1 | +10 | chicken |
| 2 | +20 | zombie |
| 3 | +30 | snake.entity, boar, stag |
| 4 | +40 | beeswarm, scientistnpc_ptboat, scientistnpc_rhib |
| 5 | +50 | beemasterswarm, wolf2, frankensteinpet |
| 6 | +60 | npc_tunneldweller, npc_tunneldwellerspawned |
| 7 | +90 | scientistnpc_junkpile_pistol, npc_underwaterdweller, simpleshark |
| 8 | +130 | scientistnpc_full_pistol, scientistnpc_full_shotgun, scientistnpc_full_mp5, scientistnpc_full_lr300, scientistnpc_full_any |
| 9 | +180 | scientistnpc_roam, scientistnpc_roamtethered, scientistnpc_patrol, scientistnpc_patrol_arctic, scientistnpc_arena |
| 10 | +260 | scientistnpc_outbreak, scientistnpc_excavator, scientistnpc_ch47_gunner, scientistnpc_bradley, panther, tiger, scarecrow, scarecrow_dungeon, scarecrow_dungeonnoroam |
| 11 | +380 | bear, scientist2, scientist2.shotgun, npc_bandit_guard ⛔ |
| 12 | +550 | scientistnpc_oilrig, scientistnpc_cargo, scientistnpc_cargo_turret_any, scientistnpc_cargo_turret_lr300 |
| 13 | +780 | polarbear, crocodile, gingerbread_dungeon |
| 14 | +1.130 | scientistnpc_heavy, scientistnpc_peacekeeper, scientistnpc_roam_nvg_variant, gingerbread_meleedungeon |
| 15 | +1.620 | scientistnpc_bradley_heavy |
| 16 | +2.340 | sentry.scientist.static ⛔, sentry.scientist.barge ⛔, sentry.scientist.barge.static ⛔, sentry.bandit.static ⛔ |
| 17 | +3.360 | scientist2.heavy |
| 18 | +4.830 | bradleyapc 👥 |
| 19 | +6.950 | ch47scientists.entity 👥 |
| 20 | +10.000 | patrolhelicopter 👥 |

⛔ = en la config pero **desactivado por defecto** (`DisabledNpcs`).
👥 = **recompensa compartida** (ver abajo).

- Solo cobra quien da el golpe final (salvo en los objetivos compartidos).
- Un NPC que no esté en `NpcTiers` **no da nada**, y la primera vez que alguien
  mata uno sale en la consola del servidor:
  `Unlisted NPC killed: '<shortprefabname>'. Add it to NpcTiers…`. Así sabes qué
  añadir. Desde la 1.9.0 no salen las piezas de construcción, puertas ni
  desplegables (las de una base de RaidableBases no tienen dueño y antes se
  colaban: `door.hinged.metal`, `wall.frame`, `locker.deployed`…). Aún puede
  salir algo que no sea un NPC (p. ej. un vehículo sin dueño); ignóralo.
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

Con 0 de alopecia el contador y el ranking también te llaman Greñas Sucias,
pero para los grupos, los premios y el aviso a otros plugins (1.7.0) con 0 no
tienes título: Greñas Sucias empieza en 1.

**Anuncios globales** (se pueden desactivar):
- Al subir de título: `{jugador} asciende a {título}. Su peluquero ya ha pedido el paro.`
- Al llegar al título más alto (Su Calvísima Majestad), en lugar del anterior:
  `{jugador} HA ALCANZADO LA CALVICIE SUPREMA`
- Al bajar de título: `A {jugador} le está saliendo pelo (ahora es {título})`

**Cartel grande al subir de título** (1.6.0): además del chat, a todos los
conectados les sale en el centro de la pantalla, durante 6 s,
`¡{jugador} ya es CABALLERO DE LA TONSURA!`. Es el mismo cartel que los
eventos globales.

**Cartel grande al bajar de título** (1.7.0): igual, con
`{jugador} baja a ENTRADAS INCIPIENTES. Ya no se le ve el cartón.` El mensaje
del chat al bajar sigue saliendo como antes. Se desactiva con
`"Show title drop banner"` y dura lo mismo que el de subida.

**Premio por subir de título** (1.6.0): cada título puede dar Puntos de Chola, pelones y/o
objetos. Los de por defecto (1.10.0, los mismos que tiene el servidor):

| Título | Puntos de Chola | Pelones | Objetos |
|---|---|---|---|
| Greñas Sucias | — | 2.500 | 1 cuchillo de hueso (`knife.bone`) |
| Pelambrera Lamentable | 100 | 10.000 | — |
| Entradas Incipientes | 500 | 50.000 | — |
| Coronilla a la Intemperie | 2.500 | — | 4 C4 (`explosive.timed`) |
| Caballero de la Tonsura | 15.000 | — | un **helicóptero de combate**, aparcado delante (ver abajo) |
| Lord Bola de Billar | 50.000 | — | máscara y peto de metal (`metal.facemask`, `metal.plate.torso`) |
| Su Calvísima Majestad | 250.000 | — | — |

El de Greñas Sucias lleva además su mensaje propio: "Toma este trozo de hueso
afilado. Empieza a raparte solito."

**Premios que se aparcan** (1.11.0): además de `Items`, cada premio admite
`"Spawn prefabs"`, una lista de prefabs (ruta completa) que el plugin spawnea
**delante del jugador, donde esté**, mirando hacia donde mira él y sobre el suelo.
El de Caballero de la Tonsura trae por defecto el helicóptero de combate
(`assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab`).

- Busca sitio a 4 m y, si no vale, a 7, 10, 14 y 18 m: tiene que haber suelo
  (no mar), unos 3 m libres alrededor (sin paredes, rocas, árboles, techo ni otro
  vehículo) y nada entre el jugador y el sitio.
- Si no hay sitio, no se spawnea: al jugador le sale un aviso para que hable con
  un admin y la consola dice dónde estaba. El premio ya cuenta como cobrado, así
  que el admin se lo da a mano.
- El vehículo queda a nombre del jugador (`OwnerID`).
- Si al cobrar no está conectado (casi imposible: se cobra al subir de título),
  no se spawnea y se avisa en la consola.
- Hasta la 1.10.0 ese premio era el objeto `minicopter`, que en Rust no existe:
  no daba nada. La 1.11.0 lo cambia por el helicóptero también en las configs
  que ya lo tenían.

Se cobra **una sola vez por jugador y
título**: bajar y volver a subir no paga otra vez. A los jugadores que ya
existían se les apunta como cobrado el título que tenían la primera vez que
cambian de título. La alopecia **comprada** en el barbero cobra premios como
cualquier otra (desde la 1.10.0; antes no cobraba salvo que se activara en la
config, y esa opción ya no existe).

Desde la 1.7.0:
- El premio de **Greñas Sucias** se cobra al pasar de 0 a 1 o más. Antes nunca
  se pagaba, porque con 0 ya contaba como Greñas Sucias.
- Los **objetos** del premio se dan **en silencio**: Rust ya enseña su aviso de
  objeto recibido. El mensaje del premio solo nombra los Puntos de Chola y los
  pelones; si el premio es solo de objetos, dice que ya están en el inventario.
  Si no caben, caen a los pies del jugador.
- Cada premio puede llevar su **mensaje propio** (`"Message"`), que se le dice al
  jugador tal cual, detrás del mensaje del premio.

### Grupos por título (plugin 1.7.0)

Cada jugador está siempre en el **grupo de Oxide de su título** y en ningún
otro grupo de la lista: `calvo1` (Greñas Sucias) a `calvo7` (Su Calvísima
Majestad). Con 0 de alopecia, en ninguno. Las **ventajas** de cada título
(homes, espera del teletransporte, mochila, título en el chat) son permisos de
NTeleportation, Backpacks y Better Chat que el admin da a esos grupos: el
plugin solo mueve a los jugadores de grupo.

- Se sincroniza al cargar el plugin (los conectados), al conectarse y en cada
  cambio de alopecia que cambie de título: al subir y al bajar, también con
  alopecia comprada, con `/calvoadmin` y con el reset del wipe.
- A diferencia del premio, que se cobra una vez, el grupo **sigue siempre al
  título actual**: se pierde al bajar. La alopecia comprada cuenta para los dos.
- Si un grupo no existe, el plugin lo crea. Los grupos que no están en la lista
  (`default`, `admin`…) no se tocan nunca; `default`, `admin` y `*` no se
  pueden poner como grupo de título.
- Es silencioso: nada en el chat.

### Aviso a otros plugins (plugin 1.7.0)

Cuando un jugador cambia de título (al subir y al bajar, con anuncios; no con
`/calvoadmin` ni al sincronizar la carga), el plugin llama al hook:

```csharp
OnIslaTitleChanged(ulong userId, string playerName, string oldTitle, string newTitle, bool up, long baldness)
```

`oldTitle` o `newTitle` van vacíos si no había título (alopecia 0). No hace
falta devolver nada. Lo escucha JanoBridge para que el Gran Calvo Jano felicite
(o se ría) en el chat.
Desde la 1.8.0 hay más hooks (salón de la fama, cabezas, Calvo del Día y fin de
la cacería): ver [Más avisos para otros plugins](#más-avisos-para-otros-plugins-hooks).

Los mensajes van resaltados con los colores de la casa: dorado para cifras
buenas y comandos, y óxido para lo que duele (ver [docs/TONO.md](docs/TONO.md)).
Llevan como **icono** el avatar de la cuenta de Steam de la isla. Para cambiar el icono, cambia el avatar de esa
cuenta en Steam o pon otro SteamID64 en la config. Si acabas de cambiar el
avatar, los clientes pueden tardar en verlo (Steam lo cachea). **No uses emojis en los textos**: el
chat de Rust no los dibuja y salen como `??`.

### Puntos de Chola de Server Rewards por alopecia (plugin 1.4.0, lineal desde la 1.6.0)

Ser calvo da de comer. Cada **30 minutos vivo, conectado y despierto**, el
plugin paga **Puntos de Chola de Server Rewards**: **1 Punto de Chola por cada 100 de alopecia**, sin
tope (100 → 1, 3.900 → 39, 2.000.000 → 20.000). Se calcula en entero largo
(64 bits).

**Límite de Server Rewards** (1.6.7): Server Rewards guarda los Puntos de Chola en un
entero de 32 bits y, si se pasa de 2.147.483.647, el saldo salta a negativo.
El plugin nunca empuja un saldo más allá: la paga se recorta hasta el tope, y
vender alopecia por Puntos de Chola se rechaza si no cabe. Si Server Rewards está parcheado
para trabajar en 64 bits (métodos `AddPointsLong`, `TakePointsLong` y
`CheckPointsLong`), el plugin los usa solo y el límite desaparece.

Con `"RP per X baldness (0 = use the table)": 0` se paga por la tabla de
escalones por título de la 1.4.0 (`"RP per interval by title"`), que desde la
1.10.0 viene vacía, como en el servidor: si se vuelve a ella, hay que rellenarla.

- Solo cobra quien se haya **movido** durante esos 30 minutos (anti-AFK). Se
  mira cada minuto; basta con moverse 1 m entre dos comprobaciones.
- Cuenta el título que tengas **en el momento del pago**. Morir no reinicia
  el reloj de los Puntos de Chola, pero los minutos muerto o dormido no cuentan.
- El jugador ve en el chat `+X Puntos de Chola por lucir calva de {título}`.
- Los Puntos de Chola no se multiplican con eventos ni con la pila, y no tocan la alopecia.
- Si **Server Rewards** no está cargado, no se paga nada y se avisa una vez
  en la consola. La alopecia sigue funcionando igual.

**Estadísticas** por jugador: kills, muertes y kills de headshot (solo contra
jugadores), y desde la 1.8.0 también kills y muertes **del mapa actual**
(`WipeKills`/`WipeDeaths`, que vuelven a 0 en cada wipe). Las kills cuentan
aunque no den alopecia (sleeper o cooldown).

## En pantalla

- **Contador de alopecia**, siempre visible en la **esquina superior
  derecha**: `ALOPECIA 1.174` y tu título debajo (desde la 1.6.1; antes
  ponía `CALVICIE`). Se actualiza con cada cambio. (Abajo chocaba con el
  panel de RaidableBases.)
- **Números grandes** (1.6.7): en los sitios estrechos (contador, `+X`/`-X`,
  ranking y la línea "Tu alopecia") la cifra se abrevia a partir de mil
  millones: `2.147,4 M` (millones), `1,5 B` (billones) o `9,2 T` (trillones),
  con un decimal y sin redondear hacia arriba. Por debajo sale entera, como
  siempre. En el chat y los anuncios siempre sale entera.
- **Cartera** (1.9.1): pegada debajo del contador, una tira fina con tus
  Puntos de Chola y tus pelones: `PdC 1.234     PELONES 5.678`. A partir del
  millón sale `12,3 M` y desde mil millones `1,5 mil M`. Se actualiza con el
  contador y, además, cada 3 s si ha cambiado algo (los pelones se gastan
  fuera del mod: tienda, Cambio de divisas…). Sin Server Rewards o sin
  Economics, su cifra sale como `-`; sin ninguno de los dos, no sale.
- Al ganar alopecia sale debajo (debajo de la cartera, si está) un
  **`+18`** en dorado durante 2,5 s; al perderla, un **`-117`** en óxido.
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

## El Calvario: las reliquias (v1.3)

Objetos del juego que el plugin reparte como **reliquias**: van
**directos a tu inventario** (o
caen a tus pies si lo llevas lleno) con aviso en el chat. Se pueden cambiar o
regalar como cualquier objeto, y se usan en **el Calvario de la peluquería**.

Desde la 1.6.8 el plugin **marca** todo lo que reparte con un skin propio
(`Skin ID that marks the items this plugin hands out` en la config), y el
barbero **solo cuenta y gasta los objetos marcados**. Una pila, una cinta, una
placa o unas gemas que salgan del loot normal (o de un científico) no valen
nada en el Calvario. Los objetos marcados no se apilan con los normales. Los
premios por título son objetos normales, sin marca.

Se usan hablando con **El Barbero** (tecla **E**). Ver [La peluquería](#la-peluquería-plugin-150).

**La conversación con el barbero** imita a los dependientes vanilla (como el
del pueblo pesquero). Abajo sale un cuadro con lo que dice el barbero y tus
respuestas numeradas:

```
 EL BARBERO                        Tu alopecia: 2.944 — Coronilla a la Intemperie
 "Siéntate, peludo. ¿Qué te quito hoy, el pelo o la dignidad?"

   1. Traigo una reliquia
   2. Enséñame el catálogo
   3. Vengo a sellar el Carné de Calvo
   4. Vengo a vender (o comprar) alopecia
   5. Nada, solo miraba
```

- Te saluda con una de sus frases o con uno de los **refranes** de la isla.
- **1** lista las reliquias que llevas, cada una con cuántas tienes y qué hace.
  Al elegir una, la usa y el barbero contesta **en el cuadro** (no en el chat).
- **2** (1.9.0) abre **el catálogo**: la ventana grande del Calvario, con el
  poste de barbero y los colores de la casa. Salen **todas** las reliquias con
  su icono, qué hace cada una y cuántas llevas; las que llevas tienen botón
  **USAR**, y las que no, salen en gris con **NO LLEVAS**. Abajo, la fila de
  tarjetas del **Carné de Calvo** (selladas en verde, las que llevas con `xN`,
  las que faltan con FALTA) y el botón **SELLAR**. Lo que contesta el barbero
  sale arriba, en la misma ventana. **VOLVER AL BARBERO** vuelve a la
  conversación y la **X** cierra. Como el resto del barbero, solo funciona a
  su lado.
- **3** enseña qué colores del carné tienes sellados y cuáles te faltan, con
  la opción de sellar lo que traes.
- **4** es el [cambio de alopecia](#el-cambio-de-alopecia-plugin-160). Solo
  sale si está activado en la config.

La ventana de `/calvos` va de barbería calva: rayas de poste de barbero,
barra de progreso hacia tu siguiente título, oro/plata/bronce para el podio y
cuánto te falta para adelantar al de arriba. Desde la 1.8.0 tiene tres
pestañas: **RANKING** (la de siempre), **SALÓN DE LA FAMA** (un bloque por
mapa) y **CABEZAS** (las recompensas por cabeza), y al lado de las pestañas
sale el **Calvo del Día** vigente. Ver
[Salón de la fama, cabezas y Calvo del Día](#salón-de-la-fama-cabezas-y-calvo-del-día-plugin-180).

| Objeto | Cómo se consigue | Qué hace al usarlo |
|---|---|---|
| **Lejía** (`bleach`) | 5 % al romper un barril | Apuesta: 70 % **+5.000** / 30 % **−5.000**. La única reliquia que puede salir mal |
| **Cinta americana** (`ducttape`) | 5 % al romper un barril | Tu próxima muerte **no resta** (máx. 1 activa) |
| **Pila pequeña** (`battery.small`) | 3 % al romper un barril | **x2** en todo lo que ganes durante 10 min |
| **Placa militar** (`dogtagneutral`) | 50 % al matar un NPC de tier 8-12 | **+2.000** |
| **Placas azules** (`bluedogtags`) | 30 % al matar un NPC de tier 13-17 (heavies, RaidableBases…) | **+5.000** |
| **Placas rojas** (`reddogtags`) | Siempre, a cada jugador que cobra el heli, la Bradley o el Chinook | **+15.000** |
| **Gemas** (`kickgems`) | 1 % al matar un NPC de tier 12 o más | **+50.000** |
| **Tarjetas de identificación** (11 colores) | 5 % al matar un NPC de tier 1-17, color al azar | Van al **Carné de Calvo** |

**Carné de Calvo:** entrega al barbero una tarjeta de cada color (opción
**Séllame lo que traigo**):
**+1.000** por tarjeta y **+100.000** al completar los 11 colores, con anuncio en
el cartel del centro. Después empieza un carné nuevo. Las tarjetas repetidas
no se gastan: sirven para cambiarlas con otros.

Lo que dan los objetos cuenta como ganancia normal: la **Hora de la
calvicie** lo duplica, y con la **pila** a la vez, x4. La lejía que sale mal y
el premio del carné completo no se multiplican.

Si un nombre interno no existe en la versión de Rust del servidor, el plugin
lo avisa en la consola al arrancar.

### El cambio de alopecia (plugin 1.6.0)

Opción **3. Vengo a vender (o comprar) alopecia** en la conversación con el barbero.
Alopecia, Puntos de Chola y pelones se cambian entre sí:

| Operación | Por defecto |
|---|---|
| Vender alopecia por Puntos de Chola | 1.000 de alopecia → 1 Punto de Chola |
| Vender alopecia por pelones | 1.000 de alopecia → 25 pelones |
| Comprar alopecia con Puntos de Chola | 1 Punto de Chola → 10 de alopecia |
| Comprar alopecia con pelones | 25 pelones → 10 de alopecia |

Desde la 1.11.0, con la alopecia ×10 en todo; hasta la 1.10.0 eran 100 de
alopecia por 1 Punto de Chola o 25 pelones, y 1 Punto de Chola o 25 pelones por
cada 1. Las configs que ya existían se pasan solas a las cifras nuevas.

- Las tasas son **asimétricas a propósito**: la alopecia paga Puntos de Chola cada 30 min
  para siempre, y si comprarla fuera barato sería una máquina de hacer dinero.
- Cantidades fijas (100, 1.000, 10.000, 100.000 de alopecia), solo enteras. Para
  vender, un mínimo de 1.000 y múltiplos exactos (así que 100 solo sale para
  comprar); para comprar, múltiplos de 10.
- Antes de cada cambio sale una **confirmación** ("¿Seguro que cambias 10.000 de
  alopecia por 10 Puntos de Chola?…") con **CONFIRMAR** / **ME LO PIENSO**. Lo que se confirma se
  guarda en el servidor, no en el botón, y se vuelve a comprobar al confirmar.
- Primero se paga o se cobra en Server Rewards o Economics, y solo si eso sale
  bien se toca la alopecia.
- Vender puede bajarte de título (se anuncia en el chat, como cualquier
  bajada). Lo comprado sube de título normal y cobra sus premios (desde la
  1.10.0), pero no se multiplica con eventos o la pila.
- Si Server Rewards o Economics no están cargados, sus opciones salen cerradas.

## La peluquería (plugin 1.5.0)

Tres casitas, cada una con un NPC de **HumanNPC**:

| Casa | NPC | Plugin que la atiende |
|---|---|---|
| **El Calvario** | El Barbero | Este plugin: reliquias y Carné de Calvo |
| **El Mercalvona®** | Tendero del Mercalvona | GUIShop (pelones) |
| **Cambio de divisas** (antes Premios Calvos) | Traficante | Server Rewards (Puntos de Chola) |

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
   Después: `oxide.reload IslaDeCalvos`. Por defecto (1.10.0) viene el
   `userid` del barbero del servidor, `4211000001`; en otro servidor hay que
   cambiarlo por el suyo.
3. **El Mercalvona (GUIShop).** En la tienda que quieras asignar, activa
   `EnableNPC` y pon el `userid` del tendero en `NpcIds`. Para que `/shop` no
   abra nada fuera de la casa, deja `"Set Default Global Shop to open": ""`
   (vacío). Es la forma que indica el propio GUIShop para desactivar las
   tiendas globales.
4. **Cambio de divisas (Server Rewards).** Mirando al Traficante, `/srnpc add`. En su
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
/npc name "Traficante"
/npc hello "Cambio de divisas: tu calva vale Puntos de Chola y aquí se cobra. Pasa por caja."
/npc use "A ver cuánto te ha pagado esa cabeza. Dale a la E."
/npc bye "Sigue brillando, que cada media hora te cae algo."
```

**GUIShop** (`oxide/lang/es/GUIShop.json`). Solo estas cinco; el resto ya
está en el servidor con el tono de la isla y no se toca:

```json
"NPCResponseOpen": "¡Bienvenido a {0}! ¿Qué te pongo? Dale a la E, que no tengo todo el día.",
"NPCResponseClose": "Gracias por comprar en {0}. Vuelve pronto, y más pelado.",
"GlobalShopsDisabled": "Aquí no se compra desde el sofá, señorito. Ve al Mercalvona con /peluqueria y háblale al tendero.",
"Bought": "Te llevas {0} de {1}. El tendero ya está contando tus pelones.",
"Sold": "Has vendido {0} de {1}. Con eso no te da ni para un peine."
```

**Server Rewards 2.x** (`oxide/lang/es/ServerRewards.json`). Con
`"Use NPC dealers only": true`, el aviso de Puntos de Chola sin gastar usa esta clave
(conservar la etiqueta de color):

```json
"Message.Notification.Unspent.NPC": "Busca al <color=#B6F34A>Traficante</color> con /peluqueria para gastarlos."
```

## Salón de la fama, cabezas y Calvo del Día (plugin 1.8.0)

Encargo de Jano, el que administra los servers.

### Salón de la fama por wipe

En cada **wipe** (`OnNewSave`), **antes de cualquier reset** y aunque
`Reset baldness on map wipe` esté a `false`, se guarda una entrada con el mapa
que acaba:

- la **fecha** (hora del server);
- el **podio de alopecia**: los 3 primeros con su cifra (mismo orden que el ranking);
- quien **más ha matado** y quien **más ha muerto en ese mapa**, con sus números.

Para lo de "en ese mapa" cada jugador lleva `WipeKills` y `WipeDeaths`, que
suben junto a `Kills`/`Deaths` y vuelven a 0 en el wipe, después de guardar la
entrada. Los `Kills`/`Deaths` de siempre no se tocan.

- Si nadie ha **matado ni muerto** en el mapa, no se guarda nada (1.8.2; antes
  miraba la alopecia, que con el reset apagado nunca vuelve a 0).
- **Un wipe, un cierre** (1.8.2): si el último cierre de mapa fue hace menos
  de **12 horas**, un `OnNewSave` nuevo no hace nada (ni entrada, ni anuncio, ni
  hook, ni reset): solo un aviso en la consola. Es para el relanzamiento con
  semilla nueva ~1 minuto después del wipe, que hace llegar `OnNewSave` dos veces.
- Se ve en la pestaña **SALÓN DE LA FAMA** de `/calvos`: un bloque por mapa,
  del más nuevo al más viejo, con el podio en oro, plata y bronce. Cada bloque
  lleva su número (`#3`). La pulla para el que más ha muerto sale solo en el
  bloque más nuevo (1.8.2). Las entradas de un cierre dicen "Mapa cerrado el…";
  las guardadas con `salon guardar`, "Foto del mapa del…".
- **Anuncio del ganador** (1.8.2): cada jugador, la **primera vez que despierta**
  tras el cierre, ve en **su** chat quién se lleva el mapa (el primero del podio).
  Se guarda quién lo ha visto ya, así que sale una vez por jugador y por cierre.
- `/calvoadmin salon guardar` guarda a mano una **foto** con el estado actual
  (sin anuncio en el chat, ni resetear nada). `/calvoadmin salon borrar <n>`
  quita la entrada `#n`; si era la del anuncio pendiente y queda otra entrada del
  mismo cierre, el anuncio pasa a esa.
- **Cierre a mano** (1.8.2): `/calvoadmin salon cerrar`, o `isla.salon cerrar`
  desde la consola del servidor o RCON, hace **todo lo que hace el wipe**: guarda
  la entrada, apunta el anuncio, llama al hook, pone a 0 las kills y muertes del
  mapa y, **solo si la config tiene el reset por wipe activado**, la alopecia.
  Tiene la misma protección de 12 horas; con `forzar` al final se la salta
  (`/calvoadmin salon cerrar forzar`, `isla.salon cerrar forzar`).
- Las entradas se guardan en el fichero de datos del plugin, sin tope por
  defecto (`Max entries kept`).
- **Ojo**: `OnNewSave` solo le llega al plugin si está **cargado al arrancar**
  el servidor con el mapa nuevo (Oxide compila y carga los plugins antes de
  cargar el mapa). Si tras la actualización de Rust no compilara, o se carga a
  mano después, el cierre no se hace solo: toca `salon cerrar` (a mano o por
  consola).

### Recompensas por cabeza

"Se paga por su cabellera". Con **`/cabeza <jugador> <cantidad>`** pones
**Puntos de Chola** sobre la cabeza de alguien. Se te cobran **al momento**.
Si ya tenía precio, se suma al bote.

- Mínimo: **10 Puntos de Chola** (configurable). La cantidad va al final, así
  que valen nombres con espacios (`/cabeza Pepe el Calvo 50`) y la cantidad
  admite puntos de miles (`1.000`).
- Se puede poner sobre un jugador **desconectado** (que tenga datos). No se
  puede poner sobre uno mismo.
- Quien lo **mate en PvP** se lleva **el bote entero** y sale en el chat. No
  cobra: el propio jugador (suicidio), alguien de su **equipo**, ni nadie si el
  muerto estaba **dormido o desconectado** (configurable, activado por defecto).
  Si muere por otra cosa (NPC, caída, suicidio…), el bote sigue ahí.
- **Nada se devuelve**: lo que se pone, se pierde. El bote dura hasta que
  alguien lo cobre y **el wipe no lo borra**.
- Si Server Rewards no paga al que lo mata, el bote se queda para el siguiente.
- **`/cabezas`** abre la pestaña **CABEZAS** de `/calvos`: todas las cabezas
  con precio, de la más cara a la más barata, y cuánto dan por la tuya.
- Sin **Server Rewards** cargado, `/cabeza` está cerrado.
- `/calvoadmin cabeza quitar <jugador>` anula un bote (sin devolver nada).

### Calvo del Día

Cada día a las **19:00** (hora del server, configurable; hasta la 1.9.1, a las 21:00) se elige al **Calvo
del Día**: el que más alopecia ha **ganado** desde la elección anterior (lo
ganado, no el total; las muertes restan). En cada elección se guarda una foto
de la alopecia de todos para comparar al día siguiente.

- Solo cuenta quien se haya **conectado** desde la elección anterior y haya
  ganado algo. Si nadie, ese día no hay Calvo del Día (y el anterior lo deja
  de ser).
- Se anuncia en el chat y en el **cartel grande** del centro de la pantalla
  (el de los títulos), y sale en `/calvos`, al lado de las pestañas.
- Está en el grupo de Oxide **`calvodeldia`** (configurable), y **solo él**:
  al elegir uno nuevo, el plugin saca del grupo a todos los demás. Si el grupo
  no existe, lo crea. Sirve para darle permisos, como un prefijo de Better
  Chat. No puede ser un grupo de título ni `default`, `admin` o `*`.
- **Premio** opcional (Puntos de Chola, pelones y objetos, con `"Message"`
  propio), con la misma forma que los premios por título; a 0 por defecto. Los objetos
  solo se dan si está conectado en ese momento.
- Se guarda el historial de los **últimos 30**.
- Si el servidor está apagado a las 19:00, se elige en cuanto arranca (si no
  ha pasado la medianoche). La primera vez que carga la 1.8.0 se hace la
  primera foto; si ya son más de las 19:00, la primera elección es al día
  siguiente.
- **No se compra** (1.8.2): la alopecia comprada en el barbero y los cambios de
  admin (`/calvoadmin set` y `reset`) no cuentan, ni para sumar ni para restar
  (se mueve la foto de ese jugador en la misma cantidad). Vender alopecia sí
  resta, como morir.
- `/calvoadmin calvodeldia ahora` fuerza una elección, y **cuenta como la del
  día** (1.8.2): ese día ya no se elige otra vez a las 19:00 ni se paga dos veces.
- Con `"Enabled": false`, el grupo se vacía al cargar el plugin (1.8.1): nadie
  se queda con sus ventajas.

### Más avisos para otros plugins (hooks)

Además de `OnIslaTitleChanged`, el plugin llama a estos hooks (no hace falta
devolver nada):

```csharp
OnIslaWipeHallOfFame(string json)
OnIslaBountyPlaced(ulong placerId, string placerName, ulong targetId, string targetName, long amount, long total)
OnIslaBountyClaimed(ulong killerId, string killerName, ulong targetId, string targetName, long total)
OnIslaCalvoDelDia(ulong userId, string playerName, long gained)
OnIslaHuntEnded(ulong targetId, string targetName, string outcome, string killerName)
```

- `OnIslaWipeHallOfFame`: la entrada recién guardada (cierre de mapa o foto a
  mano), en una línea de JSON. La del wipe (`OnNewSave`) llega mientras carga el
  mundo, cuando aún no hay nadie en RCON: desde la 1.8.2 se guarda como pendiente
  y se llama **60 s después de `OnServerInitialized`** (si el server se reinicia
  antes, en el siguiente arranque). Las de `salon cerrar` y `salon guardar`, al
  momento:
  `{"number":3,"date":"2026-10-01T20:00:12","manual":false,"podium":[{"id":"7656…","name":"…","alopecia":123456}],"topKiller":{"id":"7656…","name":"…","kills":12},"topDeaths":{"id":"7656…","name":"…","deaths":34}}`.
  `topKiller`/`topDeaths` van a `null` si nadie mató o murió. Los SteamID van
  como texto.
- `OnIslaBountyPlaced`: `amount` es lo que se acaba de poner y `total`, el bote
  después de sumarlo.
- `OnIslaHuntEnded`: al acabar la Cacería del peludo, con `outcome` =
  `killed`, `survived`, `died`, `escaped` o, desde la 1.8.2, `stopped` (un admin
  paró el evento). `killerName` va vacío salvo en `killed`.

### Comando de consola `isla.ranking`

Solo desde la **consola del servidor o RCON** (a un jugador no le contesta).
Devuelve en **una sola línea de JSON** todos los jugadores, de más a menos
alopecia:

```json
[{"name":"…","id":"7656…","alopecia":123456,"title":"Coronilla a la Intemperie","kills":40,"deaths":12,"wipeKills":5,"wipeDeaths":2,"online":true}]
```

`id` va como texto y `title` va vacío con 0 de alopecia (sin título).

### Comando de consola `isla.salon` (1.8.2)

`isla.salon cerrar [forzar]`: el cierre de mapa a mano (ver [Salón de la fama
por wipe](#salón-de-la-fama-por-wipe)), solo desde la **consola del servidor o
RCON**. Contesta en la consola qué ha hecho. Sin nada detrás, desde la 1.12.0,
devuelve el Salón de la fama en JSON (ver abajo).

### Comandos de consola para la web (1.12.0)

Para la web de la isla, que los lee por RCON como `isla.ranking`. Igual que él:
solo desde la **consola del servidor o RCON** y una sola línea de JSON. **Ninguno
lleva SteamIDs**, porque la web los enseña en público. Las horas van en ISO 8601
UTC (`2026-10-01T17:00:00Z`); si no se sabe una, va `null`.

**`isla.salon`**: el Salón de la fama entero, del más nuevo al más viejo.

```json
[{"wipe":"2026-10-01","manual":false,"top":[{"name":"…","alopecia":1500000,"title":"Caballero de la Tonsura"}],"mostKills":{"name":"…","kills":12},"mostDeaths":{"name":"…","deaths":9}}]
```

`wipe` es la fecha (del server) en que se guardó la entrada. `manual` es `true`
en las fotos de `/calvoadmin salon guardar`, que no son un cierre de mapa. `top`
es el podio (hasta 3); el `title` se calcula con los títulos de ahora.
`mostKills`/`mostDeaths` son `null` si nadie mató o murió en ese mapa.

**`isla.calvodeldia`**: el vigente, cuándo toca el siguiente y los últimos 14.

```json
{"current":{"name":"…","gained":50000,"since":"2026-10-01T17:00:00Z"},"nextPick":"2026-10-02T17:00:00Z","history":[{"date":"2026-10-01T17:00:00Z","name":"…","gained":50000}]}
```

`current` es `null` si no hay Calvo del Día. `since` y `date` son la hora de la
elección. `nextPick` es la siguiente elección programada: si ya ha pasado la hora
de hoy sin elegir, es "ahora", porque se elige en el siguiente minuto. Es `null`
con el Calvo del Día desactivado. `history` va del más nuevo al más viejo.

**`isla.cabezas`**: las cabezas con precio, de la más cara a la más barata.

```json
[{"target":"…","amount":500,"placedBy":["…","…"],"since":"2026-10-01T16:00:00Z"}]
```

`amount` es el total en Puntos de Chola. `placedBy` es quién ha puesto Puntos
de Chola, cada uno una vez y en orden. `since` es cuándo se puso el primero.
Las dos cosas se apuntan desde la 1.12.0: un bote de antes sale con
`"placedBy": []` y `"since": null`. Si alguien sube un bote de antes, sale él
solo en `placedBy`, y `since` sigue en `null`.

**`isla.evento`**: el evento global en marcha.

```json
{"active":"HairiestHunt","name":"Cacería del peludo","endsAt":"2026-10-01T21:06:05Z","target":"…"}
```

`active` es `BaldHour`, `ShampooRain`, `HairiestHunt` o `BladeStorm`. `name` es
el nombre del juego. `target` es el peludo en la cacería, y `null` en los demás
eventos. Sin evento sale `{"active":null,"nextAt":"…Z"}`. `nextAt` es cuándo se
intenta el siguiente evento al azar; no siempre sale uno, porque no los hay si
no hay nadie conectado. Con los eventos apagados, o justo después de cargar el
plugin, sale solo `{"active":null}`.

## Comandos

| Comando | Quién | Qué hace |
|---|---|---|
| `/calvos` | Todos | Abre la ventana de la isla: **ranking** de todo el servidor (de 10 en 10, con tu posición), **Salón de la fama** por mapa y **cabezas** con precio. Se cierra con la **X**. Las reliquias se usan hablando con el barbero de la peluquería. |
| `/cabeza <jugador> <cantidad>` | Todos | Pone Puntos de Chola por la cabeza de un jugador (se cobran al momento y no se devuelven). |
| `/cabezas` | Todos | Abre `/calvos` en la pestaña de cabezas. |
| `/calvoadmin set <jugador> <valor>` | Admin | Fija la alopecia de un jugador (entero, 0 o más). |
| `/calvoadmin reset <jugador>` | Admin | Pone la alopecia de un jugador a 0. |
| `/calvoadmin evento <hora\|champu\|peludo\|alopecia>` | Admin | Lanza ese evento ya, sin esperar a la hora. Para probar. (`cuchillas` también vale para el Brote de alopecia.) |
| `/calvoadmin evento parar` | Admin | Cancela el evento en marcha. |
| `/calvoadmin debug on\|off` | Admin | Muestra en tu chat cada cambio de alopecia (de cualquier jugador) con su motivo: NPC, tier, valor… También avisa cuando algo **no** da alopecia y por qué. Para probar. Se apaga al recargar el plugin. |
| `/calvoadmin salon guardar` | Admin | Guarda a mano una foto del mapa en el Salón de la fama (sin cerrar nada). |
| `/calvoadmin salon cerrar [forzar]` | Admin | Cierre de mapa completo, como el wipe: entrada, anuncio, hook, kills y muertes del mapa a 0 y reset de alopecia si la config lo dice. Sin `forzar`, no hace nada si el último cierre fue hace menos de 12 horas. |
| `/calvoadmin salon borrar <n>` | Admin | Quita la entrada `#n` del Salón de la fama. |
| `/calvoadmin cabeza quitar <jugador>` | Admin | Anula el bote que haya por la cabeza de ese jugador (no se devuelve a nadie). |
| `/calvoadmin calvodeldia ahora` | Admin | Elige ya al Calvo del Día. Cuenta como la elección del día: a las 19:00 ya no hay otra. |
| `isla.ranking` | Consola del servidor / RCON | Todos los jugadores en una línea de JSON (ver arriba). |
| `isla.salon cerrar [forzar]` | Consola del servidor / RCON | Lo mismo que `/calvoadmin salon cerrar`. |
| `isla.salon` | Consola del servidor / RCON | El Salón de la fama en JSON, para la web (1.12.0). |
| `isla.calvodeldia` | Consola del servidor / RCON | El Calvo del Día vigente, la siguiente elección y los últimos 14, en JSON (1.12.0). |
| `isla.cabezas` | Consola del servidor / RCON | Las cabezas con precio en JSON (1.12.0). |
| `isla.evento` | Consola del servidor / RCON | El evento global en marcha (o cuándo toca el siguiente) en JSON (1.12.0). |

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
| `Baldness gained per player kill` | 10000 | Alopecia por kill. |
| `Baldness gained per headshot kill (instead of the normal kill reward)` | 10000 | Alopecia por kill de headshot. |
| `Baldness gained per survival interval` | 100 | Alopecia por sobrevivir. |
| `Survival interval (minutes alive and connected)` | 30 | Cada cuántos minutos se gana. |
| `Survival reward only if the player moved during the interval (not AFK)` | true | Si no te has movido en el intervalo, no cobras (misma comprobación que la paga de Puntos de Chola). |
| `Baldness lost on death (% of current baldness)` | 10 | Porcentaje de tu alopecia que pierdes al morir. |
| `Deaths caused by NPCs lower baldness` | true | Si morir a manos de un NPC resta alopecia. |
| `Kill cooldown per victim (minutes)` | 30 | Anti-farmeo por víctima (0 = sin cooldown). |
| `Announce when a player reaches the highest title` | true | Anuncio de calvicie suprema. |
| `Announce when a player rises to a higher title` | true | Anuncio de subida de título. |
| `Announce when a player drops to a lower title` | true | Anuncio de bajada de título. |
| `Reset baldness on map wipe (stats are kept)` | false | Si el wipe pone a todos a 0. |
| `Titles (minimum baldness -> title)` | ver tabla | Lista de títulos y desde cuántos puntos se consiguen. |
| `NpcTiers` | ver tabla | `"<shortprefabname>": <tier>` para cada NPC que da alopecia. |
| `TierRewards` | curva 10 … 10000 | `"<tier>": <alopecia>` para los tiers 1-20. Números enteros. |
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
  "1": 10,
  "9": 180,
  "20": 10000
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
  "Show wallet under the counter": true,
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
  "Bleach (gamble)": { "Item shortname": "bleach", "Drop chance (0-1)": 0.05, "Win chance (0-1)": 0.7, "Baldness on win": 5000, "Baldness lost on fail": 5000 },
  "Duct tape (your next death costs nothing)": { "Item shortname": "ducttape", "Drop chance (0-1)": 0.05 },
  "Small battery (personal gain multiplier)": { "Item shortname": "battery.small", "Drop chance (0-1)": 0.03, "Gain multiplier": 2, "Duration (minutes)": 10 },
  "Dog tag": { "Item shortname": "dogtagneutral", "Drop chance (0-1)": 0.5, "Baldness when used": 2000, "Drops from NPC tier (min)": 8, "Drops from NPC tier (max)": 12 },
  "ID tags (Carne de Calvo collection)": { "Drop chance per NPC kill (0-1)": 0.05, "Baldness per delivered tag": 1000, "Bonus for completing all colors": 100000 }
}
```

Bloque de Puntos de Chola (valores por defecto). Las claves son la alopecia mínima, igual
que los títulos:

```json
"Server Rewards (RP by title)": {
  "Enabled": true,
  "Interval (minutes alive and connected)": 30,
  "Only pay players who moved during the interval (not AFK)": true,
  "Minimum movement between checks to count as active (meters)": 1.0,
  "Tell the player in chat when RP is paid": true,
  "RP per X baldness (0 = use the table)": 100,
  "RP per interval by title (minimum baldness -> RP)": {}
}
```

Bloques de premios por título y del cambio (valores por defecto). Las claves de
los premios son la alopecia mínima de cada título:

```json
"Tier prizes (title minimum baldness -> prize)": {
  "1": { "RP (Server Rewards)": 0, "Coins (Economics)": 2500,
         "Items": [ { "Item shortname": "knife.bone", "Amount": 1 } ],
         "Spawn prefabs": [],
         "Message": "Toma este trozo de hueso afilado. Empieza a raparte solito." },
  "1000": { "RP (Server Rewards)": 100, "Coins (Economics)": 10000, "Items": [], "Spawn prefabs": [], "Message": "" },
  ...
  "1000000": { "RP (Server Rewards)": 15000, "Coins (Economics)": 0, "Items": [],
               "Spawn prefabs": [ "assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab" ],
               "Message": "" },
  ...
},
"Sync title groups": true,
"Title groups (title minimum baldness -> Oxide group)": {
  "1": "calvo1", "1000": "calvo2", "10000": "calvo3", "100000": "calvo4",
  "1000000": "calvo5", "10000000": "calvo6", "100000000": "calvo7"
},
"Baldness exchange (El Calvario)": {
  "Enabled": true,
  "Sell: baldness for 1 RP": 1000,
  "Sell: coins per 1000 baldness": 25,
  "Buy: RP per 10 baldness": 1,
  "Buy: coins per 10 baldness": 25,
  "Minimum baldness to sell": 1000,
  "Amounts offered (baldness)": [ 100, 1000, 10000, 100000 ]
}
```

(Los demás títulos, en la tabla de **Premio por subir de título**. Hasta la
1.9.1 los premios venían vacíos por defecto.)

En `"On-screen UI"` están
`"Show a banner to everyone when a player rises to a higher title": true`,
`"Seconds the title-up banner stays": 6.0` y, desde la 1.7.0,
`"Show title drop banner": true`.

La config lleva además un `Config version (do not edit)`. Sirve para que una
actualización pueda corregir valores ya guardados (la 1.3.1 mueve el contador
a la esquina superior derecha una sola vez; la 1.9.1 añade la cartera; la 1.11.0
pasa el cambio a las claves nuevas y cambia el `minicopter` por el helicóptero). No lo
toques.

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

Bloques de la 1.8.0 (valores por defecto):

```json
"Hall of fame (one entry per map wipe)": {
  "Enabled": true,
  "Max entries kept (0 = no limit)": 0,
  "Announce the winner in chat after the wipe": true
},
"Bounties (/cabeza)": {
  "Enabled": true,
  "Minimum amount (Puntos de Chola)": 10,
  "Not paid if the victim was sleeping or disconnected": true
},
"Calvo del Día (top alopecia gainer of the last 24 h)": {
  "Enabled": true,
  "Pick time (server time, HH:mm)": "19:00",
  "Oxide group": "calvodeldia",
  "History entries kept": 30,
  "Prize": { "RP (Server Rewards)": 0, "Coins (Economics)": 0, "Items": [], "Spawn prefabs": [], "Message": "" }
}
```

`"Oxide group": ""` = sin grupo. Una hora mal escrita vuelve a la de por defecto
(`19:00`) con un aviso en la consola.

Los textos de los mensajes se editan en `oxide/lang/es/IslaDeCalvos.json` y
`oxide/lang/en/IslaDeCalvos.json`. Los dos están en español por defecto: la
mayoría de jugadores tienen el cliente en inglés, y Oxide les mostraría el
fichero `en`.

Los datos de los jugadores se guardan en `oxide/data/IslaDeCalvos.json`, en
cada guardado automático del servidor y al descargar el plugin o apagar el
servidor. Desde la 1.8.0 van en el mismo fichero el Salón de la fama
(`HallOfFame`), las cabezas con precio (`Bounties`) y el Calvo del Día
(`CalvoDelDia`: foto de alopecia, vigente e historial).

## Instalación

1. Ten un servidor dedicado de Rust con **Oxide (uMod)** instalado.
2. Copia `src/IslaDeCalvos.cs` en la carpeta `oxide/plugins/` del servidor.
3. Oxide lo compila y carga solo. En la consola deberías ver algo como
   `Loaded plugin Isla de Calvos v1.11.0 by Igor Monasterio`.
4. Para recargarlo tras cambiar el fichero (normalmente se recarga solo):
   `oxide.reload IslaDeCalvos`

Si falla la compilación, el error sale en la consola y en `oxide/logs/`.

## Documentación

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): cómo es un plugin de Oxide,
  hooks usados y convenciones del proyecto.
- [`CLAUDE.md`](CLAUDE.md): contexto para sesiones de Claude.

## Licencia

[MIT](LICENSE)
