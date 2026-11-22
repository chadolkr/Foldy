# Cómo usar Archivo por fechas

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
