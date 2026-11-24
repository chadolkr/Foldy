# Cómo usar TIER Protocol 4.0

> La especificación de carpetas de investigación de Project TIER (Teaching Integrity in Empirical Research). Los datos originales quedan intactos y todos los cambios se hacen con scripts, así que **cualquiera que ejecute los scripts obtiene los mismos resultados**. Encaja en investigación estadística y empírica y en paquetes de replicación para revistas.

## Qué va en cada carpeta

| Carpeta | Qué va |
|---|---|
| **Data/InputData** | Los datos originales que recibiste. No los modifiques nunca |
| **Data/InputData/Metadata** | Guía de fuentes (dónde y cuándo los obtuviste) y libros de códigos |
| **Data/IntermediateData** | Datos generados a mitad del procesamiento |
| **Data/AnalysisData** | Datos finales de análisis y el apéndice de datos |
| **Scripts/ProcessingScripts** | Scripts que convierten los originales en datos de análisis |
| **Scripts/DataAppendixScripts** | Scripts que generan el apéndice de datos (resumen de variables) |
| **Scripts/AnalysisScripts** | Scripts que generan tablas, figuras y resultados |
| **Output/DataAppendixOutput** | Salida del apéndice de datos |
| **Output/Results** | Resultados del estudio como tablas y figuras |

Arriba del todo van el **Readme** (cómo leer y ejecutar todo) y el **informe** (tu artículo). El **script maestro** que ejecuta todos los scripts en orden va en Scripts.

## Árbol de ejemplo

```
WageStudy/
├─ Readme.txt
├─ Report.pdf
├─ Data/
│  ├─ InputData/
│  │  ├─ survey2025.csv
│  │  └─ Metadata/
│  ├─ IntermediateData/
│  └─ AnalysisData/
├─ Scripts/
│  ├─ MasterScript.R
│  ├─ ProcessingScripts/
│  ├─ DataAppendixScripts/
│  └─ AnalysisScripts/
└─ Output/
   ├─ DataAppendixOutput/
   └─ Results/
```

## Reglas de nombres

| Qué | Regla | Ejemplo |
|---|---|---|
| Carpetas y archivos | Sin espacios; cada palabra con mayúscula inicial | `ProcessingSurveyData.R` |

Los nombres de carpeta quedan en inglés tal como los define la especificación, para que revisores y coautores los reconozcan enseguida.

## Para mantenerlo en orden

- No abras ni modifiques los archivos de InputData. Cualquier cambio se hace con un script.
- Debes poder borrar Output en cualquier momento y regenerarlo con el script maestro.
- Al publicar, comprime la carpeta entera como paquete de replicación.

## Cuándo encaja mejor otro estilo

- Investigación basada en leer bibliografía y escribir manuscritos → **Proyecto de investigación y estudio**
- Creas modelos de aprendizaje automático → **Cookiecutter Data Science**

Fuente: Project TIER, "TIER Protocol 4.0", https://www.projecttier.org/tier-protocol/protocol-4-0/root/
