# HISTORIA.md — cómo se hizo el plugin Isla de Calvos

Crónica de la conversación entre Igor y el Claude de GitHub (el "Jano de GitHub") del
24 al 27 de septiembre de 2026, sacada de la transcripción completa de la sesión.
Sirve para el vault de Igor y para que Jano (el Claude del servidor) sepa qué se habló.

Es un repo público: aquí no hay IPs, puertos, contraseñas, tokens, SteamIDs ni datos
personales. Las citas de Igor van entre comillas y tal cual (salvo erratas del dictado
por voz).

## Quién es quién

- **Igor**: dueño del servidor y del proyecto. Decide todo. No es programador; es la
  primera vez que usa GitHub. Tiene unas 2.000 horas de Rust. Casi siempre escribe por
  dictado de voz desde el móvil.
- **Jano (el del server)**: el Claude que administra el servidor desde el NAS de Igor
  (RCON, ficheros de config y datos, recargas). Mantiene el menú `/info` (plugin
  IslaInfo). Igor copia y pega sus mensajes en esta conversación, y las respuestas
  vuelven por el mismo camino.
- **El Claude de GitHub** (este): escribe el plugin en este repo, rama
  `claude/fervent-archimedes-97i6op`, con una PR por cambio.
- **Otro Claude** sube las versiones al servidor.
- **Gran Calvo Jano** (antes "Señor Jano"): la IA del chat del juego. No es este Claude.

---

## 1. Cronología

### Día 1 — 24/09/2026

