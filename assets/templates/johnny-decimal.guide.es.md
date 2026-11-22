# Cómo usar Johnny.Decimal

> Cada lugar lleva un número. Dices "22.04" y su ubicación queda clara. Ideal si tienes mucho material y vuelves a buscar a menudo lo mismo.

## Tres niveles

| Nivel | Aspecto | Significado | Límite |
|---|---|---|---|
| **Área** | `10-19`, `20-29` | Un gran tema. Cada decena es un área | hasta 10 |
| **Categoría** | `11`, `12`, `21` | Una caja dentro de un área, numerada en su rango | hasta 10 por área |
| **ID** | `11.01`, `22.04` | El lugar real donde va el material | 01–99 por categoría |

## Qué va dentro / qué no

- Las **carpetas de área** solo contienen carpetas de categoría. No pongas archivos directamente.
- Las **carpetas de categoría** contienen carpetas de ID (`11.01 Nombre`) o archivos con número de ID.
- **00-09 Sistema** solo guarda la lista de números (el índice) y notas sobre el propio sistema.
- **Nunca más de tres niveles.** Si quieres dividir más, toma un nuevo número de ID.

## Mantener la lista de números

La lista de números (`00.00 Lista de números.md`) es el corazón del método. Escribe una línea cada vez que crees un número nuevo y sabrás qué hay dónde sin abrir carpetas, y nunca usarás dos veces el mismo número.

## Árbol de ejemplo

```
00-09 Sistema/
└─ 00 Índice/
   └─ 00.00 Lista de números.md
10-19 Vida/
├─ 11 Hogar/
│  └─ 11.01 Contrato de alquiler/
└─ 12 Coche/
20-29 Dinero/
├─ 22 Impuestos/
│  └─ 22.04 2026 Declaración anual/
└─ 23 Seguros/
```

## Reglas de nombres

- Patrón: `Número Contenido.ext` — el número es la ubicación.
- P. ej. `11.01 Póliza seguro coche.pdf`, `22.04 2026 Declaración anual.xlsx`
- Un espacio entre número y nombre. Los números siempre con dos cifras (`11.01`, `11.10`).

## Para mantenerlo en orden

- No llenes todas las cajas al principio; empieza con 3–5 áreas y unas pocas categorías.
- Si una categoría va a pasar de 10, es señal de que toca dividir el área.
- Nunca reutilices números. Mueve los ID que no uses a una categoría de archivo (p. ej. `39 Archivo`).

## Cuándo encaja mejor otro estilo

- Gestionar números te resulta pesado y solo quieres dividir por "¿está terminado?" → **PARA**
- Fotos y vídeos donde importa el orden por fecha → **Archivo de fotos y vídeos**
