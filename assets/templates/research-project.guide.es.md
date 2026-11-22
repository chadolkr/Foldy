# Cómo usar Proyecto de investigación y estudio

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
