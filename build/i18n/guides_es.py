# -*- coding: utf-8 -*-
"""Uso de las plantillas, español."""

LANG = 'es'

G = {
'para': r'''# Cómo usar PARA

> Todo va a una de cuatro cajas según "¿cuándo termina?". Con solo cuatro cajas, decidir dónde va cada cosa lleva muy poco tiempo.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **1_Projects** | Trabajo con plazo o fecha de fin. P. ej. la mudanza, el informe del tercer trimestre, renovar el pasaporte | Lo que nunca termina (eso es Areas) |
| **2_Areas** | Responsabilidades continuas que no terminan. P. ej. salud, finanzas, coche, dirigir un equipo | Tareas de una sola vez |
| **3_Resources** | Material de referencia por tema. P. ej. recetas, referencias de diseño, manuales | Documentos de cosas de las que eres responsable |
| **4_Archives** | Lo terminado o que ya no se usa. Carpetas traídas de las otras tres | Material que usas ahora |

¿Dudas sobre dónde va algo? Pregúntate en este orden:

1. **¿Cuándo termina?** Si tiene fecha → 1_Projects
2. **¿Me sigo ocupando de ello?** Si hay que gestionarlo siempre → 2_Areas
3. **¿Es solo referencia?** → 3_Resources
4. Nada de lo anterior y ya no se usa → 4_Archives

## Árbol de ejemplo

```
Documentos/
├─ 1_Projects/
│  ├─ 2026-11_Mudanza/
│  └─ 2026-12_Declaración anual/
├─ 2_Areas/
│  ├─ Salud/
│  └─ Finanzas/
├─ 3_Resources/
│  └─ Cocina/
└─ 4_Archives/
   └─ 2026-03_Renovación pasaporte/
```

## Reglas de nombres

- Patrón: `Fecha_Tema_Contenido[_vVersión].ext`
- P. ej. `2026-11-18_Mudanza_ComparativaPresupuestos_v02.xlsx`, `2026-03-02_Chequeo_Resultados.pdf`
- Nombra las carpetas de proyecto `AñoMesInicio_Nombre` (2026-11_Mudanza) para que se ordenen por fecha de inicio.
- Versiones de dos cifras, como `_v01`, `_v02`. Evita "final" y "final de verdad".

## Para mantenerlo en orden

- Cuando termina un proyecto, mueve su carpeta **entera** a 4_Archives. No la borres.
- Una vez al mes, repasa 1_Projects y pasa lo terminado al archivo.
- 2_Areas cambia poco. Si tienes más de 10 áreas, comprueba si las has dividido demasiado.
- Las subcarpetas se encuentran mejor si no pasan de 2–3 niveles.

## Cuándo encaja mejor otro estilo

- Mucho material al que quieres llegar directamente por número → **Johnny.Decimal**
- Trabajo freelance o comercial dividido por cliente → **Trabajo por cliente y proyecto**
- Sobre todo fotos y vídeos → **Archivo de fotos y vídeos**
''',
'johnny-decimal': r'''# Cómo usar Johnny.Decimal

> Cada lugar lleva un número. Dices "22.04" y su ubicación queda clara. Ideal si tienes mucho material y vuelves a buscar a menudo lo mismo.

## Tres niveles

| Nivel | Aspecto | Significado | Límite |
|---|---|---|---|
| **Área** | `10-19`, `20-29` | Un gran tema. Cada decena es un área | hasta 10 |
| **Categoría** | `11`, `12`, `21` | Una caja dentro de un área, numerada en su rango | hasta 10 por área |
| **ID** | `11.01`, `22.04` | El lugar real donde va el material | 01–99 por categoría |

## Qué va dentro / qué no

- Las **carpetas de área** solo contienen carpetas de categoría. No pongas archivos directamente.
- Las **carpetas de categoría** contienen carpetas de ID (`11.01 Nombre`) o archivos con número de ID.
- **00-09 Sistema** solo guarda la lista de números (el índice) y notas sobre el propio sistema.
- **Nunca más de tres niveles.** Si quieres dividir más, toma un nuevo número de ID.

## Mantener la lista de números

La lista de números (`00.00 Lista de números.md`) es el corazón del método. Escribe una línea cada vez que crees un número nuevo y sabrás qué hay dónde sin abrir carpetas, y nunca usarás dos veces el mismo número.

## Árbol de ejemplo

```
00-09 Sistema/
└─ 00 Índice/
   └─ 00.00 Lista de números.md
10-19 Vida/
├─ 11 Hogar/
│  └─ 11.01 Contrato de alquiler/
└─ 12 Coche/
20-29 Dinero/
├─ 22 Impuestos/
│  └─ 22.04 2026 Declaración anual/
└─ 23 Seguros/
```

## Reglas de nombres

- Patrón: `Número Contenido.ext` — el número es la ubicación.
- P. ej. `11.01 Póliza seguro coche.pdf`, `22.04 2026 Declaración anual.xlsx`
- Un espacio entre número y nombre. Los números siempre con dos cifras (`11.01`, `11.10`).

## Para mantenerlo en orden

- No llenes todas las cajas al principio; empieza con 3–5 áreas y unas pocas categorías.
- Si una categoría va a pasar de 10, es señal de que toca dividir el área.
- Nunca reutilices números. Mueve los ID que no uses a una categoría de archivo (p. ej. `39 Archivo`).

## Cuándo encaja mejor otro estilo

- Gestionar números te resulta pesado y solo quieres dividir por "¿está terminado?" → **PARA**
- Fotos y vídeos donde importa el orden por fecha → **Archivo de fotos y vídeos**
''',
'number-prefix': r'''# Cómo usar Números y fechas

> La forma más fácil de ordenar. Los números delante de las carpetas mantienen su orden, y si los nombres de archivo empiezan por la fecha, el orden por nombre es el orden por fecha.

| Carpeta | Qué va dentro |
|---|---|
| **00_Bandeja de entrada** | Aquí primero: archivos recibidos y lo que aún no sabes dónde poner (está arriba, así que siempre la ves) |
| **01_En curso** | En lo que trabajas ahora |
| **02_Material** | Material de referencia que consultas a menudo |
| **03_Hecho** | Terminado, pero lo consultas a veces |
| **99_Archivo** | Antiguo y casi nunca consultado (99, así que siempre al final) |

## Cómo usar la bandeja de entrada

1. Mete los archivos nuevos en **00_Bandeja de entrada** sin pensarlo.
2. Una vez por semana, vacía 00_Bandeja de entrada repartiéndolo en 01–03.
3. ¡Cuando está vacía, has terminado! La clave es que no se acumule.

## Nombres de archivo

Nombra los archivos **Fecha_Contenido**, como `2026-11-19_Informe.docx`, y se ordenan por fecha con solo ordenarlos por nombre.

## Cuándo encaja mejor otro estilo

- Quieres separar el trabajo en curso de las responsabilidades continuas → **PARA**
- Tantas carpetas que quieres ir directo a ellas por número → **Johnny.Decimal**
- Material donde la fecha lo es todo, como fotos y escaneos → **Archivo por fechas**
''',
'date-archive': r'''# Cómo usar Archivo por fechas

> Sin decidir dónde va nada. Solo deja cada archivo en la carpeta año-mes de la fecha en que se creó.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Año (2026)** | Las carpetas año-mes de ese año | Ningún archivo directamente |
| **Año-mes (2026-11)** | Archivos creados o recibidos ese mes | Archivos de trabajo que sigues editando mucho tiempo |

Va mejor con registros que rara vez vuelves a mirar (recibos, facturas, capturas, archivos recibidos) que con material que buscas por tema.

## Árbol de ejemplo

```
Archivo/
└─ 2026/
   ├─ 2026-10/
   └─ 2026-11/
      ├─ 2026-11-03_Acta_reunión.md
      └─ 2026-11-18_Factura_luz.pdf
```

## Reglas de nombres

Nombra las cosas Fecha_Contenido. Así, ordenar por nombre es ordenar por tiempo.

| Qué | Regla | Ejemplo |
|---|---|---|
| Archivo | AAAA-MM-DD_Contenido | `2026-11-18_Factura_luz.pdf` |
| Carpeta | AAAA-MM | `2026-11` |

## Para mantenerlo en orden

- Al cambiar de mes, crea una carpeta año-mes nueva. Volver a usar esta plantilla en el mismo archivo crea solo las carpetas que faltan.
- Usa siempre la fecha en que la cosa "se creó". Mezclar fecha de recepción y de redacción confunde.
- También puedes mover a esta estructura los archivos ya acumulados con el estilo "Por fecha" del orden de carpetas.

## Cuándo encaja mejor otro estilo

- Necesitas buscar por proyecto o tema → **PARA** o **Johnny.Decimal**
- Fotos y vídeos → **Archivo de fotos y vídeos** (Fecha_Evento y una carpeta RAW aparte)
''',
'client-project': r'''# Cómo usar Trabajo por cliente y proyecto

> Los proyectos van dentro de la carpeta de cada cliente por orden de inicio, y dentro, carpetas numeradas siguen el orden del trabajo (contrato → material → trabajo → entregables).

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Nombre del cliente/** | Las carpetas de proyecto de ese cliente | Material que mezcla varios clientes |
| **Fecha_Proyecto/** | Todo lo de un proyecto. La fecha es la de inicio | — |
| **01_Contrato** | Contratos, presupuestos, pedidos, NDA, facturas | Borradores en curso |
| **02_Material** | Material original del cliente (nunca se edita) | Archivos que has editado |
| **03_Trabajo** | Borradores, archivos de trabajo, notas de reuniones | Versiones finales enviadas |
| **04_Entregables** | Solo las versiones finales enviadas al cliente | Borradores e intermedias |
| **99_Archivo** | Lo que se guarda al terminar, lo que ya no se usa | — |

## Árbol de ejemplo

```
ACME/
└─ 2026-11-18_Rediseño web/
   ├─ 01_Contrato/
   │  └─ 2026-11-18_ACME_Contrato_Firmado.pdf
   ├─ 02_Material/
   ├─ 03_Trabajo/
   ├─ 04_Entregables/
   │  └─ 2026-12-20_ACME_PropuestaDiseño_v03.pdf
   └─ 99_Archivo/
```

## Reglas de nombres

- Patrón: `Fecha_Cliente_TipoDoc_vVersión.ext`
- P. ej. `2026-11-18_ACME_Propuesta_v03.pptx`, `2026-12-01_ACME_Contrato_Firmado.pdf`
- En lugar de añadir `_final`, usa la **fecha**. La fecha más reciente es la última versión.
- Las versiones empiezan en `_v01`, con dos cifras.

## Para mantenerlo en orden

- Guarda las versiones finales **solo en 04_Entregables**. No tengas copias del mismo archivo en otros sitios.
- No edites los originales de 02_Material; cópialos a 03_Trabajo y edítalos allí.
- Al terminar un proyecto, déjalo en la carpeta del cliente o mueve solo lo necesario a 99_Archivo.
- Escribe cada cliente de una sola forma (no mezcles "ACME" y "Acme S.A.").

## Cuándo encaja mejor otro estilo

- Asuntos personales y de trabajo mezclados, sin división por cliente → **PARA**
- Un proyecto que desarrolla software → **Documentos de desarrollo de sistemas TI**
''',
'document-workflow': r'''# Cómo usar Etapas del documento

> Las carpetas siguen las etapas por las que pasa un documento. Con solo mirar las carpetas sabes "¿por dónde va?".

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **01_Borrador** | El archivo que escribes y sus versiones | Archivos para enviar |
| **02_Revisión** | Archivos en revisión y comentarios recibidos | Borradores aún en redacción |
| **03_Final** | Una única versión final confirmada | Varias versiones |
| **04_Distribución** | Lo que realmente enviaste (PDF, etc.) y la fecha | Archivos por editar |

## Árbol de ejemplo

```
Plan de negocio/
├─ 01_Borrador/
│  ├─ PlanNegocio_v0.1_Borrador.docx
│  └─ PlanNegocio_v0.2_Borrador.docx
├─ 02_Revisión/
├─ 03_Final/
│  └─ PlanNegocio_v1.0_Final_2026-11-18.docx
├─ 04_Distribución/
│  └─ 2026-11-18_PlanNegocio_Inversor.pdf
└─ README.md
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Archivo de trabajo | NombreDoc_vVersión_Estado | `PlanNegocio_v0.2_Borrador.docx` |
| Versión final | NombreDoc_vVersión_Final_Fecha | `PlanNegocio_v1.0_Final_2026-11-18.docx` |
| Copia distribuida | Fecha_NombreDoc_Destinatario | `2026-11-18_PlanNegocio_Inversor.pdf` |

## Para mantenerlo en orden

- **Copia** los archivos al pasar de etapa. Si borras la etapa anterior, no podrás volver.
- Sube el número de versión en lugar de escribir "final_definitivo".
- Ten siempre un solo archivo en 03_Final.

## Cuándo encaja mejor otro estilo

- Muchos proyectos divididos por cliente → **Trabajo por cliente y proyecto**
- Escribes artículos o informes junto con material y datos → **Proyecto de investigación y estudio**
''',
'household-life': r'''# Cómo usar Documentos del hogar

> Papeles que buscas unas pocas veces al año, en seis cajas. El objetivo: encontrar cualquier cosa en tres clics cuando hay prisa.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Finanzas** | Declaraciones de impuestos, extractos de banco y tarjetas, declaración anual | Cada recibo del día a día |
| **Salud** | Consultas y recetas, resultados de chequeos | — |
| **Hogar** | Contratos y escrituras, suministros y cuotas, manuales de electrodomésticos | — |
| **Vehículo** | Permiso de circulación, mantenimiento, justificante del seguro | — |
| **Pasaporte e identidad** | Copias de pasaportes, DNI y certificados | Los originales físicos |
| **Seguros** | Pólizas, condiciones, reclamaciones | — |

## Árbol de ejemplo

```
Documentos del hogar/
├─ Finanzas/
│  ├─ Impuestos/
│  └─ Banco y tarjetas/
├─ Salud/
│  ├─ Consultas y recetas/
│  └─ Chequeos/
├─ Hogar/
│  ├─ Contratos y escrituras/
│  ├─ Suministros y cuotas/
│  └─ Reparaciones y manuales/
├─ Vehículo/
├─ Pasaporte e identidad/
└─ Seguros/
```

## Reglas de nombres

Fecha_Organismo_Contenido. Usa la fecha que figura en el documento.

| Qué | Regla | Ejemplo |
|---|---|---|
| Documento | Fecha_Organismo_Contenido | `2026-05-31_Hacienda_DeclaraciónRenta.pdf` |

## Para mantenerlo en orden

- Guarda los papeles delicados, como Pasaporte e identidad y Seguros, en una carpeta cifrada o en una copia de seguridad con contraseña. Ten especial cuidado al subirlos a la nube.
- En los papeles con caducidad (pasaporte, seguros), apunta la fecha de caducidad en el nombre del archivo o en tu calendario.
- Retira los papeles que ya no necesites tras cinco años solo después de revisarlos tú. Foldy nunca borra nada.

## Cuándo encaja mejor otro estilo

- Tienes más material de trabajo o estudio que papeles → **PARA**
- Solo quieres acumular por fecha → **Archivo por fechas**
''',
'photo-archive': r'''# Cómo usar Archivo de fotos y vídeos

> Con año → Fecha_Evento, ordenar por nombre es ordenar por tiempo. Los originales viven en un solo sitio y nunca se editan.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Año/** | Las carpetas de eventos de ese año | Fotos de años mezclados |
| **Fecha_Evento/** | Un evento. La fecha es la del primer día | — |
| **RAW** | Los originales tal cual salieron de la cámara o el móvil | Archivos retocados |
| **Editadas** | Archivos retocados, recortados o compuestos | Originales |
| **Exportaciones** | Archivos reducidos o convertidos para compartir o imprimir | Archivos en curso |

## Árbol de ejemplo

```
Fotos/
└─ 2026/
   ├─ 2026-08-02_Vacaciones de verano/
   └─ 2026-11-18_ViajeJeju/
      ├─ RAW/
      │  └─ 2026-11-18_143205_ViajeJeju_001.jpg
      ├─ Editadas/
      │  └─ 2026-11-18_ViajeJeju_Editada_001.jpg
      └─ Exportaciones/
```

## Reglas de nombres

- Patrón: `Fecha_Hora_Evento_Número.ext` (según la hora de la toma)
- P. ej. `2026-11-18_143205_ViajeJeju_001.jpg`, `2026-11-18_ViajeJeju_Editada_001.jpg`
- Los nombres que pone la cámara (IMG_1234) se cambian de una vez con "Nombrar por hora de la toma" en el cambio de nombre por lotes de Foldy.

## Para mantenerlo en orden

- **Nunca edites RAW.** Retoca siempre una copia, en Editadas.
- Con solo RAW respaldado puedes recrear todo lo demás, así que respalda RAW primero.
- Nombres de evento cortos. Las rutas completas largas dan problemas al pasar a otros dispositivos.
- El mismo día que importes, marca las fotos movidas o repetidas para ahorrarte trabajo luego (borra solo tras comprobarlo tú).

## Cuándo encaja mejor otro estilo

- Las fotos forman parte de documentos (fotos de obra, justificantes) → 02_Material en **Trabajo por cliente y proyecto**
- Más documentos que fotos → **PARA**
''',
'design-assets': r'''# Cómo usar Recursos de diseño y creación

> Cuatro cajas que separan lo que no se toca (originales), lo que estás haciendo (trabajo), los resultados (exportaciones) y la inspiración (referencia).

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Originales** | Imágenes, fotos, recursos y fuentes recibidos | Archivos editados |
| **Trabajo** | Archivos de trabajo (psd, ai, fig, proyectos) y sus versiones | Resultados terminados |
| **Exportaciones** | Resultados exportados para entregar o publicar (png, jpg, pdf, mp4) | Archivos en curso |
| **Referencia** | Referencias, moodboards, guías | Tus propios resultados |

## Árbol de ejemplo

```
Logotipo/
├─ Originales/
├─ Trabajo/
│  ├─ Logo_Principal_v2.ai
│  └─ Logo_Principal_v3.ai
├─ Exportaciones/
│  ├─ Logo_Web_512.png
│  └─ Logo_Impresión.pdf
└─ Referencia/
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Archivo de trabajo | Trabajo_Contenido_vVersión | `Logo_Principal_v3.ai` |
| Archivo exportado | Trabajo_Uso_Tamaño | `Logo_Web_512.png` |

## Para mantenerlo en orden

- Nunca edites los originales. Si hay que cambiar uno, cópialo a Trabajo y edítalo allí.
- No sobrescribas los archivos de trabajo; guarda con un número v mayor para poder volver atrás fácilmente.
- En trabajos grandes, respalda primero Originales y Trabajo.

## Cuándo encaja mejor otro estilo

- Solo acumulas fotos y vídeos por fecha → **Archivo de fotos y vídeos**
- Trabajo que entregas a clientes → **Trabajo por cliente y proyecto**
''',
'research-project': r'''# Cómo usar Proyecto de investigación y estudio

> Las cajas siguen el flujo de leer (bibliografía) → reunir (datos) → analizar → escribir (manuscrito) → presentar. Ideal para artículos, trabajos de fin de grado y apuntes de estudio.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Qué no |
|---|---|---|
| **Bibliografía** | PDF de artículos, listas de referencias (.bib), notas de lectura | Tu propio manuscrito |
| **Datos/Originales** | Datos tal como se recibieron o recogieron | Datos modificados |
| **Datos/Depurados** | Datos depurados y procesados | Originales |
| **Análisis** | Código y cuadernos de análisis, tablas y figuras de resultados | Datos originales |
| **Manuscrito** | Manuscritos de artículos e informes y sus versiones | Diapositivas |
| **Presentaciones** | Diapositivas, pósteres | Manuscritos |

## Árbol de ejemplo

```
Investigación de sensores/
├─ Bibliografía/
├─ Datos/
│  ├─ Originales/
│  └─ Depurados/
├─ Análisis/
├─ Manuscrito/
├─ Presentaciones/
└─ README.md
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Artículo | Autor_Año_Título | `Lee_2023_Fusión_Sensores.pdf` |
| Datos | Contenido_Fecha | `Encuesta_2026-11-18.csv` |
| Manuscrito, análisis | Nombre_vVersión | `Manuscrito_v0.3.docx` |

## Para mantenerlo en orden

- Nunca modifiques los datos originales. Procésalos en Depurados y deja cómo lo hiciste como código en Análisis.
- Escribe un resumen de una línea de cada artículo leído en el README o tus notas; luego será más fácil citarlo.

## Cuándo encaja mejor otro estilo

- Solo gestionas el borrador, la revisión y la versión final de un documento → **Etapas del documento**
- El centro es desarrollar un sistema más que investigar → **Documentos de desarrollo de sistemas TI**
''',
'pm-project': r'''# Cómo usar Gestión de proyectos (PMBOK, 5 fases)

> Una forma de ordenar documentos de proyecto en cualquier sector. Las carpetas siguen los cinco grupos de procesos de la Guía del PMBOK del Project Management Institute (PMI): inicio, planificación, ejecución, seguimiento y control, y cierre.

## Qué va en cada carpeta

| Carpeta | Qué va dentro |
|---|---|
| **01_Inicio** | Acta de constitución, registro de interesados, aprobación para empezar |
| **02_Planificación** | Planes de alcance, cronograma (EDT), presupuesto, calidad, riesgos y comunicación |
| **03_Ejecución** | Entregables, actas de reuniones, material de trabajo |
| **04_Seguimiento y control** | Informes de avance semanales y mensuales, solicitudes de cambio, registros de incidencias y riesgos |
| **05_Cierre** | Informe final, acta de aceptación, lecciones aprendidas (retrospectiva), cierre del contrato |
| **99_Archivo** | Versiones antiguas, lo que ya no se usa |

## Para mantenerlo en orden

- El seguimiento y control va en paralelo a la ejecución. Pon informes y solicitudes de cambio en 04 según lleguen.
- Dejar las lecciones aprendidas en 05_Cierre al final facilita el siguiente proyecto.

## Cuándo encaja mejor otro estilo

- Un proyecto que desarrolla software → **Documentos de desarrollo de sistemas TI**
- Construcción y diseño, donde varios equipos intercambian planos y modelos → **Información de construcción y diseño**
- Basta con separar contratos y entregables por cliente → **Trabajo por cliente y proyecto**
''',
'it-project': r'''# Cómo usar Documentos de desarrollo de sistemas TI (ISO/IEC/IEEE 12207)

> Los **documentos** se dividen en el orden de los procesos técnicos de ISO/IEC/IEEE 12207, la norma internacional del ciclo de vida del software (requisitos → análisis → diseño → implementación → pruebas → transición → operación y mantenimiento). El código fuente se queda en un repositorio Git; esta carpeta solo anota dónde está.

## Qué va en cada carpeta

| Carpeta | Qué va dentro | Proceso de la norma |
|---|---|---|
| **01_Requisitos** | Solicitudes recibidas, requisitos de las partes interesadas, especificación de requisitos | Definición de requisitos de interesados y de sistema/software |
| **02_Análisis** | Análisis de situación y del negocio, análisis del sistema, estudio de viabilidad | Análisis del negocio o de la misión, análisis del sistema |
| **03_Diseño** | Arquitectura, diseño detallado, diseño de pantallas y datos | Definición de arquitectura, definición de diseño |
| **04_Implementación** | Notas de implementación, configuración de compilación e implantación, ubicación del repositorio | Implementación, integración |
| **05_Pruebas** | Planes, escenarios y resultados de pruebas, lista de defectos | Verificación, validación |
| **06_Transición** | Planes de transición e implantación, migración de datos, formación de usuarios | Transición |
| **07_Operación y mantenimiento** | Manual de operación, registro de incidencias, solicitudes de cambio y mejora | Operación, mantenimiento |
| **99_Archivo** | Versiones antiguas, lo que ya no se usa | — |

## Para mantenerlo en orden

- Cuando cambien los requisitos, pon la nueva versión en 01 (fecha y número v) y mueve la anterior a 99_Archivo.
- Anotar qué requisitos comprueba cada resultado de prueba facilita la trazabilidad.

## Cuándo encaja mejor otro estilo

- Centrado en cronograma e informes, para cualquier sector → **Gestión de proyectos (PMBOK, 5 fases)**
- Trabajo externalizado por empresa cliente → **Trabajo por cliente y proyecto**
''',
'cde-project': r'''# Cómo usar Información de construcción y diseño (ISO 19650 CDE)

> Planos, modelos y documentos se dividen según los cuatro estados del entorno común de datos (CDE) de ISO 19650, la norma internacional de gestión de la información en construcción (en curso → compartido → publicado → archivo). La carpeta te dice de un vistazo en qué estado está cada archivo.

## Qué va en cada carpeta

| Carpeta | Qué va dentro |
|---|---|
| **01_En curso** (WIP) | Lo que tu equipo aún está editando. No se ve fuera del equipo |
| **02_Compartido** (Shared) | Lo publicado para otras disciplinas y equipos para coordinar y revisar |
| **03_Publicado** (Published) | Versiones finales aprobadas que pueden usar la obra o el cliente |
| **04_Archivo** (Archived) | Sustituido por una revisión nueva o terminado. Se guarda como registro |

## Reglas de nombres

- Patrón: `Proyecto_Disciplina_TipoDoc_Número_Revisión.ext` (p. ej. `NuevoCentroAtencion_Arq_Planta_A101_P02.pdf`)
- Sigue los códigos de revisión de tu empresa o del cliente (p. ej. P01 para revisión, C01 para construcción).

## Para mantenerlo en orden

- Pasar de un estado a otro requiere la revisión y aprobación acordadas. Nada salta de 01 directamente a 03.
- Nunca edites las versiones publicadas. Para cambiar algo, crea una nueva revisión en 01_En curso.

## Cuándo encaja mejor otro estilo

- Un proyecto de trabajo normal sin planos → **Gestión de proyectos (PMBOK, 5 fases)**
''',
}
