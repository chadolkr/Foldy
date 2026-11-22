# Cómo usar Trabajo por cliente y proyecto

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
