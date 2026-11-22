# Cómo usar Documentos de desarrollo de sistemas TI (ISO/IEC/IEEE 12207)

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
