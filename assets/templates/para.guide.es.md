# Cómo usar PARA

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
