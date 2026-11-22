# Cómo usar Etapas del documento

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
