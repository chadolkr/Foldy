# Cómo usar BIDS

> BIDS (Brain Imaging Data Structure) es el estándar internacional para organizar datos de investigación en neuroimagen. Las carpetas van participante → sesión → tipo de dato, y los nombres de archivo siguen reglas fijas. Así, herramientas de análisis como fMRIPrep y repositorios públicos (OpenNeuro) leen los datos directamente.

## Qué va en cada carpeta

| Carpeta o archivo | Qué va |
|---|---|
| **dataset_description.json** | Nombre del conjunto de datos y versión de BIDS (obligatorio) |
| **participants.tsv** | Lista de participantes (un participant_id por línea) |
| **README.md** | Descripción del conjunto de datos |
| **sub-01** | Un participante. Cada participante tiene su carpeta sub-<etiqueta> |
| **sub-01/ses-01** | Una sesión de registro. Se puede omitir si solo hay una |
| **anat, func …** | Tipo de dato. Solo los nombres fijados: anat (resonancia anatómica), func (resonancia funcional), dwi (difusión), fmap (mapas de campo), eeg, meg, pet, etc. |
| **sourcedata** | Originales antes de convertir (DICOM, etc.) |
| **derivatives** | Resultados de procesamiento y análisis |
| **code** | Scripts de conversión y análisis |

## Árbol de ejemplo

```
MyStudy/
├─ dataset_description.json
├─ participants.tsv
├─ README.md
├─ sub-01/
│  └─ ses-01/
│     ├─ anat/
│     │  └─ sub-01_ses-01_T1w.nii.gz
│     └─ func/
│        └─ sub-01_ses-01_task-rest_bold.nii.gz
├─ sourcedata/
├─ derivatives/
└─ code/
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Archivos de datos | sub-<participante>_ses-<sesión>_<tipo> | `sub-01_ses-01_T1w.nii.gz` |
| Archivos con tarea | sub-<participante>_ses-<sesión>_task-<tarea>_<tipo> | `sub-01_ses-01_task-rest_bold.nii.gz` |

Los nombres de carpetas y archivos quedan en inglés tal como en el estándar. Las herramientas no leen otros nombres.

## Para mantenerlo en orden

- Para cada participante nuevo, copia la carpeta sub-01, renómbrala sub-02 y añade una línea a participants.tsv.
- Un conversor como dcm2bids o HeuDiConv comete menos errores que renombrar a mano.
- Al terminar, revisa todo con el BIDS Validator (https://bids-standard.github.io/bids-validator/).

## Cuándo encaja mejor otro estilo

- Datos estadísticos en lugar de neuroimagen → **TIER Protocol 4.0 (investigación reproducible)**
- Investigación basada en bibliografía y manuscritos → **Proyecto de investigación y estudio**

Fuente: BIDS Specification, https://bids-specification.readthedocs.io/ , https://bids.neuroimaging.io/
