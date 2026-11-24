# Cómo usar Cookiecutter Data Science

> La estructura de proyectos de ciencia de datos de DrivenData. Los datos van por etapas (raw → interim → processed) y los cuadernos, modelos e informes tienen cada uno su caja. Es la estructura más usada en ciencia de datos.

## Qué va en cada carpeta

| Carpeta | Qué va |
|---|---|
| **data/raw** | Los datos originales que recibiste. No los modifiques |
| **data/interim** | Datos transformados a medias |
| **data/processed** | Conjuntos de datos finales para modelar |
| **data/external** | Datos de fuentes externas |
| **docs** | Documentación del proyecto |
| **models** | Modelos entrenados y predicciones |
| **notebooks** | Cuadernos de Jupyter |
| **references** | Material de referencia como diccionarios de datos y manuales |
| **reports** | Informes de análisis (HTML, PDF) |
| **reports/figures** | Figuras para los informes |

Foldy solo crea las carpetas de arriba y un README.md. Para crear también la carpeta de código y los archivos de configuración (pyproject.toml, etc.), usa la herramienta oficial `ccds`. El código se gestiona con Git.

## Árbol de ejemplo

```
churn-model/
├─ README.md
├─ data/
│  ├─ raw/
│  ├─ interim/
│  ├─ processed/
│  └─ external/
├─ models/
├─ notebooks/
│  └─ 1.0-jqp-initial-data-exploration.ipynb
├─ references/
└─ reports/
   └─ figures/
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Cuadernos | número-iniciales-descripción | `1.0-jqp-initial-data-exploration.ipynb` |

Los nombres de carpeta quedan en inglés tal como en la estructura, porque el código usa estas rutas directamente.

## Para mantenerlo en orden

- No modifiques data/raw. Haz cada transformación en código para poder regenerarla.
- data y models pesan mucho, así que déjalos fuera de Git.
- Los cuadernos son para explorar. Pasa el código que uses a menudo a la carpeta de código.

## Cuándo encaja mejor otro estilo

- Un paquete de replicación para un artículo estadístico → **TIER Protocol 4.0 (investigación reproducible)**
- Datos de neuroimagen → **BIDS (estándar de datos de neuroimagen)**

Fuente: DrivenData, "Cookiecutter Data Science", https://cookiecutter-data-science.drivendata.org/
