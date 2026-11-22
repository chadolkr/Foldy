# Cómo usar Información de construcción y diseño (ISO 19650 CDE)

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