**Base del proyecto (PR #1, 0.1.0).** El primer encargo de Igor fue solo el esqueleto:
uMod/Oxide + C#, nada de Carbon, "la funcionalidad del plugin TODAVÍA NO ESTÁ DECIDIDA.
No inventes features". Se crearon `.gitignore`, el esqueleto `src/IslaDeCalvos.cs`,
`docs/ARCHITECTURE.md`, `CLAUDE.md` y el README.
- **Compilación:** se comprobó que desde el entorno cloud **no se puede compilar contra
  las DLL reales** de Rust y Oxide, porque son de Facepunch y no se pueden bajar. Se
  compila contra imitaciones (stubs) y la prueba de verdad es cargarlo en el servidor.
  Esto se repite en cada versión.
- **Proteger la rama principal:** Igor preguntó "¿debería proteger la rama principal?".
  Se le recomendó una protección ligera.

**v1.0 — el sistema de calvicie (issue #2, PR #6).** Primera especificación de Igor:
"En Isla de Calvos ser calvo es la gloria. El pelo es una maldición que vuelve a crecer".
- **Cifra:** calvicie en **% (0-100)** por jugador.
- **Ganar:** kill +3, headshot +7, +1 cada 30 min vivo.
- **Perder:** muerte −5, y −3 extra si es por headshot.
- **Anti-farmeo:** sleepers, cooldown por víctima y suicidio.
- **Títulos:** Aspirante a Calvo … Dios Calvo.
- **Anuncios:** "CALVICIE SUPREMA" y "le está saliendo pelo".
- **Comandos:** `/calvo`, `/calvos` y `/calvoadmin`.
- **Config y textos:** config JSON, textos por lang y opción de reset en el wipe.
- Se crearon los issues #3 (eventos), #4 (objetos malditos) y #5 (peluquería/NPC).

Igor contestó tres dudas:
- Morir dormido **también resta**.
- **Toda muerte resta** (NPC, caída, suicidio…).
- El reset por wipe **solo pone a cero la calvicie**; las estadísticas se quedan.

Decisiones tomadas en esa versión:
- Una muerte por desangrado tras un derribo cuenta para quien derribó.
- Los cambios de `/calvoadmin` no se anuncian.
- Los textos en español se registran también como `en`, porque el cliente de Rust suele
  ir en inglés.

**NPCs por tiers (PR #7).** "El servidor tiene muy pocos jugadores, así que las kills de
NPC tienen que dar calvicie YA."
- **Tabla de Igor:** tiers 1-20 por `ShortPrefabName`, de la gallina (T1) al heli (T20).
- **NPC no listado:** no da nada y se avisa una vez en consola.
- **Desactivados:** `npc_bandit_guard` y los `sentry.*` quedan en la config pero apagados.
- **Recompensa compartida** para heli, Bradley y Chinook: cobran todos los que les hicieron
  daño y sus compañeros de equipo a menos de 300 m, una vez cada uno. Se montó como
  sistema genérico de "recompensas de evento".
- **Morir por NPC** resta como cualquier muerte.
- **Depuración:** `/calvoadmin debug on|off`.

**De porcentaje a puntos sin límite.** Igor, en el mismo rato: "lo que quiero es que sea
como una tabla de puntuación que no tenga límite, que pueda subir hasta el infinito…
el NPC tier 1 daría uno y el NPC de tier 20 daría 1000".
- **Idea de economía:** apuntó ya la idea de pagar dinero según el nivel ("nivel 100 → 10
  monedas a la hora…"), pero "todo esto no quiero que lo implementes aún". Quedó en el
  issue #8.
- **Nombre de la escala:** tampoco lo tenía claro ("100.000 de poder calvo, no sé, ja, ja").
- **Primeros números:** títulos "del 1 al millón y suben por cada cero añadido"; el anuncio
  al llegar al título más alto y también en cada subida; kill 800, headshot 1.000 y muerte
  "siempre −1000 en cualquier circunstancia, incluso si te tropiezas y te caes por un
  barranco".
- **Enseguida los cambió:** "supervivencia +100 cada 30 min… todas las kills pvp dan
  siempre 1000".
- **Curva de NPCs:** geométrica de +1 (T1) a +1.000 (T20), unas ×1,44 por tier.
- Las PR #6 y #7 iban apiladas; hubo que explicar a Igor cómo fusionarlas en orden.

**1.0.1 (PR #9).**
- **Emojis:** salían como `??` en el chat de Rust y se quitaron. Desde entonces está
  prohibido usarlos en textos para jugadores.
- **Títulos:** Igor pidió títulos "de mucho pelo a poco pelo… con tono de humor calvo
  británico". Correcciones suyas: "Greñas Sucias" (no "Apestosas") y "Entradas
  Incipientes" (no "Preocupantes").
- **Icono del chat:** Igor creó una cuenta de Steam para la isla y todos los mensajes del
  plugin salen con su avatar. Rust solo acepta avatares de cuentas de Steam, no imágenes
  sueltas.

**v1.1 — eventos globales (issue #3, PR #10).** Se propusieron cuatro eventos; Igor: "me
gustan los 4, mételos". Uno al azar **cada hora** (lo pidió así), con duraciones cortas.
Para la cacería eligió anuncio con nombre, sin marca en el mapa.
- **Hora de la calvicie** (30 min): todo da ×2.
- **Lluvia de champú** (20 min): morir resta ×2.
- **Cazar al más peludo** (20 min): +2.000 a quien lo mate, +1.000 si sobrevive; hacen
  falta 2 jugadores.
- **Brote de alopecia** (60 min): heli, Bradley y Chinook ×3.

**1.2.0 (PR #11).** Igor: "esto debería salir en un mensaje más grande en el centro de la
pantalla, y… quiero que cada vez que consigas puntos por matar te suba el contador
visualmente". Resultado: contador fijo con `+X`/`-X` y cartel grande en el centro para
los eventos.

**v1.3.0 — El Calvario (issue #4, PR #12).**
- **Ideas descartadas:** se propuso primero un piloto con objetos disfrazados (maquinilla
  +500, minoxidil −500) y se apuntaron peluca y cera arrojadiza. Todo eso quedó fuera.
- **La idea de Igor:** pasó capturas de objetos raros del juego. Se buscaron sus nombres
  internos en una lista generada con los datos del juego (rostov114/rust-items).
- **La corrección de Igor:** "Tengo dos mil horas de Rust. Y sé de buena tinta que todas
  esas movidas que te he pasado no aparecen en el juego… bleach, duct tape, battery small,
  eso no los he visto nunca, jamás en ningún evento ni nada". Sangre y carbón fuera,
  porque son de Halloween y Navidad. Según Corrosion Hour y Rustafied, las placas, las
  tarjetas y las gemas son fichas de eventos de streamers.
- **Solo los reparte el plugin:**
  - lejía, cinta y pila al romper barriles;
  - placas, gemas y tarjetas al matar NPCs según su tier;
  - placas rojas a todos los que cobran el heli, la Bradley o el Chinook.
- **Carné de Calvo:** una tarjeta de cada uno de los 11 colores, +100 por sello y +10.000
  al completarlo.
- **Cómo se usan:** idea de Igor, una ventana como la de `/s` que detecta lo que llevas
  ("¿qué te parece la propuesta?"). El nombre "El Calvario" le encantó.
- **Valores desde GitHub:** Igor quiso que se ajustaran aquí ("quiero que el mod sea
  ajustado ya directamente desde aquí"). Pidió a Jano un **resumen del servidor**
  (plugins, economía, choques) sin credenciales.
- **Decisiones con ese resumen:**
  - los NPCs que spawnea el bot a petición de un admin: "Nada, confío en los admins";
  - los NPCs de RaidableBases cuentan como heavies (T14) tal cual;
  - las placas azules se bajaron al 30 %, porque una casa Pesadilla trae 25 heavies.
- **`/calvos`:** pasó a ser el único comando de jugador, con ventana de Objetos y Ranking
  y una X para cerrar (Igor la pidió). `/calvo` desapareció.
- **Muerte:** pasó a **−10 % de la calvicie actual**, redondeado hacia arriba (−20 % con la
  Lluvia de champú). Entró en el alcance del issue #4 junto al Calvario; la transcripción
  no recoge la frase exacta con la que se pidió.
- **Ramas:** Igor preguntó si borrar las ramas era necesario. Se le explicó que borrarlas
  no borra el trabajo.
- **Etiquetas de versión:** GitHub no deja a este Claude crearlas (error 403), así que las
  releases las crea Igor.

**1.3.1 (PR #13).** Igor: el contador chocaba con el panel de RaidableBases, "tienes que
poner lo de los calvos arriba a la derecha". Del menú: "está de putísima madre", pero con
más humor calvo. Se rehízo con estilo de barbería (postes, refranes).

**1.3.2 (PR #14).** El primer fallo de compilación real, detectado por Igor en el servidor:
`ConsoleSystem.Arg.FullString` es un `StringView`, no un `string`. Se arregló con
`.ToString()` y quedó como regla en `CLAUDE.md`.

**1.4.0 — Economía (issue #8, PR #15).** Jano avisó: "Tu mod no da ni RP ni monedas". Era a
propósito, estaba pendiente de Igor. Se propuso RP por título; Igor: "dale".
- **Paga:** RP de Server Rewards cada 30 min vivo, conectado y **moviéndose** (anti-AFK):
  Coronilla 1, Caballero 3, Lord 10, Majestad 30.
- **PR #16:** puso `CLAUDE.md` al día. El primer intento de editarlo lo bloqueó el filtro
  de seguridad; entró tras el "dale" de Igor.

### Día 2 — 25/09/2026

**1.4.1 (PR #17).** Igor pasó un txt con textos reescritos por Jano con "el tono de la
isla" ("vamos a iterar sobre los textos"). Entraron los 28 textos nuevos.

**1.5.0 — La peluquería (issue #5, PR #18).** Igor: "Propongo que sea una tienda donde
puedas acceder a las tiendas de /shop /s y /calvos… Se le añade un tp a /peluqueria como
/bandit o /outpost. /calvos se queda con solo el ranking… Y /S y /shop desaparecen… En
realidad serían tres casas pequeñas con 3 npc".
- **Barbero:** eligió NPC de HumanNPC, aunque no lo tenía instalado ("No, lo instalo").
- **Objetos:** se usan **solo en la peluquería**.
- **Estilo:** para la ventana pidió "como la tienda del puerto pesquero, un dependiente al
  que hablas".
- **Tres NPC:** Mercalvona (GUIShop, monedas), El Calvario (este plugin) y Premios Calvos
  (Server Rewards, RP; luego renombrado Cambio de divisas).
- **Textos:** Igor revisó los de los NPC antes de meterlos ("Dame todo el texto… que lo voy
  a revisar").
- **Qué hace el plugin:** solo escucha el hook `OnUseNPC` de HumanNPC. El TP y las tiendas
  son configuración de NTeleportation, GUIShop y Server Rewards; los pasos están en el
  README.

**1.5.1 (PR #19).** Igor fusionó la #18 justo antes del último push, así que la
conversación del barbero salió en una PR nueva.
- **La conversación:** como los dependientes vanilla: cuadro abajo, frase del barbero y
  respuestas numeradas.
- **Arreglo:** los cadáveres (`wolf.corpse`) salían en el log como NPC sin tier.

**Mensaje de Jano con la 1.5.0 en producción.** El Calvario estaba inutilizable: no había
NPCs creados, `/peluqueria` no existía e Igor no tiene permisos de admin del juego (a
propósito). Hizo 11 preguntas. Respuesta de Igor:
- **Ubicación:** "cada mapa va a ser diferente cuando se genere después de los wipes… yo
  dejaría el plugin preparado para enlazar a Human NPC y luego a la persona que haga el
  Human NPC que se encargue de colocar los NPCs dónde y cómo".
- **Ropa:** "eso todo va aparte".
- **Plan B:** Jano lo pidió (abrir el Calvario desde `/calvos` si no hay barbero). Igor lo
  descartó: "no me gusta, quiero que esté todo los tres NPCs". En su lugar, un aviso en
  consola si no hay barbero configurado.
- **Para Jano:** se le pasaron los pasos: fichero de datos de HumanNPC, GUIShop sin
  tiendas globales, Server Rewards "solo NPC" y `/peluqueria` como comando dinámico de
  NTeleportation con los límites de `/outpost`.

**1.6.0 (PR #20).** Encargo de Igor a través de Jano:
- **Cartel grande** para todos cuando alguien sube de título.
- **Premios por título:** RP, monedas u objetos, una vez por jugador y título, **todo a 0**
  hasta que Igor decida. La alopecia comprada no cobra premios.
- **RP lineal:** `floor(alopecia/100)` cada 30 min, sin tope.
- **Títulos ×10:** 1, 1.000, 10.000… 100.000.000.
- **Cambio de calvicie en el barbero:**
  - tasas asimétricas: vender 100 → 1 RP o 10 monedas; comprar 1 → 1 RP o 10 monedas;
  - "Igor no quiere compras por un clic sin querer": confirmación antes de cada cambio;
  - Economics pasa a ser la segunda dependencia blanda.
- **Textos:** los nuevos los revisó Jano ("los paso bajando").
- **Cuándo se prueba:** Igor: "No podré probar todo esto hasta el lunes, pero lo vamos
  metiendo".

### Día 3 — 26/09/2026

**Alopecia (PR #21, de la 1.6.1 a la 1.6.5).**
- **1.6.1:** "No es calvicie, es alopecia. En la ventanita que sale arriba a la derecha…".
  Hubo un malentendido con "nivel de alopecia" ("no, no, no… es alopecia a secas"). El
  contador pone `ALOPECIA`.
- **1.6.2:** el evento "Alopecia" chocaba con la cifra y se renombró **Tormenta de
  cuchillas**. Igor recordó la regla: textos cambiados, claves de lang nuevas (V2/V3). Si
  no, Oxide conserva los viejos en el servidor.
- **1.6.3:** Jano pasó la regla de Igor: la cifra se llama **alopecia**, y "calvicie" o
  "calva" solo en chistes y nombres de eventos. Se cambiaron 17 textos.
- **Tono (Igor, vía Jano):**
  - sin negritas;
  - le chirría "ser calvo es gloria";
  - nada de chistes que expliquen el cambio de nombre;
  - las tiendas se llaman **el Mercalvona®** (masculino) y **Cambio de divisas**, con el
    **Cambista de divisas**;
  - Economics empieza en 0 monedas y los saldos no se borran en el wipe.
- **Resumen para Obsidian:** Igor pidió un resumen de todo el mod en texto para copiar;
  se le dio en el chat.
- **1.6.4:** entra `docs/TONO.md`, la guía de tono de Igor y Jano. El lema pasa a "el pelo
  está sobrevalorado; el futuro es calvo"; Igor corrigió "…es de los calvos" por "…es
  calvo". Se repasaron los textos: fuera "la gloria", las muletillas y los chistes en
  botones, y la cacería pasa a **Cacería del peludo**. **Error de este Claude:** se podaron
  remates buenos.
- **1.6.5:** colores de la casa (dorado `#e0a526`, óxido `#e0662f`, gris `#9a9288`). Igor:
  "Los colores ok cámbialos".

**1.6.6 (PR #22).**
- **TONO.md afinado:** "repasar no es podar", y una coletilla repetida se cambia, no se
  borra el remate.
- **Remates recuperados:** tieso, Fórmula 1, figura, y la calvicie suprema larga.
- **Brote de alopecia:** vuelve el nombre, porque a Igor le gusta más y como nombre de
  evento no choca con la cifra.
- **La gloria sigue vetada.**

### Día 4 — 27/09/2026

**1.6.7 — números grandes (PR #23).** Igor (con un análisis que le pasaron): "Si la
alopecia se guarda como int de 32 bits… el jugador más calvo del server pasa a ser el más
peludo de golpe… crecimiento compuesto duplicando cada ~35 horas de juego activo".
- **Revisión:**
  - la alopecia ya era `long` (64 bits);
  - el peligro real estaba en **Server Rewards**, que guarda los RP en `int` y suma sin
    comprobar.
- **Pregunta de Igor:** "¿y cambiar el server a 64 bit es posible?". No sirve: el servidor
  ya es de 64 bits; el límite es el tipo de dato del plugin.
- **Pantalla:** Igor pidió notación compacta. "Lo de mil millones y mil billones queda
  sucio. Simplemente millones, billones y trillones. M, B y T". Recordó la escala española:
  billón = 10¹², trillón = 10¹⁸.
- **Todo en long:** "modificamos la matemática de los plugins para que vayan todos en long.
  Con cuidado, inténtalo. Si ves que no puedes, revertimos".
- **Resultado:**
  - sumas que se saturan en vez de desbordar;
  - M/B/T desde mil millones en los sitios estrechos;
  - freno para no pasar el tope de 32 bits de Server Rewards;
  - uso de una API `long` si Server Rewards la trae.
- **Parche a Server Rewards:** Jano lo parcheó a 64 bits en el servidor ese mismo día, con
  las firmas que usa el plugin. `CheckPoints` sigue en `int` porque GUIShop hace un cast a
  `int`. El comando de admin es `rp`, no `sr`.

**Dudas de GitHub.**
- **¿Es público?** Igor preguntó si el repo es público y si hay contadores de visitas. Es
  público, e Insights → Traffic enseña visitas de 14 días solo al dueño.
- **Visibilidad:** Igor: "déjalo público".
- **Vocabulario:** preguntó qué es una PR y cómo se dice en español ("solicitud de
  incorporación de cambios"; literalmente "petición de tirar").

**1.6.8 (PR #24).** Encargo de Jano con tres fallos que encontró revisando el código:
- **Supervivencia AFK:** el +100 solo pedía estar vivo; ahora pide haberse movido, como la
  paga de RP.
- **Objetos malditos sin marca:** el barbero aceptaba cualquier objeto de ese tipo. Jano
  dice que pila y cinta salen en el loot y las placas de los científicos; ver §6. Ahora el
  mod marca lo que reparte con un **skin propio** y el barbero solo acepta los marcados.
  Igor preguntó qué era eso ("¿No habíamos colocado los skins de objetos que no se
  usan?"). Se le explicó que es un sello interno, invisible, sobre los mismos objetos de
  siempre.
- **Parche de Server Rewards:** si falta, la consola avisa en español una vez por
  detección, y los centinelas del servidor lo reenvían a Telegram.
- **Frases escritas por Igor**, que entraron también en la 1.6.8:
  - "Ya no tienes que preocuparte por el champú." (calvicie suprema)
  - "Paquetes." (el peludo sobrevive)
  - "Gracias por este gran servicio a la comunidad." (lo cazan)
  - "Ha escampado. Ya podéis palmar tranquilos."
  - "Has sido muy valiente, enhorabuena." (matar a uno dormido)
  - "rapado por headshot"
- **TONO.md:** nueva regla 7 ("mejor una palabra que un párrafo") y dos vetos: pasar de
  vacilar a despreciar, y subir el tono a base de tacos o running gags.
- **Issue #8:** cerrado, porque la economía se resolvió con RP.
- **Servidores renombrados:** el modded es el principal, **"[ES] Isla de Calvos"**; el
  vanilla es **"[ES] Isla de Calvos - Vanilla"**.

---

## 2. Ideas y su estado

| Idea | Estado | Notas |
|---|---|---|
| Calvicie en % (0-100) | Descartada | Pasó a puntos enteros sin límite el día 1 |
| Economía: monedas por hora según el nivel (idea original de Igor) | Hecha de otra forma | Con RP de Server Rewards (1.4.0), lineal desde la 1.6.0 |
| Nombre de la escala ("poder calvo", "más de 9000"…) | Hecha | La cifra se llama **alopecia** (1.6.1) |
| Títulos Aspirante…Dios Calvo | Descartada | Sustituidos por los de humor británico (1.0.1) |
| Cuatro eventos globales | Hecha | 1.1; nombres ajustados después |
| Marca en el mapa para la cacería | Descartada | Igor eligió solo el anuncio |
| Maquinilla, minoxidil, peluca, cera arrojadiza | Descartadas | Sustituidas por los objetos raros que propuso Igor |
| Sangre y carbón como objetos | Descartadas | Son de eventos de Halloween y Navidad |
| Carbón "maldito pasivo" (ganas la mitad si lo llevas) | Descartada | Fuera con el carbón |
| Objetos malditos + Carné de Calvo | Hecha | 1.3.0; en el barbero desde la 1.5.0 |
| Marca propia (skin) en los objetos malditos | Hecha | 1.6.8 |
| Ignorar los NPCs que spawnea el bot a petición de admins | Descartada | "Nada, confío en los admins" |
| Que los NPCs de RaidableBases den menos | Descartada | Se quedan como heavies (T14) |
| Peluquería con 3 NPC de HumanNPC y `/peluqueria` | Hecha | 1.5.0-1.5.1; montada en el servidor por Jano |
| Máquina expendedora vanilla o ventana nativa para el Calvario | Descartadas | Igor eligió dependiente con conversación |
| Plan B: Calvario sin barbero desde `/calvos` | Descartada | "Quiero que esté todo los tres NPCs" |
| Que el plugin cree los NPCs, ropa o ubicación de la peluquería | Descartada | Cosa de quien monta el servidor tras cada wipe |
| Cartel al subir de título | Hecha | 1.6.0 |
| Premios por título (RP, monedas, objetos) | Hecha, **sin valores** | Todo a 0 hasta que Igor decida |
| Cambio de alopecia ↔ RP ↔ monedas | Hecha | 1.6.0, tasas asimétricas y confirmación |
| RP lineal sin tope | Hecha | 1.6.0 |
| Contador "NIVEL DE ALOPECIA" | Descartada | Igor: "alopecia a secas" |
| Evento "Tormenta de cuchillas" | Revertida | 1.6.2 → vuelve "Brote de alopecia" en la 1.6.6 |
| Remates podados en la 1.6.4 | Revertida | Recuperados en la 1.6.6 |
| Notación exponencial para cifras grandes | Descartada | M, B y T a la española (1.6.7) |
| "Mil M", "mil B" | Descartada | "Queda sucio" |
| Server Rewards a 64 bits | Hecha | Parche de Jano en el servidor (27/09), fuera del repo |
| Pasar el servidor a 64 bits | Descartada | Ya lo es; no era el problema |
| Frenar el crecimiento compuesto (comprar alopecia con los RP que da) | **Pendiente** | Decisión de diseño de Igor (ver §3) |
| Repo privado | Descartada | "Déjalo público" |
| Etiquetas y releases automáticas | No posible | Este Claude no puede crear tags (403); las crea Igor |
| Evento en marcha que se pierde al recargar el plugin | **En duda** | Limitación conocida desde la 1.1, sin arreglar |
| Que los objetos marcados se vean bien en el inventario | **Pendiente de probar** | Con un skin inexistente debería salir el icono normal |

---

## 3. Decisiones de diseño y su porqué

- **Puntos enteros sin límite:** los quería Igor para usar la cifra como "niveles" que
  dan dinero. Con un porcentaje, el heli dando 1.000 habría llevado a todos arriba en un
  momento.
- **Kill +1.000 y supervivencia +100:** los fijó Igor. Una kill vale lo mismo que un heli.
  Media hora viva vale lo que un heavy.
- **NPCs por tiers:** el servidor tiene 1-3 jugadores; sin NPCs no habría progreso.
- **Muerte: −10 % de lo que tienes** (desde la 1.3.0):
  - un castigo fijo (−1.000) no significaba nada para quien tiene millones;
  - con porcentaje, morir duele igual en todos los niveles y nunca te deja en negativo;
  - redondeado hacia arriba: si tienes algo, morir siempre cuesta al menos 1;
  - toda muerte resta, también dormido o por caída, por decisión de Igor.
- **Títulos ×10 y RP lineal** (1.6.0):
  - un título por cada cero, de 1 a 100.000.000;
  - la paga es 1 RP por cada 100 de alopecia, así que el título ya no marca la paga: la
    marca la cifra.
- **Cambio de alopecia asimétrico:** vender 100 → 1 RP y comprar 1 → 1 RP. La alopecia paga
  RP cada 30 min para siempre; si comprarla fuera barato, sería "una máquina de hacer
  dinero". La compra no cobra premios de título ni se multiplica con eventos.
- **El interés compuesto** (analizado el 27/09):
  - quien compra alopecia con todos sus RP gana un 1 % cada media hora, porque cobra
    alopecia/100 RP y cada RP compra 1 de alopecia;
  - eso **duplica cada ~70 pagos, unas 35 horas activas**, y la cuenta de Igor es correcta;
  - lo frena en la práctica que cada compra da como mucho 100.000 (harían falta muchos
    clics) y que hay que estar moviéndose;
  - si Igor quiere cortarlo, las opciones son encarecer la compra, un límite de compra al
    día o una paga de RP no lineal por arriba. **Sin decidir.**
- **Alopecia y no calvicie:** es el nombre oficial de la cifra, por decisión de Igor.
  "Calvicie" solo en chistes y nombres de eventos ("Hora de la calvicie").
- **Claves de lang nuevas** para cada texto cambiado: Oxide no pisa un texto que ya existe
  en el servidor. Sufijos V2, V3…
- **Un plugin, un fichero:** Oxide carga cada `.cs` como un plugin independiente;
  verificado en su código.
- **Dependencias blandas** (Server Rewards y Economics): si no están, el plugin funciona
  sin RP o sin monedas. HumanNPC solo aporta su hook.
- **Peluquería solo con NPCs:** da sentido al sitio (`/shop` y `/s` solo en sus NPC) y
  encaja con cómo funcionan GUIShop y Server Rewards.
- **Supervivencia y RP anti-AFK:** mismo criterio, haber movido al menos 1 m entre dos
  comprobaciones de un minuto.
- **Números grandes:**
  - la alopecia en `long` con sumas saturadas;
  - Server Rewards parcheado a `long`, con la API vieja en `int` para no romper GUIShop;
  - el plugin detecta el parche y, sin él, frena en 2.147.483.647 y avisa.
- **M, B y T a la española:** millón = 10⁶, billón = 10¹², trillón = 10¹⁸. Solo desde
  mil millones y solo donde no cabe; en el chat, el número entero.

---

## 4. Problemas encontrados y cómo se resolvieron

| Problema | Solución |
|---|---|
| No se puede compilar contra las DLL reales en el entorno cloud | Stubs y un arnés de pruebas fuera del repo; la prueba real es el servidor |
| Las listas de la config crecían en cada recarga (7 → 14 → 21 títulos) | `ObjectCreationHandling.Replace` en todas las colecciones (v1.0) |
| Los emojis salían como `??` en el chat | Fuera emojis; resaltar con `<color>` (1.0.1) |
| PR #7 apilada sobre la #6 | Fusionar en orden; se le explicó a Igor |
| El contador chocaba con los paneles de RaidableBases | Arriba a la derecha (1.3.1), con migración de config |
| `arg.FullString` es `StringView` en el Rust real y no compilaba | `.ToString()` (1.3.2); regla en `CLAUDE.md` |
| Crear etiquetas de versión da 403 | Las releases las crea Igor desde la web |
| El filtro bloqueó una edición de `CLAUDE.md` | Se hizo tras el permiso explícito de Igor |
| Claves de lang de Server Rewards v1 que no existen en la 2.x | Documentado; se usan las de la 2.x |
| PR #18 fusionada antes del último push | Lo que faltaba salió en la PR #19 (1.5.1) |
| Un `.cs` subido con la ruta de Windows como nombre | Aviso de Jano; cuidado al subir |
| El Calvario inutilizable con la 1.5.0 sin NPCs creados | Jano montó la peluquería; aviso en consola si falta el barbero |
| `wolf.corpse` salía como NPC sin tier | Se ignoran los `*.corpse` (1.5.1) |
| Oxide conserva los textos viejos del servidor | Claves nuevas en cada cambio de texto |
| "Nivel de alopecia": malentendido | Corregido: "alopecia" a secas (1.6.1) |
| Remates buenos podados al aplicar la guía de tono | TONO.md afinado y remates recuperados (1.6.6) |
| RP en `int` en Server Rewards: saldo negativo pasado 2.147 millones | Freno en el plugin, parche a `long` en el servidor y aviso si falta (1.6.7 y 1.6.8) |
| Cifras demasiado largas para el contador | M, B y T desde mil millones (1.6.7) |
| Farmeo AFK de la supervivencia | Pide movimiento (1.6.8) |
| El barbero aceptaba objetos de cualquier origen | Skin propio; solo cuentan los marcados (1.6.8) |
| Número de PR equivocado en el comentario que cerró el issue #8 | Corregido (la 1.4.0 es la PR #15) |

---

## 5. Versiones

- **0.1.0** (PR #1): esqueleto del proyecto, documentación y reglas.
- **1.0.0** (PR #6 y #7):
  - calvicie, títulos, ranking, anti-farmeo y comandos;
  - NPCs por tiers y recompensa compartida;
  - puntos enteros sin límite.
- **1.0.1** (PR #9): títulos de humor calvo británico, icono de la isla en el chat, fuera
  emojis.
- **1.1.0** (PR #10): cuatro eventos globales, uno al azar cada hora.
- **1.2.0** (PR #11): contador en pantalla con `+X`/`-X` y cartel central para los eventos.
- **1.3.0** (PR #12): El Calvario (objetos malditos y Carné de Calvo), `/calvos` como único
  comando, muerte −10 %.
- **1.3.1** (PR #13): ventana con estilo de barbería y contador arriba a la derecha.
- **1.3.2** (PR #14): `arg.FullString.ToString()` para que compile en el servidor real.
- **1.4.0** (PR #15): la calvicie paga RP de Server Rewards por título (y PR #16, docs).
- **1.4.1** (PR #17): textos con el tono de la isla (28 nuevos).
- **1.5.0** (PR #18): la peluquería (Calvario en un NPC de HumanNPC, `/calvos` solo
  ranking).
- **1.5.1** (PR #19): conversación con el barbero, arreglo de cadáveres, aviso si falta
  barbero.
- **1.6.0** (PR #20): cartel y premios por título, RP lineal, títulos ×10, cambio de
  calvicie.
- **1.6.1–1.6.5** (PR #21):
  - contador `ALOPECIA`;
  - Tormenta de cuchillas;
  - textos en "alopecia";
  - TONO.md y repaso de textos;
  - colores de la casa.
- **1.6.6** (PR #22): TONO.md afinado, vuelve el Brote de alopecia y los remates.
- **1.6.7** (PR #23): números grandes (M/B/T, sin desbordes, RP en 64 bits si Server
  Rewards lo permite).
- **1.6.8** (PR #24):
  - supervivencia anti-AFK;
  - objetos malditos marcados;
  - aviso si falta el parche de Server Rewards;
  - frases de Igor y TONO.md con la regla 7.

---

## 6. Lo que veo pendiente o arriesgado

1. **Probar en el juego lo que no se ha podido ver:**
   - la ventana del barbero de verdad;
   - que los objetos con el skin propio se vean bien;
   - comprar en el Cambio de divisas y que el Mercalvona abra;
   - que llegue la paga de RP.

   Igor lo tenía previsto para el lunes.
2. **El parche de Server Rewards es frágil:** cualquier actualización del plugin lo pisa.
   La 1.6.8 avisa en consola, y Jano tiene que volver a aplicarlo.
3. **El crecimiento compuesto** (comprar alopecia con los RP que da) sigue abierto. No
   rompe nada técnico, pero puede disparar la economía en meses. Lo decide Igor.
4. **Premios por título a 0:** están programados pero sin valores.
5. **¿Salen o no esos objetos en el Rust normal?** Hay versiones distintas:
   - Igor, por su experiencia: lejía, cinta y pila no salen;
   - la investigación del día 1 (Corrosion Hour, Rustafied): placas, tarjetas y gemas son
     de eventos de streamers;
   - Jano (27/09): pila y cinta salen en el loot, y las placas de los científicos.

   Con la marca de la 1.6.8 da igual quién tenga razón, pero conviene aclararlo para el
   menú `/info`.
6. **Un evento en marcha se pierde** si se recarga el plugin o se reinicia el servidor,
   sin anuncio de fin.
7. **NPCs spawneados por admins** siguen dando alopecia y objetos. Se decidió confiar en
   los admins.
8. **Todo se ha probado solo contra imitaciones.** Cada versión hay que cargarla y mirar
   la consola (`Loaded plugin Isla de Calvos vX`).
9. **El repo es público.** Hasta ahora no hay secretos en él. Hay que seguir sin meter
   credenciales, IPs ni SteamIDs de jugadores en issues, PRs o documentos.
