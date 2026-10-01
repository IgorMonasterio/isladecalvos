# CLAUDE.md — contexto para sesiones de Claude

## Proyecto

Plugin principal del servidor modded de Rust **[ES] Isla de Calvos**. Temática: todo
lo que pasa en la isla acaba teniendo que ver con el pelo o la calvicie.

## Premisa (lore)

- **El pelo está sobrevalorado; el futuro es calvo.** El pelo es una
  maldición que vuelve a crecer.
- Cada jugador tiene un **nivel de alopecia**: entero persistente, **sin
  límite por arriba**, nunca por debajo de 0. Matar y sobrevivir lo suben;
  morir hace que crezca el pelo (lo baja).
- Es humor: **no hay cambio visual del pelo**. Todo son puntos, títulos y mensajes.
- **En el juego la cifra se llama alopecia** ("Alopecia 25.000"), decidido por
  Igor. "Calvicie", "calva" y demás sinónimos solo valen para chistes y
  nombres de eventos ("Vender calva", "Hora de la calvicie"). Los
  identificadores del código siguen siendo `Baldness` y compañía, y no se
  tocan.
- La alopecia da **Puntos de Chola** (los RP de Server Rewards) cada 30 min:
  1 Punto de Chola por cada 100 de alopecia, sin tope (lineal desde la 1.6.0;
  antes, por escalones de título). **1 Punto de Chola = 25 pelones** (la moneda
  de Economics). Desde la 1.6.0 hay además un cambio de alopecia ↔ Puntos de
  Chola ↔ pelones en El Calvario.
- **Nombres de las divisas** (decidido por Igor el 2026-09-27): los RP se llaman
  **Puntos de Chola** (singular "1 Punto de Chola", masculino; abreviatura
  **PdC** donde no quepa) y las monedas, **pelones** (singular "1 pelón",
  masculino). "RP" y "monedas" no aparecen en nada que vea un jugador. La tasa
  pasó de 1 = 10 a **1 = 25**. En el código y en las claves de la config siguen
  `Rp`, `Coins`, "RP (Server Rewards)"… y no se tocan.

## Estado y hoja de ruta

Cada versión tiene su issue en GitHub. Solo se trabaja en lo que tenga el
alcance cerrado:

