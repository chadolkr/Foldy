# How to use Johnny.Decimal

> Every place gets a number. Say "22.04" and its location is settled. Great when you have lots of material and often look up the same things again.

## Three levels

| Level | Looks like | Meaning | Limit |
|---|---|---|---|
| **Area** | `10-19`, `20-29` | A big topic. One range of ten is one area | up to 10 |
| **Category** | `11`, `12`, `21` | A box inside an area, numbered within the area's range | up to 10 per area |
| **ID** | `11.01`, `22.04` | The actual place where material goes | 01–99 per category |

## What goes in / what doesn't

- **Area folders** contain only category folders. Don't put files there directly.
- **Category folders** contain ID folders (`11.01 Name`) or files with an ID number.
- **00-09 System** holds only the number list (the index document) and notes about the system itself.
- **Never go deeper than three levels.** If you want to split further, take a new ID number.

## Keeping the number list

The number list (`00.00 Number list.md`) is the heart of this method. Write one line every time you create a new number, and you'll know what's where without opening folders, and you'll never use the same number twice.

## Example tree

```
00-09 System/
└─ 00 Index/
   └─ 00.00 Number list.md
10-19 Life/
├─ 11 Home/
│  └─ 11.01 Lease agreement/
└─ 12 Car/
20-29 Money/
├─ 22 Taxes/
│  └─ 22.04 2026 Year-end tax/
└─ 23 Insurance/
```

## Naming rules

- Pattern: `Number Content.ext` — the number is the location.
- E.g. `11.01 Car insurance policy.pdf`, `22.04 2026 Year-end tax.xlsx`
- One space between number and name. Numbers always have two digits (`11.01`, `11.10`).

## Keeping it tidy

- Don't fill every box at the start; begin with 3–5 areas and a few categories.
- If a category looks like it'll exceed 10, it's a sign to split the area.
- Never reuse numbers. Move unused IDs to an archive category (e.g. `39 Archive`).

## When another style fits better

- Managing numbers feels like a chore and you just want to split by "is it finished?" → **PARA**
- Photos and videos where date order matters → **Photo & video archive**