- **v1.0** — sistema de alopecia: puntos, títulos, ranking, anti-farmeo, comandos.
  Implementada (issue #2).
- **v1.1** — eventos globales (issue #3): Hora de la calvicie, Lluvia de
  champú, Cacería del peludo (hasta la 1.6.3, Cazar al más peludo) y Brote de
  alopecia (de la 1.6.2 a la 1.6.5 se llamó Tormenta de cuchillas). Uno al
  azar cada hora.
  Implementada.
- **v1.2 (plugin 1.2.0)** — en pantalla: contador de alopecia fijo abajo a la
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
  Cambio de divisas, antes Premios Calvos → Server Rewards 2.0.8). TP con
  `/peluqueria` (Dynamic Command de NTeleportation). `/shop` y `/s` solo en sus NPC. `/calvos` solo muestra el
  ranking; los objetos se usan hablando con el barbero. El plugin solo
  escucha `OnUseNPC` de HumanNPC; lo demás es config de esos plugins.
  (1.5.1: El Calvario es una conversación con el barbero, al estilo de los
  dependientes vanilla.)
- **Plugin 1.6.0** — encargo de Igor: cartel grande al subir de título; premios
  por título (RP, monedas, objetos; una vez por jugador y título, todo a 0 por
  defecto hasta la 1.10.0); RP lineal (`floor(alopecia/100)`); títulos ×10 por defecto; y
  **cambio de alopecia** en el barbero, con tasas asimétricas y confirmación.
- **Plugin 1.6.1–1.6.3** — la cifra pasa a llamarse alopecia en el juego: el
  contador pone `ALOPECIA` (1.6.1), el Brote de alopecia se renombra a
  Tormenta de cuchillas (1.6.2) y los textos que llamaban "calvicie" a la
  cifra dicen "alopecia" (1.6.3). Todo con claves de lang nuevas.
- **Plugin 1.6.4** — `docs/TONO.md` entra en el repo y los textos del plugin
  se repasan contra él: fuera "la gloria", las muletillas repetidas y los
  chistes en botones y textos de admin; la cacería pasa a llamarse Cacería del
  peludo; remates dobles recortados.
- **Plugin 1.6.5** — colores de la casa de TONO.md en textos e interfaz
  (dorado `#e0a526`, óxido `#e0662f`, gris `#9a9288`), confirmado por Igor.
- **Plugin 1.6.6** — TONO.md afinado ("repasar no es podar"): vuelve el nombre
  Brote de alopecia y vuelven los remates que se quitaron en la 1.6.4, con las
  coletillas cambiadas (tieso, Fórmula 1, figura). La gloria sigue fuera.
- **Plugin 1.6.7** — números grandes, pedido por Igor: la alopecia sigue en
  `long` pero las sumas se saturan en vez de desbordar; en los sitios
  estrechos la cifra se abrevia a M/B/T desde mil millones; los RP no pasan
  nunca del tope de 32 bits de Server Rewards, y si Server Rewards está
  parcheado a `long` (`AddPointsLong`/`TakePointsLong`/`CheckPointsLong`) se
  usa eso. Ver ARCHITECTURE §4f.
- **Plugin 1.6.8** — tres fallos que encontró Jano: la supervivencia pide
  haberse movido (como la paga de RP); los objetos malditos llevan un skin
  propio y el barbero solo cuenta los marcados; y si falta el parche de 64 bits
  de Server Rewards, la consola lo avisa una vez por detección. En la misma
  versión entran seis frases escritas por Igor (calvicie suprema, fin de la
  Lluvia de champú, cacería cazada y sobrevivida, y dos de depuración).
- **Plugin 1.6.9** — las divisas cambian de nombre (Puntos de Chola y pelones,
  decisión de Igor del 2026-09-27) en todos los textos, con claves nuevas; la
  tasa del cambio de calva por pelones pasa a 25 por defecto.
- **Plugin 1.7.0** — encargo de Jano con decisión de Igor: **ventajas por título**.
  Cada jugador está siempre en el grupo de Oxide de su título (`calvo1`…`calvo7`;
  con 0, en ninguno) y las ventajas (homes y espera de NTeleportation, mochila de
  Backpacks, título de Better Chat) las da Jano con permisos de esos grupos: el
  plugin solo mueve de grupo. El premio se cobra una vez; el grupo sigue siempre
  al título actual. Además: objetos de premio en silencio, `"Message"` propio por
  premio, cartel grande al bajar de título y el hook `OnIslaTitleChanged` para
  JanoBridge. Con 0 de alopecia no hay título real: el premio de Greñas Sucias
  se cobra al pasar de 0 a 1 (antes nunca se pagaba).
- **Plugin 1.8.0** — encargo de Jano (el que administra los servers), todo en una PR:
  - **Salón de la fama por wipe**: en `OnNewSave`, antes de cualquier reset (y
    aunque el reset esté apagado), se guarda una entrada con la fecha, el podio de
    alopecia y quién más ha matado y más ha muerto **en ese mapa** (contadores
    nuevos `WipeKills`/`WipeDeaths`, que vuelven a 0 en cada wipe; `Kills`/`Deaths`
    no se tocan). Pestaña en `/calvos` y anuncio del ganador en el chat cuando
    despierta el primer jugador tras el wipe. `/calvoadmin salon guardar|borrar <n>`.
  - **Recompensas por cabeza**: `/cabeza <jugador> <cantidad>` pone Puntos de Chola
    sobre alguien (se cobran al momento; mínimo 10 por defecto). Se los lleva
    enteros quien lo mate en PvP (no el propio, ni su equipo, ni si estaba dormido
    o desconectado). Nada se devuelve y el wipe no borra los botes. `/cabezas` y
    pestaña en `/calvos`; `/calvoadmin cabeza quitar <jugador>`. `/cabeza` y
    `/cabezas` son comandos de jugador nuevos, junto a `/calvos`.
  - **Calvo del Día**: cada día a las 21:00 (hora del server; 19:00 desde la 1.10.0) gana quien más
    alopecia ha ganado desde la elección anterior (foto de la alopecia de todos en
    cada elección), entre los que se han conectado en ese tiempo. Anuncio, cartel,
    grupo de Oxide `calvodeldia` (solo el vigente), premio opcional (a 0) e
    historial de 30. `/calvoadmin calvodeldia ahora`.
  - Hooks para JanoBridge (`OnIslaWipeHallOfFame`, `OnIslaBountyPlaced`,
    `OnIslaBountyClaimed`, `OnIslaCalvoDelDia`, `OnIslaHuntEnded`) y el comando de
    consola `isla.ranking` (JSON en una línea; solo consola del servidor y RCON).
  - La pestaña del ranking de `/calvos` pasa a llamarse RANKING, para no chocar
    con la del Salón de la fama.
- **Plugin 1.8.1** — dos arreglos de la revisión de la 1.8.0: el anuncio del Calvo
  del Día dice "desde la última elección" en vez de "en 24 horas", que no siempre era
  verdad (`CalvoDelDiaChatV2`); y con el Calvo del Día desactivado, el grupo
  `calvodeldia` se vacía al cargar.
- **Plugin 1.8.2** — arreglos de la revisión, encargo de Jano antes del wipe del
  1 de octubre: el cierre de mapa sale de `OnNewSave` a `CloseMap` y no se repite si
  el último fue hace menos de 12 h (el server se relanza con semilla nueva y
  `OnNewSave` llega dos veces); la entrada del cierre mira kills y muertes del mapa;
  cierre a mano con `/calvoadmin salon cerrar [forzar]` y `isla.salon cerrar
  [forzar]` (consola/RCON); el hook del salón del wipe espera a 60 s después de
  `OnServerInitialized`; el anuncio del ganador llega a cada jugador la primera vez
  que despierta; la alopecia comprada y la de admin no cuentan para el Calvo del
  Día, y su `ahora` cuenta como la elección del día; `OnIslaHuntEnded` con
  `stopped`; y textos repasados con claves nuevas.
- **Plugin 1.8.3** — solo textos, encargo de Jano con frases de Igor: remate nuevo
  del Brote de alopecia ("Al rape."), paga de Puntos de Chola más corta ("Cobra y
  calla.") y el cambio del barbero dice "alopecia" en vez de "calva" en botones y
  textos de compraventa. Claves nuevas; TONO.md con las reglas.
- **Plugin 1.9.0** — encargo de Jano, con lo que pidió Igor:
  - **Catálogo del barbero**: opción nueva "Enséñame el catálogo" que abre la
    ventana grande de la 1.3.1 (todas las reliquias con icono, qué hacen y cuántas
    llevas; botón de usar en las que llevas y gris con "NO LLEVAS" en las demás;
    fila del Carné de Calvo con botón de sellar), con volver al barbero y X. Solo
    al lado del barbero, como el resto; sigue sin haber `/calvario`.
  - **"Objetos malditos" pasa a "reliquias"** en todo texto para jugadores (casi
    todas dan ventajas; la lejía es la única que puede salir mal). Claves nuevas.
    Los nombres internos (`CursedItemKeys`, config `"Cursed items"`, skin
    9202609270) no cambian.
  - El log `Unlisted NPC killed` ya no sale por puertas, construcciones y
    desplegables de las bases reventadas (ARCHITECTURE §4l).
- **Plugin 1.9.1** — el resto del encargo de la 1.9.0 (Jano, lo pidió Igor):
  **cartera** debajo del contador de alopecia, en una tira fina: `PdC 1.234
  PELONES 5.678` (a partir del millón `12,3 M`; desde mil millones `1,5 mil M`).
  Se redibuja con el contador y con un sondeo cada 3 s (solo si cambia), porque
  los pelones cambian fuera del mod. Config `"Show wallet under the counter"`
  (true; migración 191). El `+X`/`-X` baja por debajo de la cartera. El NPC del
  Cambio de divisas se llama ahora **Traficante** (docs).
- **Plugin 1.10.0** — encargo de Igor (issue #38): **los valores por defecto de la
  config son los del servidor**. Una instalación limpia genera exactamente el
  `oxide/config/IslaDeCalvos.json` del server a 2026-10-01 (salvo el icono del
  chat, que ya era el del código y sin la opción que se elimina). Cambia: kill PvP +10.000
  (PR #37, incluida), `TierRewards` de NPC ×10 (de 10 a 10.000), premios por
  título con valores (pelones y Puntos de Chola ya ×10, y objetos; mensaje
  propio en Greñas Sucias), Calvo del Día a las 19:00, el `userid` del barbero
  (4211000001) y la tabla de Puntos de Chola por título vacía (la paga es la
  lineal). Versión de las 17:20: **la alopecia comprada en el barbero cobra
  premios de título siempre** (Igor: "la alopecia es alopecia"); fuera la opción
  `Tier prizes also for bought baldness` y el parámetro `bought`. Sin migración:
  los configs existentes no se tocan (la clave vieja se ignora y desaparece al
  guardar). Si cambia la config
  del server y Igor quiere que el repo la siga, se repite esto.
- **Plugin 1.11.0** — encargo de Igor (issue #40), alopecia ×10 en lo que faltaba:
  - **El Calvario ×10** por defecto (en el server desde el 1 de octubre, 17:35): lejía
    ±5.000, placa militar 2.000, azules 5.000, rojas 15.000, gemas 50.000; Carné de
    Calvo 1.000 por tarjeta y 100.000 al completarlo.
  - **Cambio ×10** con claves nuevas: vender 1.000 de alopecia = 1 Punto de Chola =
    25 pelones (mínimo 1.000); comprar 1 Punto de Chola o 25 pelones por cada 10.
    Claves `Sell: coins per 1000 baldness`, `Buy: RP per 10 baldness`, `Buy: coins per
    10 baldness`; textos del barbero con claves nuevas. **Migración 1110**: si el
    fichero trae las claves viejas, se convierten (ARCHITECTURE §4n). Las cantidades
    ofrecidas no cambian (100 solo sale para comprar).
  - **Premio de Caballero de la Tonsura**: el objeto `minicopter` no existe en Rust.
    Ahora son los 15.000 Puntos de Chola y un **helicóptero de combate** spawneado
    delante del jugador, donde esté. Los premios admiten `"Spawn prefabs"` además de
    `Items`. La migración cambia el `minicopter` por el helicóptero en los configs que
    lo tenían.
- **Plugin 1.12.0** — encargo de Igor (issue #42), para la web de la isla
  (isladecalvos.martigor.org, que lee por RCON): cuatro comandos de consola/RCON más
  que devuelven JSON en una línea, como `isla.ranking`: `isla.salon` (sin argumentos;
  `isla.salon cerrar` sigue igual), `isla.calvodeldia`, `isla.cabezas` e `isla.evento`.
  **Sin SteamIDs**; horas en ISO 8601 UTC. Para las cabezas se apunta desde ahora quién
  puso y desde cuándo (`BountyInfo`); el juego no cambia. Formato en el README y
  ARCHITECTURE §4o.

Decisiones de diseño de la v1.0 que no venían en la especificación inicial:
- Toda muerte resta alopecia (PvP, NPC, entorno, suicidio), también la de un
  sleeper.
- Matar a un sleeper o a un desconectado cuenta en las estadísticas, pero no
  da alopecia. El cooldown por víctima solo frena la alopecia, no las
  estadísticas.
- Una muerte por desangrado o rendición tras un derribo se atribuye a quien derribó.
- El reset por wipe solo pone a cero la alopecia; las estadísticas se conservan.
- Los cambios hechos con `/calvoadmin` no generan anuncios globales.

Cambio de diseño de la v1.0 (el servidor tiene pocos jugadores, así que los
NPCs tienen que dar alopecia):
- NPCs por **tiers 1-20**: `NpcTiers` (por `ShortPrefabName`, nunca por la ruta
  completa) y `TierRewards` (alopecia por tier: de 1 a 1000; de 10 a 10.000 desde la 1.10.0).
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
- PvP: **toda kill +10000** (headshot o no; Igor, 2026-10-01: antes 1000). **Toda muerte −10 % de la
  alopecia actual** (redondeado hacia arriba), sea cual sea la causa;
  Lluvia de champú −20 %.
- Supervivencia: +100 cada 30 min vivo y conectado (y moviéndose, desde la 1.6.8).
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
  sin Puntos de Chola o sin pelones. HumanNPC no es dependencia de código: solo se escucha
  su hook `OnUseNPC`. NTeleportation, Backpacks y Better Chat tampoco: el plugin
  solo mete a cada jugador en el grupo de su título (1.7.0) y al Calvo del Día en
  el suyo (1.8.0). Las recompensas por cabeza (1.8.0) necesitan Server Rewards:
  sin él, `/cabeza` está cerrado.

## Estructura

```
src/IslaDeCalvos.cs     # el plugin entero, organizado con #region
docs/ARCHITECTURE.md    # cómo es un plugin Oxide, hooks verificados y convenciones
docs/TONO.md            # tono y vocabulario de todo texto para jugadores
README.md               # descripción, instalación, comandos, permisos, config
```

Lee `docs/ARCHITECTURE.md` antes de tocar código.

## Reglas

- **Textos para jugadores**: antes de escribir, cambiar o traducir cualquier
  texto que vea un jugador (lang, mensajes, carteles, NPCs, README de cara al
  jugador), lee `docs/TONO.md` y cúmplelo. Si Igor cambia algo del tono o del
  vocabulario, se actualiza en ese fichero, no en otro sitio.
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
- **Contexto del servidor** (a 2026-09-27): hay dos servidores. El modded es el
  **principal** y se llama **"[ES] Isla de Calvos"** (el de este plugin); el
  vanilla es **"[ES] Isla de Calvos - Vanilla"**. Del principal: 1-3 jugadores, x5 gather, loot x1
  (BetterLoot), StackSizeController x5, RaidableBases (NPCs `scientistnpc_heavy`,
  skinID 3710562502), Economics + GUIShop (pelones) y ServerRewards **2.0.8**
  (Puntos de Chola, 1 = 25 pelones; sus claves de lang no son las de la v1, p. ej.
  `Message.Notification.Unspent.NPC`). No hay otros plugins que den
  recompensas por kills. Tiendas de la peluquería: **el Mercalvona®**
  (masculino: "al Mercalvona"; GUIShop, pelones) y **Cambio de divisas**
  (Server Rewards, Puntos de Chola; su NPC es el "Traficante", antes "Cambista de divisas"). Economics empieza
  en 0 pelones y los saldos no se borran en el wipe. Desde el 2026-09-27 el
  Server Rewards del servidor está parcheado a `long` por Jano
  (`AddPointsLong`/`TakePointsLong`/`CheckPointsLong`; `CheckPoints` y
  `OnPointsUpdated` siguen en `int` recortado). Una actualización de Server
  Rewards pisa el parche hasta que Jano lo vuelve a aplicar. Su comando de
  admin es `rp` (`rp check/add/take <jugador>`).
- **Menú `/info`** (plugin IslaInfo): lo mantiene Jano, el Claude que
  administra el servidor. Si un PR cambia números, mecánicas, comandos o
  nombres de eventos, se dice en la descripción del PR para que lo actualice.
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
- **Ganancias de alopecia siempre por `GainBaldness`** (para que los eventos
  puedan multiplicarlas). `ChangeBaldness` directo solo para admin, muertes,
  premios de eventos y alopecia comprada en el cambio (esta cobra premios de
  título como cualquier otra desde la 1.10.0; lo que no hace es multiplicarse ni
  contar para el Calvo del Día).
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
